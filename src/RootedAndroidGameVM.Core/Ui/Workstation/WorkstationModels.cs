using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public interface IWorkstationApi
{
    Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null);
    Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken);
}

public enum WorkstationSection { Android, Applications, Files, Diagnostics, Automation, Checkpoints, Settings }
public sealed record WorkstationNavigation(WorkstationSection Section, string Title, string Glyph, string Description);
public sealed class ApplicationRow(string package, string name, string? appRef = null, int userId = 0,
    string? iconPath = null, bool system = false, string? installationRevision = null, string runningState = "未观察") : ObservableState
{
    private string _name = name, _runningState = runningState;
    private string? _iconPath = iconPath;
    public string Package { get; } = package;
    public string Name => _name;
    public string? AppRef { get; } = appRef;
    public int UserId { get; } = userId;
    public string? IconPath => _iconPath;
    public bool System { get; } = system;
    public string? InstallationRevision { get; } = installationRevision;
    public string RunningState => _runningState;
    public string DisplayText => $"{Name} · {Package}";
    public string Detail => $"{Package} · 用户 {UserId} · {RunningState}" + (System ? " · 系统应用" : "");
    public void RefreshMetadata(ApplicationRow row)
    {
        if (row.AppRef != AppRef) throw new ArgumentException("应用身份不同。");
        Set(ref _name, row.Name, nameof(Name)); Set(ref _iconPath, row.IconPath, nameof(IconPath));
        Set(ref _runningState, row.RunningState, nameof(RunningState));
        Changed(nameof(DisplayText)); Changed(nameof(Detail));
    }
}
public sealed record RendererChoice(string Value, string Title);
public sealed record FileRow(string Name, string Kind, long? Bytes, string Permissions)
{
    public bool IsDirectory => Kind.Contains("directory", StringComparison.OrdinalIgnoreCase);
    public string TypeText => IsDirectory ? "文件夹" : Kind.Contains("link", StringComparison.OrdinalIgnoreCase) ? "链接" : "文件";
    public string SizeText => IsDirectory || Bytes is null ? "—" : Bytes >= 1048576 ? $"{Bytes / 1048576d:0.##} MiB" : Bytes >= 1024 ? $"{Bytes / 1024d:0.#} KiB" : $"{Bytes} B";
    public static FileRow Parse(JsonElement entry)
    {
        var details = entry.GetProperty("details").GetString()!.Split('|');
        return new(entry.GetProperty("name").GetString()!, details[0], details.Length > 1 && long.TryParse(details[1], out var bytes) ? bytes : null,
            details.Length >= 5 ? $"{details[2]}:{details[3]} · {details[4]}" : "不可用");
    }
}
public sealed record CheckpointRow(string Id, string Path);
public sealed record FileScope(string Value, string Title, string Detail);
public sealed class WorkItem(string title) : ObservableState
{
    private string _status = "正在开始";
    private string? _id;
    private string? _directory;
    public string Title { get; } = title;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string? Id { get => _id; set => Set(ref _id, value); }
    public string? Directory { get => _directory; set => Set(ref _directory, value); }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public bool Completed { get; set; }
    public string Details { get; set; } = "任务尚未完成。";
}
