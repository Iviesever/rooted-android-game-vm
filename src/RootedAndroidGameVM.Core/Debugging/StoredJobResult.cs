using System.Text.Json;
using System.Runtime.Versioning;

namespace RootedAndroidGameVM.Core.Debugging;

// Metadata only: a finished job must not root its arbitrary object graph for the broker's lifetime.
public sealed record StoredJobResult(string Path, long Bytes, bool Ok, DebugError? Error, DebugReply? Envelope = null)
{
    public const int MaxInlineBytes = 1024 * 1024;
    [SupportedOSPlatform("windows")]
    public static async Task<StoredJobResult> WriteAsync(string directory, string id, DebugReply reply)
    {
        Storage.StoragePathPolicy.RejectReparsePoints(directory);
        ColdCheckpoint.Restrict(directory);
        var path = ColdCheckpoint.SafeChild(directory, id + ".json");
        var temporary = path + ".partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true))
                await JsonSerializer.SerializeAsync(stream, reply, DebugJson.Options);
            File.Move(temporary, path);
            var error = reply.Error;
            if (error?.Message.Length > 4096) error = error with { Message = error.Message[..4096] + "…完整错误见结果文件。" };
            return new(path, new FileInfo(path).Length, reply.Ok, error, reply with { Result = null, Error = error });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public DebugReply Reference() => (Envelope ?? new(Ok, Error: Error)) with { Result = new { resultPath = Path, resultBytes = Bytes, inline = false } };
    public DebugReply Read()
    {
        if (Bytes > MaxInlineBytes) return Reference();
        using var stream = File.OpenRead(Path);
        if (stream.Length != Bytes) throw new IOException("任务结果文件长度发生变化。");
        return JsonSerializer.Deserialize<DebugReply>(stream, DebugJson.Options) ?? throw new IOException("任务结果为空。");
    }
}
