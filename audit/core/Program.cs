using CurseTorrent.Core;
using MonoTorrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

var mode = args.FirstOrDefault();
if (mode is null or "-h" or "--help") {
  Console.WriteLine("CoreAudit modes: changed-payload | session | save-failure | transitions | metadata-stop");
  Console.WriteLine("Observations may describe defects even when this diagnostic process exits successfully.");
  return 0;
}
if (mode is not ("changed-payload" or "session" or "save-failure" or "transitions" or "metadata-stop")) {
  Console.Error.WriteLine("Unknown mode. Use --help for the supported audit scenarios.");
  return 2;
}
var root = Path.Combine(Path.GetTempPath(), "cursetorrent-audit-" + mode + "-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"AUDIT mode={mode} root={root}");
try {
  if(mode=="metadata-stop") await MetadataStopAudit(root);
  else if(mode=="session") await SessionAudit(root);
  else if(mode=="changed-payload") await ChangedPayloadAudit(root);
  else if(mode=="save-failure") await SaveFailureAudit(root);
  else if(mode=="transitions") await TransitionAudit(root);
  else throw new ArgumentException("unknown mode");
  return 0;
} catch(Exception ex) { Console.WriteLine(ex); return 1; }

static async Task Until(Func<bool> condition, string stage, int timeout=10) {
  var timer=Stopwatch.StartNew();
  while(!condition()) { if(timer.Elapsed.TotalSeconds>timeout) throw new TimeoutException(stage); await Task.Delay(25); }
}
static async Task<(string metadata, string seeds, string filename, byte[] bytes)> Fixture(string root) {
  var seeds=Path.Combine(root,"seed"); Directory.CreateDirectory(seeds);
  var bytes=new byte[128*1024]; new Random(614).NextBytes(bytes);
  var filename="sample.bin"; await File.WriteAllBytesAsync(Path.Combine(seeds,filename),bytes);
  var metadata=Path.Combine(root,"sample.torrent");
  var creator=new TorrentCreator(TorrentType.V1Only){PieceLength=32*1024};
  await creator.CreateAsync(new TorrentFileSource(Path.Combine(seeds,filename)),metadata);
  return(metadata,seeds,filename,bytes);
}
static async Task MetadataStopAudit(string root) {
  using var sink=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
  int port=((IPEndPoint)sink.Client.LocalEndPoint!).Port;
  string magnet="magnet:?xt=urn:btih:1111111111111111111111111111111111111111&tr="+Uri.EscapeDataString($"udp://127.0.0.1:{port}/announce");
  await using var service=new TorrentService(Path.Combine(root,"state"),true);
  await service.InitializeAsync();
  var id=await service.AddAsync(new(magnet,Path.Combine(root,"downloads"),true));
  await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(8));
  Console.WriteLine("tracker_request_received=true");
  var sw=Stopwatch.StartNew();
  var pause=service.PauseAsync(id);
  await Task.Delay(100);
  var settings=service.UpdateSettingsAsync(service.Settings with {MaximumConnections=40});
  await Task.Delay(3000);
  Console.WriteLine($"after_3_seconds pause_completed={pause.IsCompleted} unrelated_settings_completed={settings.IsCompleted}");
  await pause.WaitAsync(TimeSpan.FromSeconds(75));
  Console.WriteLine($"pause_elapsed_seconds={sw.Elapsed.TotalSeconds:F3}");
  await settings;
  Console.WriteLine("settings_applied_connections="+service.Settings.MaximumConnections);
}
static async Task SessionAudit(string root) {
  var f=await Fixture(root);
  var state=Path.Combine(root,"base-state");
  await using(var service=new TorrentService(state,true)) {await service.InitializeAsync();await service.AddAsync(new(f.metadata,f.seeds,false));}
  var valid=await File.ReadAllTextAsync(Path.Combine(state,"session.json"));
  var cases=new Dictionary<string,string>{
    ["null-list"]="{\"Version\":1,\"Torrents\":null}",
    ["null-entry"]="{\"Version\":1,\"Torrents\":[null]}",
    ["missing-fields"]="{\"Version\":1,\"Torrents\":[{\"Id\":\"abcdef\"}]}",
    ["wrong-priority-type"]=valid.Replace("\"Priorities\": {}","\"Priorities\": {\"sample.bin\": \"not-a-priority\"}"),
    ["negative-upload"]=valid.Replace("\"UploadedBytes\": 0","\"UploadedBytes\": -12345"),
    ["unknown-version"]=valid.Replace("\"Version\": 1","\"Version\": 2"),
    ["missing-version"]=valid.Replace("\"Version\": 1,",""),
    ["empty-document"]="{}"
  };
  foreach(var (name,json) in cases) {
    string dir=Path.Combine(root,"case-"+name);Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir,"session.json"),json);
    await File.WriteAllTextAsync(Path.Combine(dir,"session.json.bak"),valid);
    var service=new TorrentService(dir,true);
    try {await service.InitializeAsync();Console.WriteLine($"CASE {name} count={service.GetSnapshots().Count} warnings={JsonSerializer.Serialize(service.StartupWarnings)} snapshots={JsonSerializer.Serialize(service.GetSnapshots())}");}
    catch(Exception ex) {Console.WriteLine($"CASE {name} INIT_FAILED {ex.GetType().Name}: {ex.Message}");}
    await service.DisposeAsync();
    Console.WriteLine($"CASE {name} original_preserved={File.ReadAllText(Path.Combine(dir,"session.json"))==json} damaged_copies={Directory.GetFiles(dir,"*.damaged-*").Length} backup_still_valid={File.ReadAllText(Path.Combine(dir,"session.json.bak"))==valid}");
  }
}

