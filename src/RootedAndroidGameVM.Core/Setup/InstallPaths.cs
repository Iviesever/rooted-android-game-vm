using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record InstallPaths(
    string ProductRoot,
    string RuntimeRoot,
    string SdkRoot,
    string JavaHome,
    string AvdHome,
    string RootAvdRoot,
    string DownloadCache)
{
    public static InstallPaths CreateDefault(string? controlRoot = null) =>
        FromProductRoot(new ProductStorageLocation(controlRoot).ReadRoot());

    public static InstallPaths FromProductRoot(string productRoot)
    {
        var root = Path.GetFullPath(productRoot);
        var runtime = PathBoundary.EnsureWithinRoot(root, Path.Combine(root, "runtime"));
        return new(
            root,
            runtime,
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "android-sdk")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "java")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "avd")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "rootavd")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(root, "downloads")));
    }
}
