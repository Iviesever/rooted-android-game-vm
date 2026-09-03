using System.IO.Compression;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Setup;

public sealed class RootedVmInstaller
{
    private readonly InstallPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly VerifiedDownloader _downloader;
    private AndroidVmOptions _options;

    public RootedVmInstaller(
        InstallPaths? paths = null,
        ProcessRunner? runner = null,
        HttpClient? httpClient = null,
        AndroidVmOptions? options = null)
    {
        _paths = paths ?? InstallPaths.CreateDefault();
        _runner = runner ?? new ProcessRunner();
        _options = options ?? AndroidVmOptions.ProductDefault;
        _downloader = new VerifiedDownloader(httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        });
    }

    public async Task InstallAsync(
        bool sdkLicenseAccepted,
        IProgress<SetupProgressState>? progress = null,
        CancellationToken cancellationToken = default,
        bool adoptExistingEnvironment = false)
    {
        if (!sdkLicenseAccepted)
        {
            throw new InvalidOperationException("必须先阅读并接受 Android SDK 许可协议。");
        }

        Report(progress, SetupStage.Preflight);
        RunPreflight();
        Directory.CreateDirectory(_paths.ProductRoot);
        Directory.CreateDirectory(_paths.RuntimeRoot);
        Directory.CreateDirectory(_paths.DownloadCache);
        await WriteJournalAsync(SetupStage.Preflight, cancellationToken);

        if (adoptExistingEnvironment)
        {
            var existingLayout = AndroidSdkLayout.Discover();
            if (existingLayout.HasRequiredTools)
            {
                var existingController = new AndroidVmController(existingLayout, _options);
                if (await existingController.GetStatusAsync(cancellationToken) != VmStatus.NotInstalled)
                {
                    await VerifyAndRecordAsync(existingLayout, existingController, progress, cancellationToken);
                    return;
                }
            }
        }

        _options = _options with { AvdHome = _options.AvdHome ?? _paths.AvdHome };
        Directory.CreateDirectory(_options.AvdHome);

        Report(progress, SetupStage.Download);
        await WriteJournalAsync(SetupStage.Download, cancellationToken);
        await PrepareJavaAsync(cancellationToken);
        var layout = AndroidSdkLayout.FromRoot(_paths.SdkRoot);
        await PrepareCommandLineToolsAsync(layout, cancellationToken);
        await InstallSdkPackagesAsync(layout, cancellationToken);
        SdkComponentRevisionVerifier.Verify(layout);
        await VerifyAccelerationAsync(layout, cancellationToken);

        Report(progress, SetupStage.CreateAvd);
        await WriteJournalAsync(SetupStage.CreateAvd, cancellationToken);
        await CreateAvdAsync(layout, cancellationToken);
        var controller = new AndroidVmController(layout, _options);
        await controller.StartAsync(cancellationToken);

        Report(progress, SetupStage.Root);
        await WriteJournalAsync(SetupStage.Root, cancellationToken);
        if (await GetRootPreparationStateAsync(layout, cancellationToken) == RootPreparationState.NeedsPatch)
        {
            await PrepareRootToolsAsync(cancellationToken);
            await RestoreStockRamdiskWhenAvailableAsync(layout, cancellationToken);
            await PatchRootAsync(layout, cancellationToken);
            await RecordRamdiskHashesAsync(layout, cancellationToken);
            await controller.StopAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            DeleteAvdInitrdCache();
        }

        await VerifyAndRecordAsync(layout, controller, progress, cancellationToken);
    }

    private async Task<RootPreparationState> GetRootPreparationStateAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        var ramdiskMatchesStock = false;
        var currentHash = File.Exists(ramdisk)
            ? await Security.Sha256Verifier.ComputeAsync(ramdisk, cancellationToken)
            : string.Empty;
        var journal = await Journal.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(journal?.PatchedRamdiskSha256))
        {
            if (string.Equals(currentHash, journal.StockRamdiskSha256, StringComparison.OrdinalIgnoreCase))
            {
                ramdiskMatchesStock = true;
            }
            else if (!string.Equals(currentHash, journal.PatchedRamdiskSha256, StringComparison.OrdinalIgnoreCase))
            {
                return RootPreparationState.NeedsPatch;
            }
        }
        if (File.Exists(ramdisk) && File.Exists(backup))
        {
            var backupHash = await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken);
            ramdiskMatchesStock = ramdiskMatchesStock ||
                string.Equals(currentHash, backupHash, StringComparison.OrdinalIgnoreCase);
        }

        var identity = await _runner.RunAsync(
            AndroidCommandFactory.RootIdentity(layout, _options),
            cancellationToken);
        var su = await _runner.RunAsync(
            AndroidCommandFactory.Adb(layout, _options, "shell", "which", "su"),
            cancellationToken);
        return RootPreparationClassifier.Classify(ramdiskMatchesStock, identity, su);
    }

    private async Task RestoreStockRamdiskWhenAvailableAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        if (File.Exists(backup))
        {
            var journal = await Journal.LoadAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(journal?.StockRamdiskSha256))
            {
                var backupHash = await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken);
                if (!string.Equals(
                        backupHash,
                        journal.StockRamdiskSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "ramdisk.img.backup does not match the install journal stock hash.");
                }
            }
            File.Copy(backup, ramdisk, overwrite: true);
        }
    }

    private async Task RecordRamdiskHashesAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        if (!File.Exists(ramdisk) || !File.Exists(backup))
        {
            throw new FileNotFoundException(
                "RootAVD did not preserve both stock and patched ramdisk files.");
        }
        await Journal.UpdateAsync(
            SetupStage.Root,
            layout.Root,
            _options.AvdHome ?? string.Empty,
            _options.AvdName,
            await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken),
            await Security.Sha256Verifier.ComputeAsync(ramdisk, cancellationToken),
            cancellationToken);
    }

    private void DeleteAvdInitrdCache()
    {
        var avdHome = _options.AvdHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "avd");
        var initrd = Path.Combine(avdHome, $"{_options.AvdName}.avd", "initrd");
        if (File.Exists(initrd))
        {
            File.SetAttributes(initrd, FileAttributes.Normal);
            File.Delete(initrd);
        }
    }

    private void RunPreflight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            !Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException("需要 64 位 Windows 11。");
        }

        var root = Path.GetPathRoot(_paths.ProductRoot)
            ?? throw new InvalidOperationException("无法确定安装磁盘。");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < InstallProfile.MinimumFreeBytes)
        {
            throw new IOException($"安装磁盘至少需要 24 GB 可用空间，当前约 {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB。");
        }
    }

    private async Task PrepareJavaAsync(CancellationToken cancellationToken)
    {
        var component = DependencyManifest.LoadEmbedded().Required("microsoft-openjdk");
        await new ProductArchiveInstaller(_downloader).InstallAsync(
            component,
            _paths.DownloadCache,
            _paths.ProductRoot,
            archiveTopLevelDirectory: null,
            targetRelativeDirectory: Path.GetRelativePath(_paths.ProductRoot, _paths.JavaHome),
            revisionFileRelativePath: "release",
            revisionProperty: "JAVA_VERSION",
            expectedRevision: component.Version,
            cancellationToken);
    }

    private async Task PrepareCommandLineToolsAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var component = DependencyManifest.LoadEmbedded().Required("android-command-line-tools");
        await new ProductArchiveInstaller(_downloader).InstallAsync(
            component,
            _paths.DownloadCache,
            layout.Root,
            "cmdline-tools",
            Path.Combine("cmdline-tools", "latest"),
            "source.properties",
            "Pkg.Revision",
            component.Version,
            cancellationToken);
    }

    private async Task InstallSdkPackagesAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var environment = CreateSdkEnvironment(layout);
        var sdkManager = GetSdkManagerPath(layout);
        var acceptLicenses = new ProcessRequest(
            new ProcessSpec(sdkManager, [$"--sdk_root={layout.Root}", "--licenses"], layout.Root),
            string.Concat(Enumerable.Repeat("y\n", 32)),
            environment);
        EnsureSuccess(await _runner.RunRequestAsync(acceptLicenses, cancellationToken), "接受 Android SDK 许可");

        var manifest = DependencyManifest.LoadEmbedded();
        var archiveInstaller = new SdkArchiveInstaller(_downloader);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-platform-tools"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == "platform-tools"),
            "platform-tools",
            cancellationToken);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-emulator"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == "emulator"),
            "emulator",
            cancellationToken);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-system-image-api35-playstore-x86_64"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == InstallProfile.SystemImagePackage),
            "x86_64",
            cancellationToken);
    }

    private async Task InstallSdkArchiveIfMissingAsync(
        AndroidSdkLayout layout,
        SdkArchiveInstaller archiveInstaller,
        DependencyComponent dependency,
        PinnedSdkComponent component,
        string archiveTopLevelDirectory,
        CancellationToken cancellationToken)
    {
        if (SdkComponentRevisionVerifier.IsInstalled(layout, component))
        {
            await archiveInstaller.EnsureGenericRegistrationAsync(
                dependency,
                layout.Root,
                component.RelativeDirectory,
                cancellationToken);
            return;
        }
        await archiveInstaller.InstallAsync(
            dependency,
            _paths.DownloadCache,
            layout.Root,
            archiveTopLevelDirectory,
            component.RelativeDirectory,
            cancellationToken);
    }

    private async Task VerifyAccelerationAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new ProcessSpec(
                layout.EmulatorPath,
                ["-accel-check"],
                Path.GetDirectoryName(layout.EmulatorPath)),
            cancellationToken);
        EnsureSuccess(result, "检查 Windows Hypervisor Platform");
    }

    private async Task CreateAvdAsync(AndroidSdkLayout layout, CancellationToken cancellationToken)
    {
        var options = _options;
        var list = await _runner.RunRequestAsync(
            AndroidCommandFactory.ListAvds(layout, options),
            cancellationToken);
        var exists = list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.Trim() == options.AvdName);
        if (!exists)
        {
            var avdManager = Path.Combine(layout.Root, "cmdline-tools", "latest", "bin", "avdmanager.bat");
            var request = new ProcessRequest(
                new ProcessSpec(
                    avdManager,
                    ["create", "avd", "--force", "--name", options.AvdName, "--package",
                        InstallProfile.SystemImagePackage, "--device", "pixel_7"],
                    layout.Root),
                "no\n",
                CreateSdkEnvironment(layout));
            EnsureSuccess(await _runner.RunRequestAsync(request, cancellationToken), "创建 Android 虚拟机");
        }

        var config = Path.Combine(
            options.AvdHome ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".android",
                "avd"),
            $"{options.AvdName}.avd",
            "config.ini");
        if (!File.Exists(config))
        {
            throw new FileNotFoundException("AVD 已创建但找不到配置文件。", config);
        }

        await AvdConfigEditor.UpsertAsync(
            config,
            new Dictionary<string, string>
            {
                ["disk.dataPartition.size"] = "12G",
                ["hw.keyboard"] = "yes",
                ["hw.ramSize"] = "4096",
                ["hw.gpu.enabled"] = "yes",
                ["hw.gpu.mode"] = "swiftshader_indirect",
                ["vm.heapSize"] = "512"
            },
            cancellationToken);
    }

    private async Task PrepareRootToolsAsync(CancellationToken cancellationToken)
    {
        var batch = Path.Combine(_paths.RootAvdRoot, "rootAVD.bat");
        if (!File.Exists(batch))
        {
            var archive = await DownloadAsync(InstallProfile.RootAvd, cancellationToken);
            var staging = CreateStagingDirectory("rootavd");
            ZipFile.ExtractToDirectory(archive, staging);
            var source = Directory.GetDirectories(staging).Single();
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.RootAvdRoot)!);
            Directory.Move(source, _paths.RootAvdRoot);
        }

        foreach (var bundled in Directory.EnumerateFiles(_paths.RootAvdRoot, "Magisk*.zip", SearchOption.TopDirectoryOnly))
        {
            File.Delete(bundled);
        }

        var appsDirectory = Path.Combine(_paths.RootAvdRoot, "Apps");
        if (File.Exists(appsDirectory))
        {
            File.Delete(appsDirectory);
        }
        Directory.CreateDirectory(appsDirectory);
        foreach (var oldApk in Directory.EnumerateFiles(appsDirectory, "*.apk", SearchOption.TopDirectoryOnly))
        {
            File.Delete(oldApk);
        }

        var magisk = await DownloadAsync(InstallProfile.Magisk, cancellationToken);
        File.Copy(magisk, Path.Combine(_paths.RootAvdRoot, "Magisk30.zip"), overwrite: true);
        File.Copy(magisk, Path.Combine(_paths.RootAvdRoot, "Magisk.zip"), overwrite: true);
    }

    private async Task PatchRootAsync(AndroidSdkLayout layout, CancellationToken cancellationToken)
    {
        var batch = Path.Combine(_paths.RootAvdRoot, "rootAVD.bat");
        var relativeRamdisk = @"system-images\android-35\google_apis_playstore\x86_64\ramdisk.img";
        if (!File.Exists(batch))
        {
            throw new FileNotFoundException("找不到 RootAVD 批处理入口。", batch);
        }

        await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "rm",
                "-f",
                "/sdcard/Download/fakeboot.img",
                "/sdcard/Download/magisk_patched*.img"),
            cancellationToken);

        var patchedCheck = AndroidCommandFactory.Adb(
            layout,
            _options,
            "shell",
            "ls",
            "/sdcard/Download/magisk_patched*.img");
        var patched = await _runner.RunAsync(patchedCheck, cancellationToken);
        if (patched.ExitCode != 0)
        {
            await RunRootAvdAsync(layout, relativeRamdisk, cancellationToken);
            var fakeBoot = await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    layout,
                    _options,
                    "shell",
                    "test",
                    "-f",
                    "/sdcard/Download/fakeboot.img"),
                cancellationToken);
            EnsureSuccess(fakeBoot, "生成 Magisk FAKEBOOTIMG");

            await new MagiskCliPatchService(layout, _options, _runner)
                .PatchAsync(cancellationToken);
        }

        var finalResult = await RunRootAvdAsync(layout, relativeRamdisk, cancellationToken);
        EnsureSuccess(finalResult, "配置 Root");
    }

    private async Task<ProcessResult> RunRootAvdAsync(
        AndroidSdkLayout layout,
        string relativeRamdisk,
        CancellationToken cancellationToken)
    {
        var command = $"rootAVD.bat {relativeRamdisk} FAKEBOOTIMG";
        var request = new ProcessRequest(
            new ProcessSpec(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["/d", "/s", "/c", command],
                _paths.RootAvdRoot),
            EnvironmentVariables: CreateSdkEnvironment(layout));
        return await _runner.RunRequestAsync(request, cancellationToken);
    }

    private async Task VerifyAndRecordAsync(
        AndroidSdkLayout layout,
        AndroidVmController controller,
        IProgress<SetupProgressState>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, SetupStage.Verify);
        await WriteJournalAsync(SetupStage.Verify, cancellationToken);
        if (await controller.GetStatusAsync(cancellationToken) != VmStatus.Running)
        {
            await controller.StartAsync(cancellationToken);
        }

        await EnsureMagiskAppInstalledAsync(layout, cancellationToken);
        var diagnostics = await controller.DiagnoseAsync(cancellationToken);
        var policyAutomator = new MagiskPolicyAutomator(layout, _options, _runner);
        if (!diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal))
        {
            await policyAutomator.GrantShellAsync(cancellationToken);
            diagnostics = await controller.DiagnoseAsync(cancellationToken);
            if (!diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Root 最终验证失败。\n{diagnostics}");
            }
        }
        else
        {
            await policyAutomator.PersistCurrentShellPolicyAsync(cancellationToken);
        }

        await VerifyPersistentRootAndHealthAsync(layout, controller, cancellationToken);

        Directory.CreateDirectory(_paths.ProductRoot);
        var marker = new
        {
            version = InstallProfile.ProductVersion,
            sdkRoot = layout.Root,
            avdName = _options.AvdName,
            avdHome = _options.AvdHome,
            verifiedAtUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ProductRoot, "install.json"),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        Report(progress, SetupStage.Complete);
        await WriteJournalAsync(SetupStage.Complete, cancellationToken);
    }

    private async Task EnsureMagiskAppInstalledAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var package = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "pm",
                "path",
                "com.topjohnwu.magisk"),
            cancellationToken);
        if (package.ExitCode == 0 && package.StandardOutput.Contains("package:", StringComparison.Ordinal))
        {
            return;
        }

        var apk = await DownloadAsync(InstallProfile.Magisk, cancellationToken);
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.InstallApk(layout, _options, apk),
            cancellationToken), "安装 Magisk 管理应用");
    }

    private async Task WriteJournalAsync(
        SetupStage stage,
        CancellationToken cancellationToken)
    {
        await Journal.UpdateAsync(
            stage,
            _paths.SdkRoot,
            _options.AvdHome ?? string.Empty,
            _options.AvdName,
            cancellationToken: cancellationToken);
    }

    private async Task VerifyPersistentRootAndHealthAsync(
        AndroidSdkLayout layout,
        AndroidVmController controller,
        CancellationToken cancellationToken)
    {
        var bootIdBefore = await ReadBootIdAsync(layout, cancellationToken);
        await controller.StopAsync(cancellationToken);
        await controller.StartAsync(cancellationToken);
        var bootIdAfter = await ReadBootIdAsync(layout, cancellationToken);
        if (string.Equals(bootIdBefore, bootIdAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("冷重启验证失败：Android boot ID 未发生变化。");
        }

        var diagnostics = await controller.DiagnoseAsync(cancellationToken);
        if (!diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"冷重启后 Root 未保持。{Environment.NewLine}{diagnostics}");
        }

        var version = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "su",
                "-c",
                "magisk -v"),
            cancellationToken);
        EnsureSuccess(version, "验证 Magisk 版本");
        if (!version.StandardOutput.Contains("30.6", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Magisk 版本不匹配，预期 30.6，实际为 {version.StandardOutput.Trim()}。");
        }

        const string healthFile = "/data/adb/rgvm-health";
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "su",
                "-c",
                $"touch {healthFile}"),
            cancellationToken), "验证 Root 数据写入");
        var readBack = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "su",
                "-c",
                $"test -f {healthFile}"),
            cancellationToken);
        EnsureSuccess(readBack, "验证 Root 数据读取");
        await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "su",
                "-c",
                $"rm -f {healthFile}"),
            CancellationToken.None);

        const string screenshot = "/data/local/tmp/rgvm-health.png";
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "screencap",
                "-p",
                screenshot),
            cancellationToken), "验证图形截图");
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "test",
                "-s",
                screenshot),
            cancellationToken), "验证图形截图内容");
        await _runner.RunAsync(
            AndroidCommandFactory.Adb(layout, _options, "shell", "rm", "-f", screenshot),
            CancellationToken.None);
    }

    private async Task<string> ReadBootIdAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "cat",
                "/proc/sys/kernel/random/boot_id"),
            cancellationToken);
        EnsureSuccess(result, "读取 Android boot ID");
        return result.StandardOutput.Trim();
    }

    private async Task<string> DownloadAsync(
        PinnedDependency dependency,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_paths.DownloadCache, dependency.FileName);
        if (File.Exists(destination))
        {
            var hash = await Security.Sha256Verifier.ComputeAsync(destination, cancellationToken);
            if (hash.Equals(dependency.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }
        }

        await _downloader.DownloadAsync(dependency.Source, destination, dependency.Sha256, cancellationToken);
        return destination;
    }

    private string CreateStagingDirectory(string component)
    {
        var path = Path.Combine(_paths.RuntimeRoot, "staging", $"{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSdkManagerPath(AndroidSdkLayout layout) =>
        Path.Combine(layout.Root, "cmdline-tools", "latest", "bin", "sdkmanager.bat");

    private IReadOnlyDictionary<string, string> CreateSdkEnvironment(AndroidSdkLayout layout)
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANDROID_HOME"] = layout.Root,
            ["ANDROID_SDK_ROOT"] = layout.Root,
            ["ANDROID_AVD_HOME"] = _options.AvdHome ?? string.Empty,
            ["JAVA_HOME"] = _paths.JavaHome,
            ["PATH"] = string.Join(Path.PathSeparator,
                Path.Combine(_paths.JavaHome, "bin"),
                Path.Combine(layout.Root, "platform-tools"),
                inheritedPath)
        };
    }

    private static void Report(IProgress<SetupProgressState>? progress, SetupStage stage) =>
        progress?.Report(SetupProgressCatalog.All.Single(state => state.Stage == stage));

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var combined = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        var detail = combined.Length > 6000 ? combined[^6000..] : combined;
        throw new InvalidOperationException($"{operation}失败：{detail}");
    }

    private InstallJournalStore Journal =>
        new(Path.Combine(_paths.ProductRoot, "install-state.json"));

}
