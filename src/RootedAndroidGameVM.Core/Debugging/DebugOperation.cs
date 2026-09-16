using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

// Per-request context flows through awaits, but completed jobs retain only small metadata.
public sealed class DebugOperation(string requestId, string? jobId, string directory)
{
    public static readonly AsyncLocal<DebugOperation?> Current = new();
    public string RequestId { get; } = requestId;
    public string? JobId { get; } = jobId;
    public string DirectoryPath { get; } = directory;
    public string Stage { get; set; } = "queued";
    public string? Session { get; set; }
    public string? Pid { get; set; }
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
        var element = JsonSerializer.SerializeToElement(value, DebugJson.Options);
        if (element.TryGetProperty("stage", out var stage)) Stage = stage.GetString() ?? Stage;
        if (element.TryGetProperty("session", out var session)) Session = session.GetString() ?? Session;
        if (element.TryGetProperty("pid", out var pid)) Pid = pid.GetString() ?? Pid;
    }
    public DebugReply Complete(DebugReply reply) => reply with
    {
        RequestId = RequestId, JobId = JobId, Stage = reply.Error?.Stage ?? Stage,
        Terminal = reply.Ok ? "succeeded" : reply.Error?.Code switch { "cancelled" => "cancelled", "timeout" => "timed_out", "interrupted" => "interrupted", _ => "failed" },
        Session = Session, Pid = Pid, ArtifactDirectory = Directory.Exists(DirectoryPath) ? DirectoryPath : null,
        Error = reply.Error is null ? null : reply.Error with { Stage = reply.Error.Stage ?? Stage }
    };
}
