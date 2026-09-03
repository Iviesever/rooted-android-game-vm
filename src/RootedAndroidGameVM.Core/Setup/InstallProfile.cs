using RootedAndroidGameVM.Core.Dependencies;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record PinnedDependency(
    string Name,
    Uri Source,
    string Sha256,
    string FileName);

public sealed record PinnedSdkComponent(
    string PackagePath,
    string RelativeDirectory,
    string Revision);

public static class InstallProfile
{
    private static readonly DependencyManifest Manifest = DependencyManifest.LoadEmbedded();

    public const string ProductVersion = "0.1.2";
    public const string SystemImagePackage = "system-images;android-35;google_apis_playstore;x86_64";
    public const long MinimumFreeBytes = 24L * 1024 * 1024 * 1024;

    public static PinnedDependency CommandLineTools { get; } =
        Direct("android-command-line-tools");

    public static PinnedDependency OpenJdk { get; } =
        Direct("microsoft-openjdk");

    public static PinnedDependency RootAvd { get; } =
        Direct("rootavd");

    public static PinnedDependency Magisk { get; } =
        Direct("magisk");

    public static IReadOnlyList<PinnedDependency> Dependencies { get; } =
        [CommandLineTools, OpenJdk, RootAvd, Magisk];

    public static IReadOnlyList<string> SdkPackages { get; } =
        ["platform-tools", "emulator", SystemImagePackage];

    public static IReadOnlyList<PinnedSdkComponent> SdkComponents { get; } =
    [
        Sdk("android-platform-tools", "platform-tools", "platform-tools"),
        Sdk("android-emulator", "emulator", "emulator"),
        Sdk(
            "android-system-image-api35-playstore-x86_64",
            SystemImagePackage,
            Path.Combine("system-images", "android-35", "google_apis_playstore", "x86_64"))
    ];

    private static PinnedDependency Direct(string id)
    {
        var component = Manifest.Required(id);
        if (component.Sha256.Length != 64 || !component.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Dependency '{id}' requires a SHA-256 digest.");
        }
        return new(
            $"{component.Name} {component.Version}",
            new Uri(component.Url),
            component.Sha256,
            component.ArchiveFileName);
    }

    private static PinnedSdkComponent Sdk(
        string id,
        string packagePath,
        string relativeDirectory)
    {
        var component = Manifest.Required(id);
        return new(packagePath, relativeDirectory, component.Version);
    }
}
