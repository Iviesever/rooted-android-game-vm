using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
if (args is ["--broker"])
{
    var control = new RootedAndroidGameVM.Core.Storage.ProductStorageLocation().ControlRoot;
    Directory.CreateDirectory(control);
    FileStream lockFile;
    try { lockFile = new FileStream(Path.Combine(control, "debug-broker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { return 0; }
    using var brokerLock = lockFile;
    using var broker = new DebugBroker();
    try { await broker.RunAsync(cancellation.Token); }
    catch (OperationCanceledException) { }
    return 0;
}
string? jobId = null;
var client = new DebugClient();
try
{
    DebugRequest request;
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine(DebugJson.Write(new
        {
            schemaVersion = 1,
            version = "0.3.0",
            usage = "RootedAndroidGameVM.Cli <command> [--json '{...}'] [--wait] | --request file.json",
            examples = new[] { "status", "start --wait", "screen", "input --json '{\"observation\":\"id\",\"frames\":[...]}' --wait", "checkpoint.create --wait", "job --json '{\"id\":\"jobId\"}'" },
            note = "长操作返回 jobId；--wait 每秒输出 NDJSON 任务状态。输入须每 5 秒内轮询一次任务，否则自动释放触点。"
        })); return 0;
    }
    if (args[0] == "--request")
    {
        if (args.Length < 2 || new FileInfo(args[1]).Length > 1024 * 1024) throw new ArgumentException("请求文件缺失或超过 1 MiB。");
        request = JsonSerializer.Deserialize<DebugRequest>(await File.ReadAllTextAsync(args[1]), DebugJson.Options) ?? throw new ArgumentException("无效请求文件。");
    }
    else
    {
        var index = Array.IndexOf(args, "--json");
        request = new(args[0], index >= 0 ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args[index + 1], DebugJson.Options) : null);
    }
    var reply = await client.SendAsync(request, cancellation.Token); Console.WriteLine(DebugJson.Write(reply));
    if (!reply.Ok) return 1;
    var result = (JsonElement)reply.Result!;
    if (args.Contains("--wait") && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("jobId", out var id))
    {
        jobId = id.GetString();
        while (true)
        {
            await Task.Delay(500, cancellation.Token);
            reply = await client.SendAsync(DebugRequest.Create("job", new { id = jobId }), cancellation.Token);
            Console.WriteLine(DebugJson.Write(reply));
            if (!reply.Ok) return 1;
            result = (JsonElement)reply.Result!;
            if (result.GetProperty("completed").GetBoolean()) return result.GetProperty("result").GetProperty("ok").GetBoolean() ? 0 : 1;
        }
    }
    return 0;
}
catch (Exception e)
{
    if (jobId is not null) { try { await client.SendAsync(DebugRequest.Create("cancel", new { id = jobId }), CancellationToken.None); } catch { } }
    Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
    Console.WriteLine(DebugJson.Write(DebugReply.Failure(e))); return 1;
}
