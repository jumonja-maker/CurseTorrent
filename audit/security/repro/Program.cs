using System.Security.Cryptography;
using CurseTorrent.Core;
using MonoTorrent;

string mode = args.FirstOrDefault() ?? "existing";
string root = Path.Combine(Path.GetTempPath(), "cursetorrent-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"MODE={mode} root={root}");
string sourceDir=Path.Combine(root,"seed");
string downloads=Path.Combine(root,"downloads");
Directory.CreateDirectory(sourceDir); Directory.CreateDirectory(downloads);
var random = new Random(137);
byte[] actual = new byte[32768]; random.NextBytes(actual);
byte[] prior = new byte[131072]; random.NextBytes(prior);
string torrentPath = Path.Combine(root, "sample.torrent");
string victim;
var priorities = new Dictionary<string,FilePriority>();
if (mode=="skipzero") {
    await File.WriteAllBytesAsync(Path.Combine(sourceDir,"payload.bin"),actual);
    await File.WriteAllBytesAsync(Path.Combine(sourceDir,"notes.txt"),[]);
    var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength=16384 };
    await creator.CreateAsync(new TorrentFileSource(sourceDir) { TorrentName="Example" }, torrentPath);
    Directory.CreateDirectory(Path.Combine(downloads,"Example"));
    victim=Path.Combine(downloads,"Example","notes.txt");
    priorities["notes.txt"]=FilePriority.Skip;
} else {
    var original = Path.Combine(sourceDir,"notes.txt");
    await File.WriteAllBytesAsync(original, actual);
    await new TorrentCreator(TorrentType.V1Only) { PieceLength=16384 }.CreateAsync(new TorrentFileSource(original),torrentPath);
    victim=Path.Combine(downloads,"notes.txt");
}
await File.WriteAllBytesAsync(victim,prior);
Console.WriteLine($"BEFORE path={victim}; length={new FileInfo(victim).Length}; sha256={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(victim)))}");
await using(var service = new TorrentService(Path.Combine(root,"state"), true)) {
    await service.InitializeAsync();
    var id = await service.AddAsync(new(torrentPath,downloads,false,priorities));
    Console.WriteLine("Added paused. Original length=" + new FileInfo(victim).Length);
    if(mode=="skipzero") await service.ResumeAsync(id); else await service.RecheckAsync(id);
    await Task.Delay(1500);
    var snapshot=service.GetSnapshots(id).Single();
    Console.WriteLine($"State={snapshot.State}; error={snapshot.ErrorMessage}; files={string.Join("|",snapshot.Files.Select(f=>$"{f.Path}:{f.Priority}"))}");
    await service.PauseAsync(id);
}
string actualVictim=File.Exists(victim)?victim:victim+".!mt";
Console.WriteLine($"AFTER path={actualVictim}; length={new FileInfo(actualVictim).Length}; sha256={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(actualVictim)))}");
Console.WriteLine("Remaining files="+string.Join("|",Directory.GetFiles(downloads,"*",SearchOption.AllDirectories)));
