namespace RootedAndroidGameVM.Core.IO;

public static class PathBoundary
{
    public static string EnsureWithinRoot(string root, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var fullRoot = Path.GetFullPath(root);
        var fullTarget = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(fullRoot, fullTarget);

        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{fullTarget}' is outside allowed root '{fullRoot}'.");
        }

        return fullTarget;
    }
}
