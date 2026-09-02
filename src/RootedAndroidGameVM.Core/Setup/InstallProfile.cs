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
    public const string ProductVersion = "0.1.0";
    public const string SystemImagePackage = "system-images;android-35;google_apis_playstore;x86_64";
    public const long MinimumFreeBytes = 24L * 1024 * 1024 * 1024;

    public static PinnedDependency CommandLineTools { get; } = new(
        "Android SDK Command-line Tools 15859902",
        new Uri("https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip"),
        "90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a",
        "commandlinetools-win-15859902.zip");

    public static PinnedDependency OpenJdk { get; } = new(
        "Microsoft Build of OpenJDK 21.0.12.1",
        new Uri("https://download.visualstudio.microsoft.com/download/pr/f1e5f23f-9d50-4b9f-8ed3-80522ae82bb5/71e8e5f0f13419cc726e470d25e0a0d0/microsoft-jdk-21.0.12.1-windows-x64.zip"),
        "192441a9d27da813bada974bb88b4cf64d37a9589ed37f204374d411ca5ce07f",
        "microsoft-jdk-21.0.12.1-windows-x64.zip");

    public static PinnedDependency RootAvd { get; } = new(
        "rootAVD 92df40e",
        new Uri("https://github.com/galihlasahido/rootAVD/archive/92df40eafa2f117053f56015e3c32ca706a55fa9.zip"),
        "d97b924d9399cdc609c04bdce36a7a6de54327d6e46f828ca50c78611cf545d4",
        "rootavd-92df40e.zip");

    public static PinnedDependency Magisk { get; } = new(
        "Magisk 30.6",
        new Uri("https://github.com/topjohnwu/Magisk/releases/download/v30.6/Magisk-v30.6.apk"),
        "f1ffc3c9a5614c251ba6bada308163acc3c3d844cf01d33f55a8bc151adc34ce",
        "Magisk-v30.6.apk");

    public static IReadOnlyList<PinnedDependency> Dependencies { get; } =
        [CommandLineTools, OpenJdk, RootAvd, Magisk];

    public static IReadOnlyList<string> SdkPackages { get; } =
        ["platform-tools", "emulator", SystemImagePackage];

    public static IReadOnlyList<PinnedSdkComponent> SdkComponents { get; } =
    [
        new("platform-tools", "platform-tools", "37.0.1"),
        new("emulator", "emulator", "37.1.11"),
        new(SystemImagePackage, Path.Combine("system-images", "android-35", "google_apis_playstore", "x86_64"), "9")
    ];
}
