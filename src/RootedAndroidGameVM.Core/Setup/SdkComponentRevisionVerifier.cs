using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Setup;

public static class SdkComponentRevisionVerifier
{
    public static bool IsInstalled(AndroidSdkLayout layout, PinnedSdkComponent component) =>
        string.Equals(ReadRevision(layout, component), component.Revision, StringComparison.Ordinal);

    public static void Verify(AndroidSdkLayout layout)
    {
        foreach (var component in InstallProfile.SdkComponents)
        {
            var propertiesPath = Path.Combine(layout.Root, component.RelativeDirectory, "source.properties");
            if (!File.Exists(propertiesPath))
            {
                throw new FileNotFoundException(
                    $"Android SDK component '{component.PackagePath}' is missing source.properties.",
                    propertiesPath);
            }

            var revision = ReadRevision(layout, component);
            if (!string.Equals(revision, component.Revision, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Android SDK component '{component.PackagePath}' revision mismatch. " +
                    $"Expected {component.Revision}, got {revision ?? "<missing>"}.");
            }
        }
    }

    private static string? ReadRevision(AndroidSdkLayout layout, PinnedSdkComponent component)
    {
        var propertiesPath = Path.Combine(layout.Root, component.RelativeDirectory, "source.properties");
        if (!File.Exists(propertiesPath)) return null;
        return File.ReadLines(propertiesPath)
            .FirstOrDefault(line => line.StartsWith("Pkg.Revision=", StringComparison.Ordinal))
            ?.Split('=', 2)[1]
            .Trim();
    }
}
