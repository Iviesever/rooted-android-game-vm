using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record TransferOptions(string ConflictPolicy, bool StopApplications);
public sealed record TransferExecutionHeader(string PlanId, TransferOptions Options, string Session, string JobId,
    string Status, DateTimeOffset UpdatedAt, string? Error = null, string? ArchivePath = null, string? ArchiveSha256 = null,
    int OwnerPid = 0, long OwnerStartedTicks = 0, string? ErrorCode = null);
public sealed record TransferItemState(int Index, string Status, string TargetRelativePath, long Offset = 0,
    string? TemporaryPath = null, string? BackupPath = null, string? Sha256 = null, string? Error = null);

public sealed class TransferExecutionStore(string directory)
{
    public string HeaderPath => Path.Combine(directory, "execution.json");
    public string EntriesPath => Path.Combine(directory, "execution.ndjson");
    public async Task<TransferExecutionHeader?> ReadHeaderAsync(CancellationToken ct)
    {
        StoragePathPolicy.RejectReparsePoints(HeaderPath);
        if (!File.Exists(HeaderPath)) return null;
        if (new FileInfo(HeaderPath).Length > 65536) throw new InvalidDataException("执行头超过限制。");
        await using var stream = new FileStream(HeaderPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        return await JsonSerializer.DeserializeAsync<TransferExecutionHeader>(stream, DebugJson.Options, ct)
            ?? throw new InvalidDataException("执行头损坏。");
    }
    public Task SaveHeaderAsync(TransferExecutionHeader header, CancellationToken ct) => AtomicJsonFile.WriteAsync(HeaderPath, header, ct);
    public Dictionary<int, TransferItemState> ReadEntries(bool repairTail = false)
    {
        StoragePathPolicy.RejectReparsePoints(EntriesPath);
        var items = new Dictionary<int, TransferItemState>();
        if (!File.Exists(EntriesPath)) return items;
        long completeBytes = 0;
        using (var stream = new FileStream(EntriesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
        {
            while (reader.ReadLine() is { } line)
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (completeBytes + bytes > stream.Length) break; // An interrupted final append has no newline.
                var item = JsonSerializer.Deserialize<TransferItemState>(line, DebugJson.Options) ?? throw new InvalidDataException("执行条目损坏。");
                if (item.Index < 0 || item.Index >= FileTransferPolicy.MaxEntries) throw new InvalidDataException("执行条目索引无效。");
                items[item.Index] = item; completeBytes += bytes;
            }
        }
        if (repairTail && new FileInfo(EntriesPath).Length != completeBytes)
        {
            using var stream = new FileStream(EntriesPath, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(completeBytes); stream.Flush(true);
        }
        return items;
    }
    public async Task SaveItemAsync(TransferItemState item, CancellationToken ct)
    {
        StoragePathPolicy.RejectReparsePoints(EntriesPath);
        var bytes = Encoding.UTF8.GetBytes(DebugJson.Write(item) + "\n");
        await using var stream = new FileStream(EntriesPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
    }
}

public static class TransferExecutionLiveness
{
    public static bool CanResume(TransferExecutionHeader header) => header.Status is "cancelled" or "interrupted" ||
        header.Status == "failed" && header.ErrorCode is not ("plan_stale" or "stale_reference" or "source_changed" or "target_changed" or "plan_has_issues" or "checksum_mismatch" or "staging_mismatch");
    public static TransferExecutionHeader Observe(TransferExecutionHeader header)
    {
        if (header.Status is "succeeded" or "failed" or "cancelled" or "interrupted") return header;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(header.OwnerPid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == header.OwnerStartedTicks) return header;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return header with { Status = "interrupted", Error = "原执行进程已结束，未自动重放。" };
    }
}

public static class TransferDispatchIdentity
{
    public static bool Applies(string command) => command is "files.transfer.start" or "files.transfer.resume";
    public static string JobId(DebugRequest request)
    {
        if (!Guid.TryParseExact(request.Text("planId"), "N", out _)) throw new ArgumentException("planId无效。");
        var key = request.Text("idempotencyKey");
        if (key.Length is < 1 or > 128 || key.Any(char.IsControl)) throw new ArgumentException("传输执行需要1–128字符的idempotencyKey。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Command + "\0" + request.Text("planId") + "\0" + key)))[..32].ToLowerInvariant();
    }
    public static string Intent(DebugRequest request)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            void Write(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject(); foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(property.Name); Write(property.Value); }
                    writer.WriteEndObject();
                }
                else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item); writer.WriteEndArray(); }
                else value.WriteTo(writer);
            }
            Write(JsonSerializer.SerializeToElement(new { request.Command, request.SchemaVersion, request.Arguments }, DebugJson.Options));
        }
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()));
    }
}
