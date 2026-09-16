using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record DebugJobJournal(string JobId, string RequestId, string Command, DateTimeOffset Created,
    bool Completed, string Stage, string? Session, string? Pid, string ArtifactDirectory,
    string? ResultPath, long? ResultBytes, bool? Ok, DebugError? Error, JsonElement? Progress,
    int BrokerPid, long BrokerStartedAtUtcTicks);

public static class DebugJobJournalStore
{
    public static void Save(string directory, DebugJobJournal record)
    {
        if (!Regex.IsMatch(record.JobId, "^[a-f0-9]{32}$")) throw new ArgumentException("任务编号无效。");
        if (OperatingSystem.IsWindows()) ColdCheckpoint.Restrict(directory);
        else Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, record.JobId + ".json");
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, DebugJson.Write(record)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<DebugJobJournal> Load(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<DebugJobJournal>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Take(128))
        {
            Storage.StoragePathPolicy.RejectReparsePoints(file.FullName);
            if (file.Length > 1024 * 1024) throw new InvalidDataException("任务归属记录超过上限：" + file.Name);
            var record = JsonSerializer.Deserialize<DebugJobJournal>(File.ReadAllText(file.FullName), DebugJson.Options)
                ?? throw new InvalidDataException("任务归属记录为空。");
            if (!Regex.IsMatch(record.JobId, "^[a-f0-9]{32}$") || file.Name != record.JobId + ".json")
                throw new InvalidDataException("任务归属记录身份不符。");
            result.Add(record);
        }
        return result;
    }

    public static DebugReply Interrupted(DebugJobJournal record, string journalPath) => new(false,
        Error: new("interrupted", "原协调进程已结束，无法确认任务终态；未自动重放。检查阶段证据，导入可使用原importId续作，输入需重新观察后释放/重试。", record.Stage, journalPath),
        RequestId: record.RequestId, JobId: record.JobId, Stage: record.Stage, Terminal: "interrupted",
        Session: record.Session, Pid: record.Pid, ArtifactDirectory: record.ArtifactDirectory);
}