static async Task ChangedPayloadAudit(string root) {
  var f=await Fixture(root);
  var state=Path.Combine(root,"state"); string id;
  await using(var service=new TorrentService(state,true)) {
    await service.InitializeAsync();
    id=await service.AddAsync(new(f.metadata,f.seeds,false));
    await service.RecheckAsync(id);
    await Until(()=>service.GetSnapshots().Single().Progress>=99.999 && service.GetSnapshots().Single().State!=DownloadState.Checking,"initial complete check");
    Console.WriteLine("original_verified="+JsonSerializer.Serialize(service.GetSnapshots().Single()));
  }
  await File.WriteAllBytesAsync(Path.Combine(f.seeds,f.filename),new byte[]{1});
  await using(var service=new TorrentService(state,true)) {
    await service.InitializeAsync();
    Console.WriteLine("after_restart="+JsonSerializer.Serialize(service.GetSnapshots().Single()));
    await service.ResumeAsync(id);
    await Until(()=>service.GetSnapshots().Single().State is DownloadState.Seeding or DownloadState.Error,"start with changed data");
    Console.WriteLine("after_resume="+JsonSerializer.Serialize(service.GetSnapshots().Single()));
    Console.WriteLine("actual_file_bytes="+new FileInfo(Path.Combine(f.seeds,f.filename)).Length);
    await service.PauseAsync(id); await service.RecheckAsync(id);
    await Until(()=>service.GetSnapshots().Single().State!=DownloadState.Checking,"manual recheck");
    Console.WriteLine("after_manual_recheck="+JsonSerializer.Serialize(service.GetSnapshots().Single()));
  }
}

static void Check(bool condition,string name) {
  if(!condition) throw new InvalidOperationException("CHECK FAIL: "+name);
  Console.WriteLine("CHECK PASS: "+name);
}
static async Task ExpectIoFailure(Func<Task> action,string name) {
  try {await action();throw new InvalidOperationException("Expected IO failure: "+name);}
  catch(IOException ex) {Console.WriteLine("EXPECTED IO FAILURE "+name+": "+ex.Message);}
  catch(UnauthorizedAccessException ex) {Console.WriteLine("EXPECTED IO FAILURE "+name+": "+ex.Message);}
}
static async Task SaveFailureAudit(string root) {
  var f=await Fixture(root); var state=Path.Combine(root,"state");
  await using var service=new TorrentService(state,true);
  await service.InitializeAsync();
  await service.UpdateSettingsAsync(service.Settings with {DownloadLimitKiB=64});
  var settingsPath=Path.Combine(state,"settings.json");
  File.Move(settingsPath,settingsPath+".original"); Directory.CreateDirectory(settingsPath);
  try {
    await ExpectIoFailure(()=>service.UpdateSettingsAsync(service.Settings with {DownloadLimitKiB=128}),"settings.json blocked by directory");
    Check(service.Settings.DownloadLimitKiB==64,"Settings snapshot rolls back failed update");
    var engine=(MonoTorrent.Client.ClientEngine)typeof(TorrentService).GetField("_engine",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
    Check(engine.Settings.MaximumDownloadRate==64*1024,"engine rate rolls back failed update");
  } finally {Directory.Delete(settingsPath);File.Move(settingsPath+".original",settingsPath);}
  var id=await service.AddAsync(new(f.metadata,Path.Combine(root,"downloads"),false));
  var sessionPath=Path.Combine(state,"session.json");
  File.Move(sessionPath,sessionPath+".original"); Directory.CreateDirectory(sessionPath);
  try {
    await ExpectIoFailure(()=>service.RemoveAsync(id),"remove with unwritable session target");
    Check(service.GetSnapshots().Count==1 && service.GetSnapshots().Single().State==DownloadState.Paused,"failed remove leaves paused visible task");
  } finally {Directory.Delete(sessionPath);File.Move(sessionPath+".original",sessionPath);}
  await service.RemoveAsync(id);
  File.Move(sessionPath,sessionPath+".original"); Directory.CreateDirectory(sessionPath);
  try {
    await ExpectIoFailure(async()=>{await service.AddAsync(new(f.metadata,Path.Combine(root,"downloads"),false));},"add with unwritable session target");
    Check(service.GetSnapshots().Count==0,"failed add rolls back public task");
  } finally {Directory.Delete(sessionPath);File.Move(sessionPath+".original",sessionPath);}
  await service.AddAsync(new(f.metadata,Path.Combine(root,"downloads"),false));
  Check(service.GetSnapshots().Count==1,"retry add after rollback succeeds without phantom manager or reserved paths");
  Check(Directory.GetFiles(state,"*.tmp-*").Length==0,"all failed atomic-write temporary files cleaned");
}
static async Task TransitionAudit(string root) {
  var f=await Fixture(root);
  await using var service=new TorrentService(Path.Combine(root,"state"),true);
  await service.InitializeAsync();
  var id=await service.AddAsync(new(f.metadata,f.seeds,false));
  for(int i=0;i<20;i++) {
    await service.RecheckAsync(id);
    await Task.WhenAll(service.ResumeAsync(id),service.PauseAsync(id));
    Check(service.GetSnapshots().Single().State==DownloadState.Paused,"recheck/resume/pause iteration "+i);
  }
  for(int i=0;i<10;i++) {
    await service.RecheckAsync(id);
    await service.RemoveAsync(id);
    Check(service.GetSnapshots().Count==0,"immediate recheck/remove iteration "+i);
    id=await service.AddAsync(new(f.metadata,f.seeds,false));
  }
  await service.ResumeAsync(id);
  await Until(()=>service.GetSnapshots().Single().State==DownloadState.Seeding,"final healthy seeding");
  Check(service.GetSnapshots().Single().Progress==100,"service remains healthy after control transitions");
  await service.PauseAsync(id);
}
