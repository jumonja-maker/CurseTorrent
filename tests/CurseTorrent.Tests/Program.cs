using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CurseTorrent.Core;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

return await IntegrationSuite.RunAsync();

internal static class IntegrationSuite
{
    private static int passed;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(22);

    public static async Task<int> RunAsync()
    {
        var timer = Stopwatch.StartNew();
        var root = Path.Combine(Path.GetTempPath(), "cursetorrent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ExerciseAsync(root).WaitAsync(TimeSpan.FromSeconds(85));
            Console.WriteLine($"PASS: {passed} checks; real loopback BitTorrent transfer, {timer.Elapsed.TotalSeconds:F1}s.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL after {passed} checks ({timer.Elapsed.TotalSeconds:F1}s): {error}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* The OS may still be releasing a test-only file handle. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ExerciseAsync(string root)
    {
        using var tracker = new LocalTracker();
        var seedDirectory = Path.Combine(root, "seed");
        Directory.CreateDirectory(seedDirectory);
        await WriteFixtureAsync(Path.Combine(seedDirectory, "alpha.bin"), 2 * 1024 * 1024, 311);
        await WriteFixtureAsync(Path.Combine(seedDirectory, "beta.bin"), 768 * 1024, 809);
        var torrentPath = Path.Combine(root, "fixture.torrent");
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024, Announce = tracker.AnnounceUrl };
        await creator.CreateAsync(new TorrentFileSource(seedDirectory) { TorrentName = "FixturePayload" }, torrentPath);
        var torrent = await Torrent.LoadAsync(torrentPath);
        var port = ReserveAvailablePort();
        using var seedEngine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "seed-cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
            DhtEndPoint = null,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            MaximumUploadRate = 64 * 1024
        }.ToSettings());
        var seeder = await seedEngine.AddAsync(torrentPath, seedDirectory, new TorrentSettingsBuilder
        {
            CreateContainingDirectory = false,
            AllowDht = false,
            AllowPeerExchange = false
        }.ToSettings());
        await seeder.StartAsync();
        await UntilAsync(() => seeder.State == TorrentState.Seeding, "fixture seeder hash check", () => seeder.Error?.Exception.Message);
        await UntilAsync(() => tracker.HasPeer(torrent), "seeder registered with loopback tracker");
        // MonoTorrent initializes the rate limiter on its periodic tick. Let the seeder
        // settle before connecting so a fast loopback burst cannot complete the fixture.
        await Task.Delay(1200);
        Pass("Local seeder validates real deterministic payload");

        try
        {
            await ExerciseServiceAsync(root, seedDirectory, torrentPath, torrent, port, seedEngine, tracker);
        }
        finally
        {
            await seedEngine.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task ExerciseServiceAsync(string root, string seedDirectory, string torrentPath,
        Torrent torrent, int port, ClientEngine seedEngine, LocalTracker tracker)
    {
        var appDirectory = Path.Combine(root, "app");
        var downloads = Path.Combine(root, "downloads");
        var peer = new Uri($"ipv4://127.0.0.1:{port}");
        var originalMetadata = await File.ReadAllBytesAsync(torrentPath);
        string id;
        string firstDownloadedFile;

        await using (var service = new TorrentService(appDirectory, offlineMode: true))
        {
            await service.InitializeAsync();
            var invalidInputs = new[] { "", "not-a-torrent", "magnet:?xt=urn:btih:invalid" };
            foreach (var input in invalidInputs)
                await RejectAsync(() => service.AddAsync(new AddTorrentRequest(input, downloads)), "Invalid input rejected");
            Require(service.GetSnapshots().Count == 0, "Malformed imports do not add phantom transfers");

            await RejectAsync(() => service.UpdateSettingsAsync(service.Settings with { DownloadLimitKiB = -1 }), "Negative download limit rejected");
            await RejectAsync(() => service.UpdateSettingsAsync(service.Settings with { UploadLimitKiB = int.MaxValue }), "Overflowing upload limit rejected");
            await RejectAsync(() => service.UpdateSettingsAsync(service.Settings with { ListenPort = 65536 }), "Invalid port rejected");
            await service.UpdateSettingsAsync(service.Settings with
            {
                DownloadDirectory = downloads,
                DownloadLimitKiB = 64,
                ResumeOnStartup = true,
                EnablePortForwarding = false
            });

            var preview = await service.InspectAsync(torrentPath);
            Require(!preview.IsMagnet && preview.Name == torrent.Name && preview.Files.Count == 2,
                "Torrent preview reads actual metadata and file list");
            var priorities = new Dictionary<string, FilePriority>
            {
                ["alpha.bin"] = FilePriority.High,
                ["beta.bin"] = FilePriority.Skip
            };
            id = await service.AddAsync(new AddTorrentRequest(torrentPath, downloads, StartImmediately: false, priorities));
            var paused = Snapshot(service, id);
            Require(paused.HasMetadata && paused.Files.Count == 2, "Paused import has metadata");
            Require(paused.Files.Single(f => f.Path == "beta.bin").Priority == FilePriority.Skip,
                "Import applies selective file download before transfer");
            await RejectAsync(() => service.AddAsync(new AddTorrentRequest(torrentPath, downloads)), "Duplicate info hash rejected");
            Require(service.GetSnapshots().Count == 1, "Duplicate import keeps one transfer");

            await service.ResumeAsync(id);
            await service.AddPeerAsync(id, peer);
            await UntilAsync(() => Snapshot(service, id).Progress > 0, "partial payload received", () => Snapshot(service, id).ErrorMessage);
            await service.PauseAsync(id);
            var partial = Snapshot(service, id);
            Require(partial.State == DownloadState.Paused && partial.Progress > 0 && partial.Progress < 100,
                $"Pause interrupts an actual partial download and exposes paused state (state={partial.State}, progress={partial.Progress:F2}%)");
            Require(partial.SelectedSizeBytes == 2 * 1024 * 1024,
                "Selected payload size excludes skipped files");
            await service.ShutdownAsync();
        }

        // The original import must not be necessary for resuming an installed client's task.
        File.Delete(torrentPath);

        await using (var service = new TorrentService(appDirectory, offlineMode: true))
        {
            await service.InitializeAsync();
            Require(service.StartupWarnings.Count == 0 && service.GetSnapshots().Count == 1,
                "Session restores after the original .torrent file is deleted");
            Require(service.Settings.DownloadLimitKiB == 64 && service.Settings.DownloadDirectory == downloads,
                "Settings survive an application restart");
            var restored = Snapshot(service, id);
            Require(restored.State == DownloadState.Paused, "Restart preserves paused intent");
            Require(restored.Files.Single(f => f.Path == "alpha.bin").Priority == FilePriority.High &&
                    restored.Files.Single(f => f.Path == "beta.bin").Priority == FilePriority.Skip,
                "Restart preserves per-file priorities");
            await service.UpdateSettingsAsync(service.Settings with { DownloadLimitKiB = 0 });
            await seedEngine.UpdateSettingsAsync(new EngineSettingsBuilder(seedEngine.Settings) { MaximumUploadRate = 0 }.ToSettings());
            await service.ResumeAsync(id);
            await service.AddPeerAsync(id, peer);
            await UntilAsync(() => Snapshot(service, id).Progress >= 99.999, "selected payload completes", () => Snapshot(service, id).ErrorMessage);
            await service.PauseAsync(id);
            firstDownloadedFile = FindPayload(downloads, "alpha.bin");
            await RequireSameHashAsync(Path.Combine(seedDirectory, "alpha.bin"), firstDownloadedFile,
                "Resumed selected payload matches source SHA-256");
            await service.RemoveAsync(id);
            Require(service.GetSnapshots().Count == 0 && File.Exists(firstDownloadedFile),
                "Removing a transfer retains downloaded data");

            // Exercise an uncached magnet: Remove intentionally retains metadata, so remove
            // only this unregistered fixture's cached .torrent copies before the new import.
            foreach (var cachedPath in Directory.EnumerateFiles(appDirectory, "*.torrent", SearchOption.AllDirectories))
            {
                var cached = await Torrent.LoadAsync(cachedPath);
                if (cached.InfoHashes.Equals(torrent.InfoHashes)) File.Delete(cachedPath);
            }

            var magnet = tracker.Magnet(torrent) + "&dn=FixturePayload";
            var magnetPreview = await service.InspectAsync(magnet);
            Require(magnetPreview.IsMagnet, "Magnet preview validates the link");
            var magnetDownloads = Path.Combine(root, "magnet-downloads");
            id = await service.AddAsync(new AddTorrentRequest(magnet, magnetDownloads, StartImmediately: false));
            Require(!Snapshot(service, id).HasMetadata,
                "Cold magnet begins without cached metadata");
            await service.ResumeAsync(id);
            await service.PauseAsync(id);
            Require(Snapshot(service, id).State == DownloadState.Paused,
                "Pause also stops magnet metadata acquisition");
            await service.ResumeAsync(id);
            // The engine's private metadata-only manager discovers peers through our local
            // tracker. Once metadata is validated, the public download manager is available.
            await UntilAsync(() => Snapshot(service, id).HasMetadata, "magnet metadata received", () => Snapshot(service, id).ErrorMessage);
            await service.AddPeerAsync(id, peer);
            await UntilAsync(() => Snapshot(service, id).Progress >= 99.999, "magnet payload completes", () => Snapshot(service, id).ErrorMessage);
            await service.PauseAsync(id);
            await RequireSameHashAsync(Path.Combine(seedDirectory, "alpha.bin"), FindPayload(magnetDownloads, "alpha.bin"),
                "Magnet download alpha matches source SHA-256");
            await RequireSameHashAsync(Path.Combine(seedDirectory, "beta.bin"), FindPayload(magnetDownloads, "beta.bin"),
                "Magnet download beta matches source SHA-256");
            await service.RecheckAsync(id);
            await UntilAsync(() => Snapshot(service, id).State != DownloadState.Checking, "completed file recheck", () => Snapshot(service, id).ErrorMessage);
            Require(Snapshot(service, id).Progress >= 99.999, "Recheck verifies completed on-disk content");

            // Keep two independent records. Corrupt only the first one's cached metadata below.
            await service.RecheckAsync(id);
            await service.RemoveAsync(id);
            Require(service.GetSnapshots().Count == 0,
                "Removing immediately after requesting a recheck is safe");
            await File.WriteAllBytesAsync(torrentPath, originalMetadata);
            id = await service.AddAsync(new AddTorrentRequest(torrentPath, magnetDownloads, StartImmediately: false));
            File.Delete(torrentPath);
            var recoverySeed = Path.Combine(root, "recovery-seed");
            Directory.CreateDirectory(recoverySeed);
            await WriteFixtureAsync(Path.Combine(recoverySeed, "recovery.bin"), 64 * 1024, 123);
            var recoveryPath = Path.Combine(root, "recovery.torrent");
            await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(recoverySeed), recoveryPath);
            await service.AddAsync(new AddTorrentRequest(recoveryPath, Path.Combine(root, "recovery-downloads"), StartImmediately: false));
            await service.ShutdownAsync();
        }

        // Cache locations and manifest schema are deliberately not hardcoded in these tests.
        var corrupted = 0;
        foreach (var path in Directory.EnumerateFiles(appDirectory, "*.torrent", SearchOption.AllDirectories))
        {
            try
            {
                var cached = await Torrent.LoadAsync(path);
                if (!cached.InfoHashes.Equals(torrent.InfoHashes)) continue;
                await File.WriteAllTextAsync(path, "corrupted test metadata");
                corrupted++;
            }
            catch (TorrentException) { }
            catch (IOException) { }
        }
        Require(corrupted > 0, "Fixture identifies cached metadata without private schema assumptions");

        await using (var service = new TorrentService(appDirectory, offlineMode: true))
        {
            await service.InitializeAsync();
            Require(service.GetSnapshots().Any(s => s.Name.Contains("recovery", StringComparison.OrdinalIgnoreCase)),
                "One damaged cached torrent does not prevent restoring another transfer");
            Require(service.StartupWarnings.Count > 0,
                "Damaged imported metadata is reported to the user during recovery");
            Require(File.Exists(firstDownloadedFile), "Session restoration never deletes retained downloads");
            foreach (var snapshot in service.GetSnapshots().ToArray())
                await service.RemoveAsync(snapshot.Id);
            await service.ShutdownAsync();
        }

        await using (var service = new TorrentService(appDirectory, offlineMode: true))
        {
            await service.InitializeAsync();
            Require(service.GetSnapshots().Count == 0, "Removed transfers stay removed after restart");
        }

        await ExerciseUploadPersistenceAsync(root, seedDirectory, torrent, originalMetadata);
        await ExercisePathCollisionsAsync(root);
        await ExerciseZeroByteMagnetAsync(root);
    }

    private static async Task ExerciseZeroByteMagnetAsync(string root)
    {
        using var tracker = new LocalTracker();
        var fixture = Path.Combine(root, "zero-fixture");
        var ownerDirectory = Path.Combine(fixture, "owner");
        var incomingDirectory = Path.Combine(fixture, "incoming");
        Directory.CreateDirectory(ownerDirectory);
        Directory.CreateDirectory(incomingDirectory);
        await WriteFixtureAsync(Path.Combine(ownerDirectory, "shared.bin"), 128 * 1024, 414);
        await WriteFixtureAsync(Path.Combine(ownerDirectory, "owner-anchor.bin"), 16 * 1024, 415);
        await File.WriteAllBytesAsync(Path.Combine(incomingDirectory, "shared.bin"), []);
        await WriteFixtureAsync(Path.Combine(incomingDirectory, "incoming-anchor.bin"), 16 * 1024, 416);
        var ownerTorrent = Path.Combine(fixture, "owner.torrent");
        var incomingTorrent = Path.Combine(fixture, "incoming.torrent");
        await new TorrentCreator(TorrentType.V1Only).CreateAsync(
            new TorrentFileSource(ownerDirectory) { TorrentName = "ZeroPayload" }, ownerTorrent);
        await new TorrentCreator(TorrentType.V1Only) { Announce = tracker.AnnounceUrl }.CreateAsync(
            new TorrentFileSource(incomingDirectory) { TorrentName = "ZeroPayload" }, incomingTorrent);
        var incomingMetadata = await Torrent.LoadAsync(incomingTorrent);
        var downloadRoot = Path.Combine(root, "zero-protected");
        var protectedDirectory = Path.Combine(downloadRoot, "ZeroPayload");
        Directory.CreateDirectory(protectedDirectory);
        var protectedFile = Path.Combine(protectedDirectory, "shared.bin");
        File.Copy(Path.Combine(ownerDirectory, "shared.bin"), protectedFile);
        var port = ReserveAvailablePort();
        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(fixture, "seed-cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
            DhtEndPoint = null,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false
        }.ToSettings());
        var manager = await engine.AddAsync(incomingTorrent, incomingDirectory,
            new TorrentSettingsBuilder { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
        await manager.StartAsync();
        await UntilAsync(() => manager.State == TorrentState.Seeding && tracker.HasPeer(incomingMetadata),
            "zero-byte metadata fixture seeds", () => manager.Error?.Exception.Message);
        try
        {
            await using var service = new TorrentService(Path.Combine(root, "zero-app"), offlineMode: true);
            await service.InitializeAsync();
            await service.AddAsync(new AddTorrentRequest(ownerTorrent, downloadRoot, StartImmediately: false));
            var id = await service.AddAsync(new AddTorrentRequest(tracker.Magnet(incomingMetadata), downloadRoot));
            await UntilAsync(() => Snapshot(service, id).State == DownloadState.Error,
                "zero-byte magnet collision rejected before creating files");
            Require(!string.IsNullOrWhiteSpace(Snapshot(service, id).ErrorMessage),
                "Zero-byte magnet collision is rejected before entering payload mode");
            await RequireSameHashAsync(Path.Combine(ownerDirectory, "shared.bin"), protectedFile,
                "Zero-byte magnet cannot truncate an existing nonempty payload");
        }
        finally
        {
            await engine.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task ExercisePathCollisionsAsync(string root)
    {
        using var tracker = new LocalTracker();
        var fixtureRoot = Path.Combine(root, "collision-fixtures");
        var aliasDirectory = Path.Combine(fixtureRoot, "aliases");
        Directory.CreateDirectory(aliasDirectory);
        await WriteFixtureAsync(Path.Combine(aliasDirectory, "payload.bin"), 32 * 1024, 4);
        await WriteFixtureAsync(Path.Combine(aliasDirectory, "payload.bin.!mt"), 32 * 1024, 5);
        var aliasTorrent = Path.Combine(fixtureRoot, "aliases.torrent");
        await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(aliasDirectory), aliasTorrent);

        var ownerDirectory = Path.Combine(fixtureRoot, "owner");
        var conflictingDirectory = Path.Combine(fixtureRoot, "conflict");
        var directConflictDirectory = Path.Combine(fixtureRoot, "direct-conflict");
        Directory.CreateDirectory(ownerDirectory);
        Directory.CreateDirectory(conflictingDirectory);
        Directory.CreateDirectory(directConflictDirectory);
        await WriteFixtureAsync(Path.Combine(ownerDirectory, "shared.bin"), 128 * 1024, 331);
        await WriteFixtureAsync(Path.Combine(conflictingDirectory, "shared.bin"), 64 * 1024, 990);
        await WriteFixtureAsync(Path.Combine(directConflictDirectory, "shared.bin"), 32 * 1024, 777);
        var ownerTorrent = Path.Combine(fixtureRoot, "owner.torrent");
        var conflictTorrent = Path.Combine(fixtureRoot, "conflict.torrent");
        var directConflictTorrent = Path.Combine(fixtureRoot, "direct-conflict.torrent");
        await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(Path.Combine(ownerDirectory, "shared.bin")), ownerTorrent);
        await new TorrentCreator(TorrentType.V1Only) { Announce = tracker.AnnounceUrl }.CreateAsync(new TorrentFileSource(Path.Combine(conflictingDirectory, "shared.bin")), conflictTorrent);
        await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(Path.Combine(directConflictDirectory, "shared.bin")), directConflictTorrent);
        var conflictMetadata = await Torrent.LoadAsync(conflictTorrent);
        var protectedDirectory = Path.Combine(root, "protected-downloads");
        Directory.CreateDirectory(protectedDirectory);
        var protectedFile = Path.Combine(protectedDirectory, "shared.bin");
        File.Copy(Path.Combine(ownerDirectory, "shared.bin"), protectedFile);
        var port = ReserveAvailablePort();
        using var seeder = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(fixtureRoot, "seed-cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
            DhtEndPoint = null,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false
        }.ToSettings());
        var seedManager = await seeder.AddAsync(conflictTorrent, conflictingDirectory,
            new TorrentSettingsBuilder { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
        await seedManager.StartAsync();
        await UntilAsync(() => seedManager.State == TorrentState.Seeding, "collision fixture seeds", () => seedManager.Error?.Exception.Message);
        await UntilAsync(() => tracker.HasPeer(conflictMetadata), "collision fixture registered with tracker");
        try
        {
            await using var service = new TorrentService(Path.Combine(root, "collision-app"), offlineMode: true);
            await service.InitializeAsync();
            await RejectAsync(() => service.AddAsync(new AddTorrentRequest(aliasTorrent, Path.Combine(root, "alias-downloads"), StartImmediately: false)),
                "A torrent cannot alias another file's .!mt partial path");
            await service.AddAsync(new AddTorrentRequest(ownerTorrent, protectedDirectory, StartImmediately: false));
            await RejectAsync(() => service.AddAsync(new AddTorrentRequest(directConflictTorrent, protectedDirectory, StartImmediately: false)),
                "Two torrent files cannot own the same destination path");

            var magnet = tracker.Magnet(conflictMetadata);
            var conflictId = await service.AddAsync(new AddTorrentRequest(magnet, protectedDirectory));
            await UntilAsync(() => Snapshot(service, conflictId).State == DownloadState.Error,
                "magnet path collision is rejected after metadata arrives");
            Require(!string.IsNullOrWhiteSpace(Snapshot(service, conflictId).ErrorMessage),
                "Magnet collision is reported after real peer-supplied metadata");
            await RequireSameHashAsync(Path.Combine(ownerDirectory, "shared.bin"), protectedFile,
                "Rejected magnet cannot truncate or overwrite another torrent's existing payload");
        }
        finally
        {
            await seeder.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task ExerciseUploadPersistenceAsync(string root, string seedDirectory,
        Torrent torrent, byte[] originalMetadata)
    {
        var appDirectory = Path.Combine(root, "upload-app");
        var payloadRoot = Path.Combine(root, "upload-content");
        var payloadDirectory = Path.Combine(payloadRoot, torrent.Name);
        Directory.CreateDirectory(payloadDirectory);
        foreach (var source in Directory.EnumerateFiles(seedDirectory))
            File.Copy(source, Path.Combine(payloadDirectory, Path.GetFileName(source)));
        var torrentPath = Path.Combine(root, "upload.torrent");
        // Isolate the upload regression: this receiver must obtain payload only from the
        // application, never from the earlier fixture seeder registered with our tracker.
        var trackerless = BEncodedValue.Decode<BEncodedDictionary>(originalMetadata);
        trackerless.Remove("announce");
        trackerless.Remove("announce-list");
        await File.WriteAllBytesAsync(torrentPath, trackerless.Encode());
        var receiverPort = ReserveAvailablePort();
        using var receiverEngine = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(root, "upload-receiver-cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, receiverPort) },
            DhtEndPoint = null,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false
        }.ToSettings());
        var receiver = await receiverEngine.AddAsync(torrentPath, Path.Combine(root, "upload-received"),
            new TorrentSettingsBuilder { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
        string id;
        long uploaded;
        try
        {
            await using (var service = new TorrentService(appDirectory, offlineMode: true))
            {
                await service.InitializeAsync();
                id = await service.AddAsync(new AddTorrentRequest(torrentPath, payloadRoot));
                await UntilAsync(() => Snapshot(service, id).State == DownloadState.Seeding,
                    "application seeds existing complete files", () => Snapshot(service, id).ErrorMessage);
                await receiver.StartAsync();
                await service.AddPeerAsync(id, new Uri($"ipv4://127.0.0.1:{receiverPort}"));
                await UntilAsync(() => receiver.Complete, "application uploads to isolated receiver", () => receiver.Error?.Exception.Message);
                uploaded = Snapshot(service, id).UploadedBytes;
                Require(uploaded >= torrent.Size, "Application uploads real payload to a second local client");
                await service.PauseAsync(id);
                Require(Snapshot(service, id).UploadedBytes >= uploaded,
                    "Pause preserves cumulative upload bytes after the engine resets its monitor");
                await service.ShutdownAsync();
            }
            // Simulate interruption between completing a file and its final rename.
            // The next AddAsync loads fast-resume data before it returns a manager.
            var completePath = Path.Combine(payloadDirectory, "alpha.bin");
            File.Move(completePath, completePath + ".!mt");
            await using (var service = new TorrentService(appDirectory, offlineMode: true))
            {
                await service.InitializeAsync();
                Require(Snapshot(service, id).UploadedBytes >= uploaded,
                    "Cumulative upload bytes survive application restart");
                Require(Snapshot(service, id).State == DownloadState.Paused,
                    "Paused seeding remains paused after restart");
                await UntilAsync(() => File.Exists(completePath), "fast-resume completes interrupted file rename",
                    () => Snapshot(service, id).ErrorMessage);
                await RequireSameHashAsync(Path.Combine(seedDirectory, "alpha.bin"), completePath,
                    "Fast-resume safely restores a completed .!mt file during manager registration");
            }
        }
        finally
        {
            await receiverEngine.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static TorrentSnapshot Snapshot(TorrentService service, string id) =>
        service.GetSnapshots(id).Single(s => s.Id == id);

    private static async Task UntilAsync(Func<bool> condition, string operation, Func<string?>? error = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            var detail = error?.Invoke();
            if (!string.IsNullOrEmpty(detail)) throw new InvalidOperationException($"{operation}: {detail}");
            if (timer.Elapsed > TestTimeout) throw new TimeoutException($"Timed out waiting for {operation}.");
            await Task.Delay(100);
        }
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or FormatException or TorrentException)
        {
            Pass(message);
            return;
        }
        throw new InvalidOperationException(message + ": operation unexpectedly succeeded.");
    }

    private static async Task WriteFixtureAsync(string path, int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task RequireSameHashAsync(string expected, string actual, string message)
    {
        await using var source = File.OpenRead(expected);
        await using var target = File.OpenRead(actual);
        var sourceHash = await SHA256.HashDataAsync(source);
        var targetHash = await SHA256.HashDataAsync(target);
        Require(sourceHash.SequenceEqual(targetHash), message);
    }

    private static string FindPayload(string directory, string filename)
    {
        var candidates = Directory.EnumerateFiles(directory, filename, SearchOption.AllDirectories).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException($"Expected exactly one {filename}, found {candidates.Length}.");
        return candidates[0];
    }

    private static int ReserveAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Pass(message);
    }

    private static void Pass(string message)
    {
        passed++;
        Console.WriteLine($"  PASS {passed:00}: {message}");
    }
}
