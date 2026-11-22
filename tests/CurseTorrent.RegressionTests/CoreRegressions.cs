using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using CurseTorrent.Core;
using MonoTorrent;
using MonoTorrent.Client;

internal static class CoreRegressions
{
    public static async Task RunAsync(string root)
    {
        await ChangedPayloadAsync(Path.Combine(root, "payload"));
        await ResumeFingerprintsAsync(Path.Combine(root, "resume-hints"));
        await SharedPieceFingerprintsAsync(Path.Combine(root, "shared-piece"));
        await SessionRecoveryAsync(Path.Combine(root, "session"));
        await SaveFailureAsync(Path.Combine(root, "writes"));
        await StateWriteQuotaAsync(Path.Combine(root, "state-quota"));
        await MetadataCancellationAsync(Path.Combine(root, "metadata"));
        await LifecycleAsync(Path.Combine(root, "lifecycle"));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("  PASS " + message);
    }

    private static async Task UntilAsync(Func<bool> condition, string description, int seconds = 8)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds >= seconds) throw new TimeoutException(description);
            await Task.Delay(20);
        }
    }

    private static async Task<(string Metadata, string PayloadDirectory, string PayloadPath, byte[] Bytes)> FixtureAsync(string root)
    {
        var payloadDirectory = Path.Combine(root, "payload");
        Directory.CreateDirectory(payloadDirectory);
        var payloadPath = Path.Combine(payloadDirectory, "sample.bin");
        var bytes = new byte[128 * 1024];
        new Random(614).NextBytes(bytes);
        await File.WriteAllBytesAsync(payloadPath, bytes);
        var metadata = Path.Combine(root, "sample.torrent");
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 };
        await creator.CreateAsync(new TorrentFileSource(payloadPath), metadata);
        return (metadata, payloadDirectory, payloadPath, bytes);
    }

    private static async Task ChangedPayloadAsync(string root)
    {
        var fixture = await FixtureAsync(root);
        foreach (string mutation in new[] { "truncate", "same-length", "delete", "append" })
        {
            await File.WriteAllBytesAsync(fixture.PayloadPath, fixture.Bytes);
            string state = Path.Combine(root, "state-" + mutation);
            string id;
            await using (var service = new TorrentService(state, offlineMode: true))
            {
                await service.InitializeAsync();
                id = await service.AddAsync(new(fixture.Metadata, fixture.PayloadDirectory, false));
                await service.RecheckAsync(id);
                await UntilAsync(() => service.GetSnapshots().Single() is { State: DownloadState.Paused, Progress: 100 }, "initial verification");
            }
            switch (mutation)
            {
                case "truncate": await File.WriteAllBytesAsync(fixture.PayloadPath, new byte[] { 1 }); break;
                case "same-length": await File.WriteAllBytesAsync(fixture.PayloadPath, new byte[fixture.Bytes.Length]); break;
                case "delete": File.Delete(fixture.PayloadPath); break;
                case "append": await File.WriteAllBytesAsync(fixture.PayloadPath, fixture.Bytes.Concat(new byte[] { 1 }).ToArray()); break;
            }
            await using var restored = new TorrentService(state, offlineMode: true);
            await restored.InitializeAsync();
            await UntilAsync(() => restored.GetSnapshots().Single().State != DownloadState.Checking, "restored payload verification");
            var snapshot = restored.GetSnapshots().Single();
            Check(snapshot.Progress < 100 && snapshot.DownloadedBytes < fixture.Bytes.Length, mutation + " is not accepted as complete after restart");
            if (mutation != "append")
            {
                await restored.ResumeAsync(id);
                await UntilAsync(() => restored.GetSnapshots().Single().State is DownloadState.Downloading or DownloadState.Error, "resume damaged payload");
                Check(restored.GetSnapshots().Single().State != DownloadState.Seeding, mutation + " does not enter seeding");
            }
            else Check(new FileInfo(fixture.PayloadPath).Length == fixture.Bytes.Length + 1, "verification preserves extra trailing bytes");
        }
    }

    private static async Task ResumeFingerprintsAsync(string root)
    {
        foreach (string scenario in new[] { "unchanged", "changed-while-paused", "changed-while-active" })
        {
            var fixture = await FixtureAsync(Path.Combine(root, scenario));
            await using var service = new TorrentService(Path.Combine(root, scenario, "state"), true);
            await service.InitializeAsync();
            string id = await service.AddAsync(new(fixture.Metadata, fixture.PayloadDirectory));
            await UntilAsync(() => service.GetSnapshots().Single().State == DownloadState.Seeding, "verified seeding before pause");
            object? originalHash = HashOperation(service);
            DateTime originalTime = File.GetLastWriteTimeUtc(fixture.PayloadPath);
            if (scenario == "changed-while-active")
            {
                await File.WriteAllBytesAsync(fixture.PayloadPath, new byte[fixture.Bytes.Length]);
                File.SetLastWriteTimeUtc(fixture.PayloadPath, originalTime.AddSeconds(2));
            }
            await service.PauseAsync(id);
            if (scenario == "changed-while-paused")
            {
                await File.WriteAllBytesAsync(fixture.PayloadPath, new byte[fixture.Bytes.Length]);
                File.SetLastWriteTimeUtc(fixture.PayloadPath, originalTime.AddSeconds(2));
            }
            await service.ResumeAsync(id);
            if (scenario == "unchanged")
            {
                await UntilAsync(() => service.GetSnapshots().Single().State == DownloadState.Seeding, "unchanged fast resume");
                Check(ReferenceEquals(originalHash, HashOperation(service)), "unchanged verified files resume without another full hash check");
            }
            else
            {
                await UntilAsync(() => service.GetSnapshots().Single().State is DownloadState.Downloading or DownloadState.Error, "changed files reverified before resume");
                Check(!ReferenceEquals(originalHash, HashOperation(service)) && service.GetSnapshots().Single().Progress < 100,
                    scenario + " invalidates the fast path; Stop cannot bless a changed file");
            }
        }
    }

    private static object? HashOperation(TorrentService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object sync = typeof(TorrentService).GetField("_entriesLock", flags)!.GetValue(service)!;
        lock (sync)
        {
            var entry = ((IEnumerable)typeof(TorrentService).GetField("_entries", flags)!.GetValue(service)!).Cast<object>().Single();
            return entry.GetType().GetProperty("HashOperation")!.GetValue(entry);
        }
    }

    private static async Task SharedPieceFingerprintsAsync(string root)
    {
        string payloadDirectory = Path.Combine(root, "Boundary");
        Directory.CreateDirectory(payloadDirectory);
        var bytes = new byte[8192];
        new Random(615).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(payloadDirectory, "alpha.bin"), bytes);
        string skippedPath = Path.Combine(payloadDirectory, "beta.bin");
        await File.WriteAllBytesAsync(skippedPath, bytes);
        string metadata = Path.Combine(root, "boundary.torrent");
        await new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }
            .CreateAsync(new TorrentFileSource(payloadDirectory) { TorrentName = "Boundary" }, metadata);
        var priorities = new Dictionary<string, FilePriority>
        {
            ["alpha.bin"] = FilePriority.Normal,
            ["beta.bin"] = FilePriority.Skip
        };
        await using var service = new TorrentService(Path.Combine(root, "state"), true);
        await service.InitializeAsync();
        string id = await service.AddAsync(new(metadata, root, FilePriorities: priorities));
        await UntilAsync(() => service.GetSnapshots().Single().State == DownloadState.Seeding, "shared boundary piece initially verified");
        object? originalHash = HashOperation(service);
        await service.PauseAsync(id);
        DateTime originalTime = File.GetLastWriteTimeUtc(skippedPath);
        await File.WriteAllBytesAsync(skippedPath, new byte[bytes.Length]);
        File.SetLastWriteTimeUtc(skippedPath, originalTime.AddSeconds(2));
        await service.ResumeAsync(id);
        await UntilAsync(() => service.GetSnapshots().Single().State is DownloadState.Downloading or DownloadState.Error,
            "shared boundary piece reverified after skipped neighbour changes");
        Check(!ReferenceEquals(originalHash, HashOperation(service)) && service.GetSnapshots().Single().Progress < 100,
            "a changed skipped file sharing a v1 piece invalidates the selected file's fast resume");
    }

    private static async Task SessionRecoveryAsync(string root)
    {
        var fixture = await FixtureAsync(root);
        var originalState = Path.Combine(root, "original");
        await using (var service = new TorrentService(originalState, true))
        {
            await service.InitializeAsync();
            await service.AddAsync(new(fixture.Metadata, fixture.PayloadDirectory, false));
        }
        string valid = await File.ReadAllTextAsync(Path.Combine(originalState, "session.json"));
        var negative = JsonNode.Parse(valid)!;
        negative["Torrents"]![0]!["UploadedBytes"] = -1;
        var invalidPriority = JsonNode.Parse(valid)!;
        invalidPriority["Torrents"]![0]!["Priorities"]!["sample.bin"] = "invalid";
        var cases = new Dictionary<string, string?>
        {
            ["empty-object"] = "{}",
            ["null-list"] = "{\"Version\":1,\"Torrents\":null}",
            ["missing-list"] = "{\"Version\":1}",
            ["null-entry"] = "{\"Version\":1,\"Torrents\":[null]}",
            ["syntax"] = "{",
            ["negative-counter"] = negative.ToJsonString(),
            ["unknown-priority"] = invalidPriority.ToJsonString(),
            ["missing-primary"] = null
        };
        foreach (var (name, malformed) in cases)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            string primary = Path.Combine(directory, "session.json");
            if (malformed != null) await File.WriteAllTextAsync(primary, malformed);
            await File.WriteAllTextAsync(primary + ".bak", valid);
            await using (var service = new TorrentService(directory, true))
            {
                await service.InitializeAsync();
                Check(service.GetSnapshots().Count == 1 && service.StartupWarnings.Count > 0, name + " restores the valid session with a warning");
            }
            var backup = JsonNode.Parse(await File.ReadAllTextAsync(primary + ".bak"))!;
            Check(backup["Torrents"]!.AsArray().Count == 1, name + " keeps a usable backup after shutdown");
            if (malformed != null) Check(Directory.GetFiles(directory, "session.json.damaged-*").Length >= 1, name + " preserves damaged input");
        }

        foreach (string future in new[] { "{\"Version\":2,\"DifferentSchema\":true}", "{\"Version\":2,\"Torrents\":\"different representation\"}" })
        {
            string directory = Path.Combine(root, "future-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string primary = Path.Combine(directory, "session.json");
            await File.WriteAllTextAsync(primary, future);
            await File.WriteAllTextAsync(primary + ".bak", valid);
            await using (var service = new TorrentService(directory, true))
            {
                bool rejected = false;
                try { await service.InitializeAsync(); }
                catch (UnsupportedSessionVersionException) { rejected = true; }
                Check(rejected, "future session version is rejected before schema fallback");
            }
            Check(await File.ReadAllTextAsync(primary) == future && await File.ReadAllTextAsync(primary + ".bak") == valid, "future primary and backup are unchanged");
        }
    }

    private static async Task SaveFailureAsync(string root)
    {
        var fixture = await FixtureAsync(root);
        string state = Path.Combine(root, "state");
        await using var service = new TorrentService(state, true);
        await service.InitializeAsync();
        await service.UpdateSettingsAsync(service.Settings with { DownloadLimitKiB = 64 });
        string settings = Path.Combine(state, "settings.json");
        File.Move(settings, settings + ".original");
        Directory.CreateDirectory(settings);
        try
        {
            await ExpectWriteFailureAsync(() => service.UpdateSettingsAsync(service.Settings with { DownloadLimitKiB = 128 }));
            var engine = (ClientEngine)typeof(TorrentService).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Check(service.Settings.DownloadLimitKiB == 64 && engine.Settings.MaximumDownloadRate == 64 * 1024, "failed settings write rolls back UI settings and engine rate");
        }
        finally { Directory.Delete(settings); File.Move(settings + ".original", settings); }
        string id = await service.AddAsync(new(fixture.Metadata, Path.Combine(root, "download"), false));
        string session = Path.Combine(state, "session.json");
        File.Move(session, session + ".original");
        Directory.CreateDirectory(session);
        try
        {
            await ExpectWriteFailureAsync(() => service.RemoveAsync(id));
            Check(service.GetSnapshots().Single().State == DownloadState.Paused, "failed remove leaves a visible paused task");
        }
        finally { Directory.Delete(session); File.Move(session + ".original", session); }
        await service.RemoveAsync(id);
        File.Move(session, session + ".original");
        Directory.CreateDirectory(session);
        try
        {
            await ExpectWriteFailureAsync(async () => { await service.AddAsync(new(fixture.Metadata, Path.Combine(root, "download"), false)); });
            Check(service.GetSnapshots().Count == 0, "failed add rolls back the task");
        }
        finally { Directory.Delete(session); File.Move(session + ".original", session); }
        await service.AddAsync(new(fixture.Metadata, Path.Combine(root, "download"), false));
        Check(service.GetSnapshots().Count == 1 && Directory.GetFiles(state, "*.tmp-*").Length == 0, "retry succeeds without stale manager, reservation or temporary writes");
    }

    private static async Task ExpectWriteFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("The deliberately blocked JSON destination did not reject the write.");
    }

    private static async Task StateWriteQuotaAsync(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "state.json");
        await AtomicJsonFile.WriteAsync(path, "original");
        await AtomicJsonFile.WriteAsync(path, "current");
        string current = await File.ReadAllTextAsync(path);
        string backup = await File.ReadAllTextAsync(path + ".bak");
        bool rejected = false;
        try { await AtomicJsonFile.WriteAsync(path, new string('x', AtomicJsonFile.MaximumStateBytes)); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && await File.ReadAllTextAsync(path) == current && await File.ReadAllTextAsync(path + ".bak") == backup,
            "state writer rejects a document its reader cannot load, without changing primary or backup");
        Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "oversized state temporary file is cleaned");
    }

    private static async Task MetadataCancellationAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var tracker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)tracker.Client.LocalEndPoint!).Port;
        string magnet = "magnet:?xt=urn:btih:1111111111111111111111111111111111111111&tr=" + Uri.EscapeDataString($"udp://127.0.0.1:{port}/announce");
        await using var service = new TorrentService(Path.Combine(root, "state"), true);
        await service.InitializeAsync();
        string id = await service.AddAsync(new(magnet, Path.Combine(root, "download")));
        await tracker.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var timer = Stopwatch.StartNew();
        var pause = service.PauseAsync(id);
        await service.UpdateSettingsAsync(service.Settings with { MaximumConnections = 40 }).WaitAsync(TimeSpan.FromSeconds(1.5));
        Check(service.Settings.MaximumConnections == 40, "metadata cleanup does not hold the command gate");
        await pause.WaitAsync(TimeSpan.FromSeconds(6));
        Check(timer.Elapsed < TimeSpan.FromSeconds(6), "unresponsive metadata tracker has bounded pause");
        await service.ResumeAsync(id);
        await service.PauseAsync(id).WaitAsync(TimeSpan.FromSeconds(6));
        Check(service.GetSnapshots().Single().ErrorMessage == null, "metadata pause/resume does not leak a duplicate hidden manager");
        await service.RemoveAsync(id);
        await service.AddAsync(new(magnet, Path.Combine(root, "download")));
        await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Check(true, "metadata shutdown unregisters temporary managers within the bound");

        await using var queued = new TorrentService(Path.Combine(root, "queue-state"), true);
        await queued.InitializeAsync();
        string last = "";
        for (int index = 1; index <= 6; index++)
        {
            string source = "magnet:?xt=urn:btih:" + index.ToString("X40") + "&tr=" + Uri.EscapeDataString($"udp://127.0.0.1:{port}/announce");
            last = await queued.AddAsync(new(source, Path.Combine(root, "queue-download")));
        }
        var slots = (SemaphoreSlim)typeof(TorrentService).GetField("_metadataSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queued)!;
        Check(slots.CurrentCount == 0, "metadata acquisition has a four-operation concurrency budget");
        await queued.PauseAsync(last).WaitAsync(TimeSpan.FromSeconds(1.5));
        Check(queued.GetSnapshots().Single(x => x.Id == last).State == DownloadState.Paused, "queued metadata cancels before creating a manager");
        await queued.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(6));
    }

    private static async Task LifecycleAsync(string root)
    {
        var fixture = await FixtureAsync(root);
        await using var service = new TorrentService(Path.Combine(root, "state"), true);
        await service.InitializeAsync();
        string id = await service.AddAsync(new(fixture.Metadata, fixture.PayloadDirectory, false));
        for (int i = 0; i < 60; i++)
        {
            await service.RecheckAsync(id);
            // Invocation order is deliberate: Resume owns/queues the gate before
            // Pause is invoked, so the last requested state is unambiguously paused.
            Task resume = service.ResumeAsync(id);
            Task pause = service.PauseAsync(id);
            await Task.WhenAll(resume, pause);
            Check(service.GetSnapshots().Single().State == DownloadState.Paused, $"lifecycle iteration {i} pauses successfully");
            await Task.Delay(20);
            Check(service.GetSnapshots().Single().State == DownloadState.Paused, $"lifecycle iteration {i} stays paused after late continuations");
        }
        await service.RecheckAsync(id);
        await service.RemoveAsync(id);
        Check(service.GetSnapshots().Count == 0, "remove interrupts verification without a stale continuation");
        await UntilAsync(() => ActiveBackgroundTaskCount(service) <= 1, "completed background tasks released");
        Check(ActiveBackgroundTaskCount(service) <= 1, "completed observers are not retained; only checkpoint loop remains");
    }

    private static int ActiveBackgroundTaskCount(TorrentService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object sync = typeof(TorrentService).GetField("_backgroundTasksLock", flags)!.GetValue(service)!;
        lock (sync)
            return ((HashSet<Task>)typeof(TorrentService).GetField("_backgroundTasks", flags)!.GetValue(service)!).Count;
    }
}
