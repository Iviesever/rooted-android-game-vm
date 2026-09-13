using System.IO.Compression;

namespace RootedAndroidGameVM.Core.IO;

public static class ImportArchivePolicy
{
    public static void Validate(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 1 or > 4096) throw new InvalidDataException("导入包的文件数量超限。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(part => part is "." or ".." or "") || !names.Add(name))
                throw new InvalidDataException("导入包包含重复项或越界路径。");
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException("导入包包含链接或特殊文件。");
            if (entry.Length > 512L * 1024 * 1024 || (total += entry.Length) > 512L * 1024 * 1024)
                throw new InvalidDataException("导入包解包后超过 512 MiB。");
        }
    }
}
