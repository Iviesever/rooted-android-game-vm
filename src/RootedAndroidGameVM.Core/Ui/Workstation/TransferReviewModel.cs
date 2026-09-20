using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public sealed record ConflictChoice(string Value, string Title);
public sealed record TransferPreviewRow(string Path, string Kind, string Size, string Action, string? Issue);
public sealed class TransferReviewModel : ObservableState
{
    private ConflictChoice _policy;
    private bool _stop;
    public TransferReviewModel(JsonElement summary, string destination, IEnumerable<ApplicationRow>? applications = null)
    {
        PlanId = summary.GetProperty("planId").GetString()!;
        Destination = destination;
        var count = summary.GetProperty("totalEntries").GetInt32(); var bytes = summary.GetProperty("totalBytes").GetInt64();
        Description = $"{count} 项 · {bytes / 1048576d:0.##} MiB · " + (summary.GetProperty("direction").GetString() == "upload" ? "电脑 → 安卓" : "安卓 → 电脑");
        var counts = summary.GetProperty("counts");
        CountText = string.Join(" · ", counts.EnumerateObject().Select(item => ActionText(item.Name) + " " + item.Value.GetInt32()));
        Issues = summary.GetProperty("issues").EnumerateArray().Select(value => value.GetString()!).ToArray();
        NeedsArchive = summary.GetProperty("direction").GetString() == "download" && (!summary.TryGetProperty("format", out var format) || format.GetString() != "tar") &&
            Issues.Any(issue => issue.StartsWith("windows_name_unsupported:", StringComparison.Ordinal) || issue.StartsWith("unsupported_entry:", StringComparison.Ordinal) || issue.StartsWith("target_collision:", StringComparison.Ordinal));
        CanExecute = Issues.All(issue => issue.StartsWith("parent_type_conflict:", StringComparison.Ordinal));
        var apps = summary.GetProperty("applicationsToStop").Deserialize<TransferApplication[]>(DebugJson.Options)!;
        Applications = string.Join("、", apps.Select(app => (applications?.FirstOrDefault(row => row.Package == app.Package && row.UserId == app.UserId)?.Name ?? app.Package) + "（用户 " + app.UserId + "）"));
        HasApplications = apps.Length > 0; _stop = HasApplications;
        NeedsReview = HasApplications || Issues.Length > 0 || summary.GetProperty("requiresConflictPolicy").GetBoolean();
        var entries = summary.GetProperty("conflicts").GetArrayLength() > 0 ? summary.GetProperty("conflicts") : summary.GetProperty("preview");
        Rows = entries.Deserialize<TransferPlanEntry[]>(DebugJson.Options)!.Select(entry => new TransferPreviewRow(entry.TargetRelativePath,
            entry.Source.Kind == "directory" ? "文件夹" : entry.Source.Kind == "symlink" ? "链接" : "文件",
            entry.Source.Kind == "directory" ? "—" : $"{entry.Source.Bytes / 1048576d:0.##} MiB", ActionText(entry.Conflict), entry.Issue)).ToArray();
        _policy = Policies[0];
    }
    public string PlanId { get; }
    public string Description { get; }
    public string CountText { get; }
    public string Destination { get; }
    public string Applications { get; }
    public bool HasApplications { get; }
    public bool NeedsReview { get; }
    public bool NeedsArchive { get; }
    public bool CanExecute { get; }
    public string[] Issues { get; }
    public string[] DisplayIssues => Issues.Select(issue =>
    {
        var colon = issue.IndexOf(':'); var code = colon < 0 ? issue : issue[..colon]; var path = colon < 0 ? "" : "：" + issue[(colon + 1)..];
        return (code switch
        {
            "windows_name_unsupported" => "Windows 无法保留此名称，可改用 tar 归档",
            "unsupported_entry" => "此项目需要归档以保留原类型",
            "parent_type_conflict" => "上级路径存在文件与目录冲突",
            "insufficient_space" => "目标空间不足",
            "target_name_collision" => "多个来源对应同一目标名称",
            _ => code
        }) + path;
    }).ToArray();
    public TransferPreviewRow[] Rows { get; }
    public IReadOnlyList<ConflictChoice> Policies { get; } = [new("keep-both", "保留双方（自动改名）"), new("overwrite", "备份后覆盖"), new("skip", "跳过冲突")];
    public ConflictChoice Policy { get => _policy; set => Set(ref _policy, value); }
    public bool StopApplications { get => _stop; set { Set(ref _stop, value); Changed(nameof(ExecuteText)); } }
    public string ExecuteText => StopApplications && HasApplications ? "停止应用并开始传输" : "开始传输";
    public static string ActionText(string action) => action switch { "new" => "新增", "same" => "相同，跳过", "merge" => "合并目录", "different" => "同名内容不同", "type_conflict" => "文件/目录冲突", "blocked" => "需处理", _ => action };
}
