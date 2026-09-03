using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class AndroidControlContractTests
{
    [Fact]
    public void Sdk_layout_uses_explicit_root_and_known_tool_paths()
    {
        var layout = AndroidSdkLayout.FromRoot(@"C:\Android\Sdk");

        Assert.Equal(Path.GetFullPath(@"C:\Android\Sdk\platform-tools\adb.exe"), layout.AdbPath);
        Assert.Equal(Path.GetFullPath(@"C:\Android\Sdk\emulator\emulator.exe"), layout.EmulatorPath);
    }

    [Fact]
    public void Start_command_has_stable_game_friendly_arguments()
    {
        var layout = AndroidSdkLayout.FromRoot(@"C:\Android\Sdk");
        var command = AndroidCommandFactory.StartEmulator(layout, AndroidVmOptions.Default);

        Assert.Equal(layout.EmulatorPath, command.FileName);
        Assert.Contains("rooted_android_game_vm_api35", command.Arguments);
        Assert.Contains("swiftshader_indirect", command.Arguments);
        Assert.Contains("-no-snapshot-load", command.Arguments);
    }

    [Theory]
    [InlineData("com.rhythm.game")]
    [InlineData("me.zhanghai.android.files")]
    public void Valid_android_package_names_are_accepted(string packageName)
    {
        Assert.Equal(packageName, AndroidPackageName.Parse(packageName).Value);
    }

    [Theory]
    [InlineData("../data")]
    [InlineData("com.rhythm.game;rm")]
    [InlineData("moe low arc")]
    public void Unsafe_android_package_names_are_rejected(string packageName)
    {
        Assert.Throws<ArgumentException>(() => AndroidPackageName.Parse(packageName));
    }

    [Fact]
    public void Apk_install_command_keeps_file_path_as_one_argument()
    {
        var layout = AndroidSdkLayout.FromRoot(@"C:\Android\Sdk");
        var apk = @"D:\Downloads\my game.apk";

        var command = AndroidCommandFactory.InstallApk(layout, AndroidVmOptions.Default, apk);

        Assert.Equal(apk, command.Arguments[^1]);
        Assert.Contains("-r", command.Arguments);
    }

    [Theory]
    [InlineData("files/dl")]
    [InlineData("databases")]
    [InlineData("shared_prefs/settings.xml")]
    public void Safe_private_data_relative_paths_are_accepted(string relativePath)
    {
        Assert.Equal(relativePath, AndroidRelativePath.Parse(relativePath).Value);
    }

    [Theory]
    [InlineData("../files")]
    [InlineData("/data/data")]
    [InlineData("files;rm")]
    [InlineData("files//dl")]
    public void Unsafe_private_data_relative_paths_are_rejected(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => AndroidRelativePath.Parse(relativePath));
    }

    [Fact]
    public void Third_party_package_list_parser_returns_safe_distinct_names()
    {
        const string output = "package:com.example.game\r\npackage:com.rhythm.game\npackage:com.example.game\n";

        Assert.Equal(
            ["com.example.game", "com.rhythm.game"],
            AndroidPackageListParser.Parse(output));
    }

    [Fact]
    public void Force_stop_command_keeps_validated_package_as_a_separate_argument()
    {
        var layout = AndroidSdkLayout.FromRoot(@"C:\Android\Sdk");
        var command = AndroidCommandFactory.ForceStopPackage(
            layout,
            AndroidVmOptions.Default,
            AndroidPackageName.Parse("com.example.game"));

        Assert.Equal(["-s", "emulator-5554", "shell", "am", "force-stop", "com.example.game"],
            command.Arguments);
    }

    [Fact]
    public void Uninstall_command_keeps_validated_package_as_a_separate_argument()
    {
        var layout = AndroidSdkLayout.FromRoot(@"C:\Android\Sdk");
        var command = AndroidCommandFactory.UninstallPackage(
            layout,
            AndroidVmOptions.Default,
            AndroidPackageName.Parse("com.example.game"));

        Assert.Equal(["-s", "emulator-5554", "uninstall", "com.example.game"], command.Arguments);
    }

    [Fact]
    public void List_avds_request_carries_the_product_scoped_avd_home()
    {
        var layout = AndroidSdkLayout.FromRoot(@"D:\Product\Sdk");
        var options = AndroidVmOptions.Default with { AvdHome = @"D:\Product\Avd" };

        var request = AndroidCommandFactory.ListAvds(layout, options);

        Assert.Equal(["-list-avds"], request.Spec.Arguments);
        Assert.Equal(@"D:\Product\Avd", request.EnvironmentVariables!["ANDROID_AVD_HOME"]);
    }

    [Fact]
    public void Product_default_uses_a_product_scoped_non_legacy_avd_name()
    {
        var options = AndroidVmOptions.ProductDefault;

        Assert.Equal("rooted_android_game_vm_api35", options.AvdName);
        Assert.NotNull(options.AvdHome);
        Assert.Contains("RootedAndroidGameVM", options.AvdHome, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".android", options.AvdHome, StringComparison.OrdinalIgnoreCase);
    }
}
