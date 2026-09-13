using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Storage;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Setup;

public static class ProgramUpgradeProbe
{
    public static async Task<bool> CanReuseAsync(InstallPaths paths, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!StorageOwnership.IsOwned(paths.ProductRoot)) return false;
            var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
            if (!layout.HasRequiredTools || !File.Exists(Path.Combine(paths.AvdHome, "rooted_android_game_vm_api35.avd", "config.ini")))
                return false;
            SdkComponentRevisionVerifier.Verify(layout);
            var journal = await new InstallJournalStore(Path.Combine(paths.ProductRoot, "install-state.json"))
                .LoadAsync(cancellationToken).ConfigureAwait(false);
            if (journal is not { SchemaVersion: 1, Stage: SetupStage.Complete } ||
                journal.AvdName != "rooted_android_game_vm_api35" ||
                !string.Equals(journal.SdkRoot, paths.SdkRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journal.AvdHome, paths.AvdHome, StringComparison.OrdinalIgnoreCase) ||
                journal.PatchedRamdiskSha256.Length != 64) return false;
            var image = InstallProfile.SdkComponents.Single(component => component.PackagePath == InstallProfile.SystemImagePackage);
            var ramdisk = Path.Combine(paths.SdkRoot, image.RelativeDirectory, "ramdisk.img");
            return string.Equals(await Sha256Verifier.ComputeAsync(ramdisk, cancellationToken).ConfigureAwait(false),
                journal.PatchedRamdiskSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
