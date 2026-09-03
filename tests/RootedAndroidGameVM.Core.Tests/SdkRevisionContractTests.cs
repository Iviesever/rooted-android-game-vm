using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SdkRevisionContractTests
{
    [Fact]
    public void Installed_sdk_revisions_must_match_the_release_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-revisions", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var component in InstallProfile.SdkComponents)
            {
                var directory = Path.Combine(root, component.RelativeDirectory);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "source.properties"), $"Pkg.Revision={component.Revision}\n");
            }

            SdkComponentRevisionVerifier.Verify(AndroidSdkLayout.FromRoot(root));
            Assert.True(SdkComponentRevisionVerifier.IsInstalled(
                AndroidSdkLayout.FromRoot(root),
                InstallProfile.SdkComponents[0]));

            File.WriteAllText(Path.Combine(root, "emulator", "source.properties"), "Pkg.Revision=999.0.0\n");
            Assert.False(SdkComponentRevisionVerifier.IsInstalled(
                AndroidSdkLayout.FromRoot(root),
                InstallProfile.SdkComponents[1]));
            Assert.Throws<InvalidDataException>(() =>
                SdkComponentRevisionVerifier.Verify(AndroidSdkLayout.FromRoot(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
