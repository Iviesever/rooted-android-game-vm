using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class RootShellPathTests
{
    [Fact]
    public void Root_identity_resolves_magisk_mount_paths_before_invoking_su()
    {
        var command = AndroidCommandFactory.RootIdentity(AndroidSdkLayout.FromRoot(@"D:\SDK"), AndroidVmOptions.Default);
        Assert.Contains("/debug_ramdisk", string.Join(" ", command.Arguments));
        Assert.Contains("/sbin", string.Join(" ", command.Arguments));
    }

    [Fact]
    public void Root_script_keeps_quotes_newlines_and_substitutions_inside_one_guest_argument()
    {
        var command = AndroidCommandFactory.RootShell(
            AndroidSdkLayout.FromRoot(@"D:\SDK"), AndroidVmOptions.Default,
            "printf '%s' 'literal $(id)'\nprintf done");
        Assert.Equal(new[]
        {
            "-s", "emulator-5554", "shell",
            "export PATH=/debug_ramdisk:/sbin:$PATH; exec su -c " +
            "'export PATH=/debug_ramdisk:/sbin:$PATH; printf '\"'\"'%s'\"'\"' '\"'\"'literal $(id)'\"'\"'\nprintf done'"
        }, command.Arguments);
    }
}
