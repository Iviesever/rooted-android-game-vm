using System.Text.Json;
using System.Text.Json.Serialization;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record DebugRequest(string Command, Dictionary<string, JsonElement>? Arguments = null, int SchemaVersion = 1, string? RequestId = null)
{
    public string Text(string key, string fallback = "") => Arguments?.TryGetValue(key, out var v) == true ? v.GetString() ?? fallback : fallback;
    public int Number(string key, int fallback = 0) => Arguments?.TryGetValue(key, out var v) == true ? v.GetInt32() : fallback;
    public bool Flag(string key) => Arguments?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.True;
    public T? Value<T>(string key) => Arguments?.TryGetValue(key, out var v) == true ? v.Deserialize<T>(DebugJson.Options) : default;
    public static DebugRequest Create(string command, object? arguments = null) => new(command,
        arguments is null ? null : JsonSerializer.SerializeToElement(arguments, DebugJson.Options).Deserialize<Dictionary<string, JsonElement>>());
}
public sealed record DebugError(string Code, string Message, string? Stage = null, string? EvidencePath = null, string? ToolEvidencePath = null);
public sealed record DebugReply(bool Ok, object? Result = null, DebugError? Error = null, int SchemaVersion = 1,
    string? RequestId = null, string? JobId = null, string? Stage = null, string? Terminal = null,
    string? Session = null, string? Pid = null, string? ArtifactDirectory = null)
{
    public static DebugReply Failure(Exception e) => new(false, Error: new(e switch
    {
        DebugException d => d.Code,
        Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.DeadlineExceeded } => "timeout",
        Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Cancelled } => "cancelled",
        Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unavailable } => "grpc_unavailable",
        Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied } => "permission_denied",
        Processes.ProcessOutputLimitException => "output_limit",
        Android.HostMemoryInsufficientException => "host_memory_low",
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        UnauthorizedAccessException => "permission_denied",
        IOException io when (io.HResult & 0xffff) is 39 or 112 => "disk_full",
        ArgumentException => "invalid_argument",
        JsonException => "invalid_argument",
        _ => "operation_failed"
    }, e is Grpc.Core.RpcException rpc ? "模拟器 gRPC 调用失败：" + rpc.StatusCode + "。认证材料不会写入诊断。" : e.Message,
        (e as DebugException)?.Stage, (e as DebugException)?.EvidencePath, ToolEvidence(e)));
    private static string? ToolEvidence(Exception? error) => error is null ? null :
        error.Data["toolEvidencePath"] as string ?? ToolEvidence(error.InnerException);
}
public sealed class DebugException(string code, string message, string? stage = null, string? evidencePath = null, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public string? Stage { get; } = stage;
    public string? EvidencePath { get; } = evidencePath;
    public static DebugException FromError(DebugError error)
    {
        var exception = new DebugException(error.Code, error.Stage is null ? error.Message : $"[{error.Stage}] {error.Message}", error.Stage, error.EvidencePath);
        if (error.ToolEvidencePath is not null) exception.Data["toolEvidencePath"] = error.ToolEvidencePath;
        return exception;
    }
}
public static class DebugJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
}
public sealed record ScreenObservation(string Id, string Session, string Path, string Backend, int Width, int Height,
    int Rotation, int Display, string? Foreground, bool BootCompleted, bool Awake, bool Locked, DateTimeOffset CapturedAt,
    int ImageRotation = 0, bool? Blank = null, string? AppPid = null, long Revision = 0);
public sealed record TouchPoint(int Id, int X, int Y, int Pressure = 1);
public sealed record InputFrame(int AtMs, TouchPoint[] Touches);
public sealed record InputTiming(int Index, double RequestedMs, double SentMs, double DeviationMs,
    long? SentTimestamp = null, long? AcknowledgedTimestamp = null);
