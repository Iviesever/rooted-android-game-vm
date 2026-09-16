using System.Text.Json;
using System.Text.Json.Serialization;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record DebugRequest(string Command, Dictionary<string, JsonElement>? Arguments = null, int SchemaVersion = 1)
{
    public string Text(string key, string fallback = "") => Arguments?.TryGetValue(key, out var v) == true ? v.GetString() ?? fallback : fallback;
    public int Number(string key, int fallback = 0) => Arguments?.TryGetValue(key, out var v) == true ? v.GetInt32() : fallback;
    public bool Flag(string key) => Arguments?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.True;
    public T? Value<T>(string key) => Arguments?.TryGetValue(key, out var v) == true ? v.Deserialize<T>(DebugJson.Options) : default;
    public static DebugRequest Create(string command, object? arguments = null) => new(command,
        arguments is null ? null : JsonSerializer.SerializeToElement(arguments, DebugJson.Options).Deserialize<Dictionary<string, JsonElement>>());
}
public sealed record DebugError(string Code, string Message);
public sealed record DebugReply(bool Ok, object? Result = null, DebugError? Error = null, int SchemaVersion = 1)
{
    public static DebugReply Failure(Exception e) => new(false, Error: new(e switch
    {
        DebugException d => d.Code,
        Processes.ProcessOutputLimitException => "output_limit",
        Android.HostMemoryInsufficientException => "host_memory_low",
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        UnauthorizedAccessException => "permission_denied",
        IOException io when (io.HResult & 0xffff) is 39 or 112 => "disk_full",
        ArgumentException => "invalid_argument",
        _ => "operation_failed"
    }, e is Grpc.Core.RpcException rpc ? "模拟器 gRPC 调用失败：" + rpc.StatusCode + "。认证材料不会写入诊断。" : e.Message));
}
public sealed class DebugException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public static class DebugJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
}
public sealed record ScreenObservation(string Id, string Session, string Path, string Backend, int Width, int Height,
    int Rotation, int Display, string? Foreground, bool BootCompleted, bool Awake, bool Locked, DateTimeOffset CapturedAt,
    int ImageRotation = 0, bool? Blank = null);
public sealed record TouchPoint(int Id, int X, int Y, int Pressure = 1);
public sealed record InputFrame(int AtMs, TouchPoint[] Touches);
public sealed record InputTiming(int Index, double RequestedMs, double SentMs, double DeviationMs);
