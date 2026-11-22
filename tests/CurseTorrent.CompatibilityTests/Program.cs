using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CurseTorrent.Core;
using MonoTorrent;
using MonoTorrent.Client;

var root = Path.Combine(Path.GetTempPath(), "CurseTorrent-network-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failed = 0;
try
{
    foreach (var type in new[] { TorrentType.V1Only, TorrentType.V2Only, TorrentType.V1V2Hybrid })
    {
        try { await Exercise(type).WaitAsync(TimeSpan.FromSeconds(65)); }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL {type}: {ex}"); }
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
Console.WriteLine($"NETWORK_AUDIT_RESULT failures={failed}");
return failed == 0 ? 0 : 1;

async Task Exercise(TorrentType type)
{
    var timer = Stopwatch.StartNew();
    using var tracker = new LocalTracker();
    string dir = Path.Combine(root, type.ToString());
    string seedDir = Path.Combine(dir, "seed");
    Directory.CreateDirectory(seedDir);
    byte[] payload = new byte[512 * 1024 + 37];
    new Random(1701).NextBytes(payload);
    await File.WriteAllBytesAsync(Path.Combine(seedDir, "Файл с пробелами.bin"), payload);
    await File.WriteAllBytesAsync(Path.Combine(seedDir, "small.bin"), payload.AsMemory(0, 10003).ToArray());
    string torrentPath = Path.Combine(dir, "fixture.torrent");
    await new TorrentCreator(type) { PieceLength = 32768, Announce = tracker.AnnounceUrl }
        .CreateAsync(new TorrentFileSource(seedDir) { TorrentName = "Аудит " + type }, torrentPath);
    var torrent = await Torrent.LoadAsync(torrentPath);
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    using var engine = new ClientEngine(new EngineSettingsBuilder
    {
        CacheDirectory = Path.Combine(dir, "seed-cache"),
        ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
        DhtEndPoint = null, AllowPortForwarding = false, AllowLocalPeerDiscovery = false,
        AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false
    }.ToSettings());
    var seeder = await engine.AddAsync(torrentPath, seedDir, new TorrentSettingsBuilder
    { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
    try
    {
        await seeder.StartAsync();
        await Until(() => seeder.State == TorrentState.Seeding, "seeder");
        await using (var service = new TorrentService(Path.Combine(dir, "app-torrent"), true))
        {
            await service.InitializeAsync();
            var id = await service.AddAsync(new AddTorrentRequest(torrentPath, Path.Combine(dir, "torrent-download")));
            await service.AddPeerAsync(id, new Uri($"ipv4://127.0.0.1:{port}"));
            await Until(() => service.GetSnapshots().Single().Progress >= 100, type + " torrent download", () => service.GetSnapshots().Single().ErrorMessage);
            var snap = service.GetSnapshots(id).Single();
            await service.PauseAsync(id);
            CheckPayload(snap.SaveDirectory, payload);
            if (snap.SelectedSizeBytes != payload.Length + 10003) throw new Exception("Selected size differs from exact payload length");
            Console.WriteLine($"PASS {type} torrent full transfer; UTF-8 filename; exact SHA-256; bytes={snap.SelectedSizeBytes}; state={snap.State}");
        }
        string magnet = "magnet:?" +
            (torrent.InfoHashes.V1 != null ? "xt=urn:btih:" + torrent.InfoHashes.V1.ToHex() : "xt=urn:btmh:1220" + torrent.InfoHashes.V2!.ToHex()) +
            "&tr=" + Uri.EscapeDataString(tracker.AnnounceUrl);
        await using (var service = new TorrentService(Path.Combine(dir, "app-magnet"), true))
        {
            await service.InitializeAsync();
            var id = await service.AddAsync(new AddTorrentRequest(magnet, Path.Combine(dir, "magnet-download")));
            bool cold = !service.GetSnapshots().Single().HasMetadata;
            await Until(() => service.GetSnapshots().Single().Progress >= 100, type + " cold magnet", () => service.GetSnapshots().Single().ErrorMessage);
            var snap = service.GetSnapshots(id).Single();
            await service.PauseAsync(id);
            CheckPayload(snap.SaveDirectory, payload);
            Console.WriteLine($"PASS {type} magnet metadata + full transfer; cold={cold}; exact SHA-256; files={snap.Files.Count}; elapsed={timer.Elapsed.TotalSeconds:F2}s");
        }
    }
    finally { await engine.StopAllAsync(TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(5)); }
}

static void CheckPayload(string directory, byte[] payload)
{
    if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "Файл с пробелами.bin"))).AsSpan().SequenceEqual(SHA256.HashData(payload)))
        throw new Exception("Main file SHA-256 mismatch");
    if (!File.ReadAllBytes(Path.Combine(directory, "small.bin")).AsSpan().SequenceEqual(payload.AsSpan(0, 10003)))
        throw new Exception("Small file differs");
}

static async Task Until(Func<bool> condition, string operation, Func<string?>? error = null)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (error?.Invoke() is string detail) throw new Exception(operation + ": " + detail);
        if (timer.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(operation);
        await Task.Delay(50);
    }
}
