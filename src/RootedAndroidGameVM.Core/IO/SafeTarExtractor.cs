using System.Formats.Tar;
namespace RootedAndroidGameVM.Core.IO;

public static class SafeTarExtractor
{
    public static void Extract(string archive, string target)
    {
        using var stream = File.OpenRead(archive); using var reader = new TarReader(stream);
        long total = 0; var count = 0; TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++count > 100000 || (total += entry.Length) > 16L * 1024 * 1024 * 1024) throw new IOException("导出归档超过安全上限。");
            var name = entry.Name.TrimEnd('/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name is "" or ".") continue;
            if (Path.IsPathRooted(name) || name.Contains(':') || name.Split('/', '\\').Any(p => p is ".." or "." or "")) throw new IOException("归档路径越界。");
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw new IOException("不允许归档内的链接或特殊设备。");
            var path = PathBoundary.EnsureWithinRoot(target, Path.Combine(target, name));
            Storage.StoragePathPolicy.RejectReparsePoints(path);
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, false); }
        }
    }
}
