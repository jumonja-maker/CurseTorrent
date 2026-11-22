using System.Diagnostics;

var root = Path.Combine(Path.GetTempPath(), "cursetorrent-regressions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var elapsed = Stopwatch.StartNew();
try
{
    await CoreRegressions.RunAsync(root).WaitAsync(TimeSpan.FromSeconds(120));
    Console.WriteLine($"PASS: CurseTorrent regression scenarios, {elapsed.Elapsed.TotalSeconds:F1}s");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}
