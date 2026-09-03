namespace RootedAndroidGameVM.Core.Release;

public static class ReleaseContentAuditor
{
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apk", ".apks", ".xapk", ".img", ".vhd", ".vhdx", ".qcow2",
        ".keystore", ".jks", ".pfx", ".key"
    };

    private static readonly HashSet<string> ForbiddenFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "secrets.json", "appsettings.Local.json"
    };

    public static IReadOnlyList<string> FindViolations(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);

        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        return Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .Where(path => ForbiddenExtensions.Contains(Path.GetExtension(path))
                || ForbiddenFileNames.Contains(Path.GetFileName(path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
