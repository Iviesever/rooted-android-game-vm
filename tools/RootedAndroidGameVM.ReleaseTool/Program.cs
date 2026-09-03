using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Release;
using RootedAndroidGameVM.Core.Security;

if (args.Length == 6 && args[0] == "generate-sbom")
{
    await SbomGenerator.GenerateAsync(
        DependencyManifest.LoadEmbedded(),
        args[1],
        [
            new("RootedAndroidGameVM.exe", args[2]),
            new("RootedAndroidGameVM.Setup.exe", args[3]),
            new(Path.GetFileName(args[4]), args[4])
        ],
        args[5]);
    Console.WriteLine($"Generated SPDX SBOM: {args[5]}");
    return 0;
}

if (args.Length == 7 && args[0] == "verify-apk-export")
{
    return await VerifyApkExportAsync(args);
}

if (args.Length == 3 && args[0] == "download")
{
    var component = DependencyManifest.LoadEmbedded().Required(args[1]);
    if (component.Sha256.Length != 64)
    {
        throw new InvalidDataException($"Dependency '{component.Id}' has no SHA-256.");
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    var destination = Path.GetFullPath(args[2]);
    var cached = File.Exists(destination) &&
                 string.Equals(
                     await Sha256Verifier.ComputeAsync(destination),
                     component.Sha256,
                     StringComparison.OrdinalIgnoreCase);
    if (!cached)
    {
        await new VerifiedDownloader(client).DownloadAsync(
            new Uri(component.Url),
            destination,
            component.Sha256);
    }
    Console.WriteLine($"Verified download: {destination}");
    return 0;
}

Console.Error.WriteLine(
    "Commands:\n" +
    "  generate-sbom <version> <launcher.exe> <setup.exe> <installer.exe> <output.json>\n" +
    "  verify-apk-export <sdk-root> <avd-home> <avd-name> <port> <apk> <export-root>\n" +
    "  download <dependency-id> <destination>");
return 2;

static async Task<int> VerifyApkExportAsync(string[] arguments)
{
    var sdkRoot = Path.GetFullPath(arguments[1]);
    var avdHome = Path.GetFullPath(arguments[2]);
    var avdName = arguments[3];
    if (!int.TryParse(arguments[4], out var port) || port % 2 != 0)
    {
        throw new ArgumentException("Emulator port must be even.");
    }
    var apk = Path.GetFullPath(arguments[5]);
    var exportRoot = Path.GetFullPath(arguments[6]);
    var options = new AndroidVmOptions(
        avdName,
        $"emulator-{port}",
        port,
        "swiftshader_indirect",
        4096,
        avdHome);
    var layout = AndroidSdkLayout.FromRoot(sdkRoot);
    var controller = new AndroidVmController(layout, options);
    var runner = new ProcessRunner();
    var operationId = Guid.NewGuid().ToString("N");
    var localScript = Path.Combine(Path.GetTempPath(), $"rgvm-app-e2e-{operationId}.sh");
    var remoteScript = $"/data/local/tmp/rgvm-app-e2e-{operationId}.sh";
    const string packageName = "me.zhanghai.android.files";
    const string probeText = "RootedAndroidGameVM-E2E";
    try
    {
        await controller.StartAsync();
        await controller.InstallApkAsync(apk);
        var packages = await controller.ListThirdPartyPackagesAsync();
        if (!packages.Contains(packageName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Pinned E2E APK was not returned by Package Manager.");
        }

        await File.WriteAllTextAsync(
            localScript,
            "#!/system/bin/sh\n" +
            "set -e\n" +
            $"mkdir -p /data/data/{packageName}/files/rgvm_e2e\n" +
            $"printf '{probeText}' > /data/data/{packageName}/files/rgvm_e2e/probe.txt\n");
        EnsureSuccess(await runner.RunAsync(
            AndroidCommandFactory.Adb(layout, options, "push", localScript, remoteScript)),
            "push E2E private-data probe");
        EnsureSuccess(await runner.RunAsync(
            AndroidCommandFactory.Adb(layout, options, "shell", "su", "-c", $"sh {remoteScript}")),
            "create E2E private-data probe");

        var exported = await new AndroidPrivateDataService(layout, options)
            .ExportDirectoryAsync(packageName, "files/rgvm_e2e", exportRoot);
        var probe = await File.ReadAllTextAsync(Path.Combine(exported, "probe.txt"));
        if (probe != probeText)
        {
            throw new InvalidDataException("Exported private-data probe content does not match.");
        }
        Console.WriteLine($"Verified APK install and private-data export: {exported}");
        return 0;
    }
    finally
    {
        File.Delete(localScript);
        try
        {
            await runner.RunAsync(
                AndroidCommandFactory.Adb(layout, options, "shell", "rm", "-f", remoteScript));
            await controller.StopAsync();
        }
        catch
        {
            // The primary verification result remains authoritative.
        }
    }
}

static void EnsureSuccess(ProcessResult result, string operation)
{
    if (result.ExitCode == 0) return;
    throw new InvalidOperationException(
        $"{operation} failed: {result.StandardError}{result.StandardOutput}");
}
