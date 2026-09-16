using System.Diagnostics;
using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;

if (args is ["buffers", var outputDirectory]) { await BufferBenchmark.RunAsync(outputDirectory); return; }
if (args.Length < 4 || args[0] != "sample")
    throw new ArgumentException("sample <dataRoot> <output.ndjson> <stage.txt> [seconds=600] [intervalMs=1000] [program directories...] | buffers <directory>");
var paths = InstallPaths.FromProductRoot(args[1]);
var seconds = args.Length > 4 ? int.Parse(args[4]) : 600;
var interval = args.Length > 5 ? int.Parse(args[5]) : 1000;
if (seconds is < 1 or > 7200 || interval is < 250 or > 10000) throw new ArgumentException("采样时长/间隔超限。");
var directories = args.Length > 6 ? args[6..] : new[] { AppContext.BaseDirectory };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await using var writer = new StreamWriter(new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read));
var clock = Stopwatch.StartNew(); var index = 0;
var sampler = new ProcessMemorySampler(paths, directories); string? previousStage = null;
try
{
    while (clock.Elapsed.TotalSeconds < seconds && !stop.IsCancellationRequested)
    {
        var started = clock.Elapsed.TotalMilliseconds;
        var stage = File.Exists(args[3]) ? File.ReadAllText(args[3]).Trim() : "unmarked";
        if (stage == "done") break;
        if (stage != previousStage) { sampler.RefreshInventory(); previousStage = stage; }
        try
        {
            var snapshot = sampler.Read();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { index, elapsedMs = started, stage, snapshot }, DebugJson.Options));
        }
        catch (Exception error) { await writer.WriteLineAsync(DebugJson.Write(new { index, elapsedMs = started, stage, error = error.Message })); }
        await writer.FlushAsync(); index++;
        var delay = Math.Max(1, interval - (clock.Elapsed.TotalMilliseconds - started));
        await Task.Delay(TimeSpan.FromMilliseconds(delay), stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
Console.WriteLine(DebugJson.Write(new { samples = index, seconds = clock.Elapsed.TotalSeconds, path = Path.GetFullPath(args[2]) }));
