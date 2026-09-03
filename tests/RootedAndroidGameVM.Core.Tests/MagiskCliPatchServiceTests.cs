using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class MagiskCliPatchServiceTests
{
    [Fact]
    public void Direct_patch_script_uses_official_boot_patch_assets_and_fixed_output()
    {
        var script = MagiskCliPatchService.BuildPatchScript();

        Assert.Contains("assets/boot_patch.sh", script, StringComparison.Ordinal);
        Assert.Contains("./busybox sh ./boot_patch.sh /sdcard/Download/fakeboot.img", script, StringComparison.Ordinal);
        Assert.Contains("/sdcard/Download/magisk_patched-rgvm.img", script, StringComparison.Ordinal);
        Assert.DoesNotContain("input tap", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Android_shell_script_encoding_is_utf8_without_bom_and_uses_lf_only()
    {
        var bytes = UnixShellScriptEncoding.Encode("#!/system/bin/sh\r\nset -e\r\n");

        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes("#!/system/bin/sh\nset -e\n"),
            bytes);
        Assert.False(bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble));
    }
}
