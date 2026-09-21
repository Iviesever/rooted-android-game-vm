using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public sealed record TransferResultRow(string Path, string Status, string Detail)
{
    public override string ToString() => Path + " · " + Status;
}
public sealed class TransferResultPage
{
    public TransferResultRow[] Rows { get; }
    public string Summary { get; }
    public int? NextOffset { get; }
    public TransferResultPage(JsonElement data)
    {
        var summary = data.GetProperty("summary");
        var execution = data.TryGetProperty("execution", out var header) && header.ValueKind == JsonValueKind.Object
            ? header.Deserialize<TransferExecutionHeader>(DebugJson.Options) : null;
        var states = data.GetProperty("executionEntries").Deserialize<TransferItemState[]>(DebugJson.Options)!.ToDictionary(item => item.Index);
        Rows = data.GetProperty("entries").Deserialize<TransferPlanEntry[]>(DebugJson.Options)!.Select(entry =>
        {
            states.TryGetValue(entry.Index, out var state);
            var status = state?.Status ?? "planned";
            var detail = new List<string> { "目标：" + (state?.TargetRelativePath ?? entry.TargetRelativePath), "状态：" + StatusText(status) };
            if ((state?.Error ?? entry.Issue) is { } error) detail.Add("问题：" + error);
            if (state?.TemporaryPath is { } temporary) detail.Add("暂存：" + temporary + $"\n已保存进度：{state.Offset} 字节");
            if (state?.BackupPath is { } backup) detail.Add("备份记录位置：" + backup);
            if (status == "committing") detail.Add("提交回执尚未确认；请从传输记录继续原任务，核对已提交内容和备份。");
            if (state?.Sha256 is { } sha) detail.Add("SHA-256：" + sha);
            if (state?.Permissions is { } permissions) detail.Add("权限：" + permissions);
            return new TransferResultRow(state?.TargetRelativePath ?? entry.TargetRelativePath, StatusText(status), string.Join("\n", detail));
        }).ToArray();
        var statusText = StatusText(execution?.Status ?? summary.GetProperty("status").GetString() ?? "planned");
        Summary = $"{statusText} · 共 {summary.GetProperty("totalEntries").GetInt32()} 项";
        if (execution?.Error is { } errorText) Summary += "\n" + errorText;
        if (execution?.ArchivePath is { } archive) Summary += "\n归档：" + archive;
        NextOffset = data.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
    }
    private static string StatusText(string status) => status switch
    {
        "planned" => "待执行",
        "running" or "verifying" => "执行中",
        "succeeded" => "全部已核验",
        "completed" => "已提交并核验",
        "committing" => "提交待确认",
        "staged" => "归档暂存已核验",
        "committing_archive" => "归档提交待确认",
        "skipped" => "已跳过",
        "staging" => "暂存中",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        "failed" => "失败",
        "rolled_back" => "已回退",
        _ => status
    };
}
