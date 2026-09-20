using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

// Per-request context flows through awaits, but completed jobs retain only small metadata.
public sealed class DebugOperation(string requestId, string? jobId, string directory)
{
    public static readonly AsyncLocal<DebugOperation?> Current = new();
    public string RequestId { get; } = requestId;
    public string? JobId { get; } = jobId;
    public string DirectoryPath { get; } = directory;
    private string _stage = "queued";
    private string? _session, _pid;
    public Action? Changed { get; set; }
    public string Stage { get => _stage; set { if (_stage != value) { _stage = value; Changed?.Invoke(); } } }
    public string? Session { get => _session; set { if (_session != value) { _session = value; Changed?.Invoke(); } } }
    public string? Pid { get => _pid; set { if (_pid != value) { _pid = value; Changed?.Invoke(); } } }
    public bool CaptureTools { get; init; } = true;
    private int _toolIndex;
    public void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) ColdCheckpoint.Restrict(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath);
    }
    public string NewToolPath()
    {
        EnsureDirectory();
        return Path.Combine(DirectoryPath, $"tool-{Interlocked.Increment(ref _toolIndex):D5}.json");
    }
    public void ObserveProgress(object value)
    {
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, DebugJson.Options);
        if (element.TryGetProperty("stage", out var stage)) Stage = stage.GetString() ?? Stage;
        if (element.TryGetProperty("session", out var session)) Session = session.GetString() ?? Session;
        if (element.TryGetProperty("pid", out var pid)) Pid = pid.GetString() ?? Pid;
    }
    public object CaptureProgress(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, DebugJson.Options);
        ObserveProgress(element);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(element, DebugJson.Options);
        if (bytes.Length <= 64 * 1024) return element;
        EnsureDirectory();
        var path = Path.Combine(DirectoryPath, "progress.json");
        File.WriteAllBytes(path + ".partial", bytes); File.Move(path + ".partial", path, true);
        return new
        {
            stage = Stage,
            session = Session,
            pid = Pid,
            detailPath = path,
            detailBytes = bytes.Length,
            directory = element.TryGetProperty("directory", out var artifact) ? artifact.GetString() : DirectoryPath,
            importId = element.TryGetProperty("importId", out var importId) ? importId.GetString() : null
        };
    }
    public DebugReply Complete(DebugReply reply) => reply with
    {
        RequestId = RequestId,
        JobId = JobId,
        Stage = reply.Error?.Stage ?? Stage,
        Terminal = reply.Ok ? "succeeded" : reply.Error?.Code switch { "cancelled" => "cancelled", "timeout" => "timed_out", "interrupted" => "interrupted", _ => "failed" },
        Session = Session,
        Pid = Pid,
        ArtifactDirectory = Directory.Exists(DirectoryPath) ? DirectoryPath : null,
        Error = reply.Error is null ? null : reply.Error with { Stage = reply.Error.Stage ?? Stage }
    };
}
