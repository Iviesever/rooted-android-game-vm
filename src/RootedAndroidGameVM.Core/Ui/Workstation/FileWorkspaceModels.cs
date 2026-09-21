using System.Collections.ObjectModel;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public static class FileSizeText
{
    public static string Format(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.##} GiB" :
        bytes >= 1048576 ? $"{bytes / 1048576d:0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KiB" : $"{bytes} B";
}

public sealed class FileTreeNode(FileRootDescriptor root, string relativePath, string name, string? cursor = null, FileTreeNode? parent = null) : ObservableState
{
    public FileRootDescriptor Root { get; } = root;
    public string RelativePath { get; } = relativePath;
    public string Name { get; } = name;
    public string? Cursor { get; set; } = cursor;
    public bool IsMore => Cursor is not null;
    public bool Loaded { get; set; }
    public bool Loading { get; set; }
    public FileTreeNode? Parent { get; } = parent;
    public bool Accessible => Root.Accessible;
    public override string ToString() => Detail;
    public ObservableCollection<FileTreeNode> Children { get; } = [];
    public string Detail => Root.Accessible ? Name : Name + " · " + (Root.Reason switch
    {
        "data_locked" => "用户未解锁",
        "not_created" => Root.Creatable ? "上传时创建" : "尚未生成",
        "metadata_unavailable" => "系统未提供",
        _ => Root.Reason ?? "不可访问"
    });
}
public sealed record FileBreadcrumb(string Title, string RelativePath);
public sealed record FileEntryRow(RemoteFileEntry Entry)
{
    public string Name => Entry.Name;
    public override string ToString() => Name + " · " + TypeText;
    public bool IsDirectory => Entry.Kind == "directory";
    public string TypeText => Entry.Kind switch { "directory" => "文件夹", "file" => "文件", "symlink" => "链接", _ => "特殊项目" };
    public string SizeText => IsDirectory ? "—" : FileSizeText.Format(Entry.Bytes);
    public string PermissionText => $"{Entry.Uid}:{Entry.Gid} · {Entry.Mode}";
    public string ModifiedText => DateTimeOffset.FromUnixTimeMilliseconds(Entry.ModifiedUnixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
public sealed record FileTransferHistoryRow(string PlanId, string Direction, string Status, DateTimeOffset UpdatedAt,
    int TotalEntries, long TotalBytes, string ArtifactDirectory, bool CanResume, string? JobId = null)
{
    public string DirectionText => Direction == "upload" ? "上传" : "下载";
    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public override string ToString() => DirectionText + " · " + StatusText + " · " + PlanId;
    public string StatusText => Status switch { "planned" => "待执行", "running" or "verifying" => "执行中", "succeeded" => "已核验", "cancelled" => "已取消", "interrupted" => "已中断", _ => "未完成" };
}
public sealed class ExportRootChoice(FileRootDescriptor root) : ObservableState
{
    private bool _selected = root.Accessible && root.Kind != "shared";
    public FileRootDescriptor Root { get; } = root;
    public string Title => Root.Title + (Root.Accessible ? "" : "（不可访问）");
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
