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
var requestId = Guid.NewGuid().ToString("N");
var client = new DebugClient();
try
{
    DebugRequest request;
    if (args.Length == 0 || args[0] == "help")
    {
        Console.WriteLine(DebugJson.Write(new
        {
            schemaVersion = 1,
            ok = true,
            requestId,
            terminal = "succeeded",
            version = "0.5.0",
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
        if (index >= 0 && index + 1 >= args.Length) throw new ArgumentException("--json缺少请求参数。");
        request = new(args[0], index >= 0 ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args[index + 1], DebugJson.Options) : null);
    }
    request = request with { RequestId = request.RequestId ?? requestId };
    requestId = request.RequestId;
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
    var cleanupConfirmed = jobId is null;
    if (jobId is not null)
    {
        try
        {
            var cleanupReply = await client.CancelAndWaitAsync(jobId, CancellationToken.None);
            Console.WriteLine(DebugJson.Write(cleanupReply));
            cleanupConfirmed = cleanupReply.Ok && cleanupReply.Result is JsonElement job && job.GetProperty("completed").GetBoolean()
                && job.GetProperty("status").GetString() != "interrupted";
        }
        catch (Exception cleanup) { Console.Error.WriteLine("取消后尚未确认清理终态；查询任务 " + jobId + "：" + cleanup.Message); }
    }
    Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
    var failed = DebugReply.Failure(e);
    if (!cleanupConfirmed) failed = new(false, Error: new("cancellation_unconfirmed", "取消后未确认任务清理终态；请查询任务 " + jobId + "。", "client_request"));
    Console.WriteLine(DebugJson.Write(failed with
    {
        RequestId = requestId,
        JobId = jobId,
        Stage = "client_request",
        Error = failed.Error! with { Stage = failed.Error!.Stage ?? "client_request" },
        Terminal = failed.Error!.Code switch { "cancelled" when cleanupConfirmed => "cancelled", "timeout" => "timed_out", _ => "failed" }
    })); return 1;
}
