using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Release;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;
using System.Text.Json;

if (args.Length == 2 && args[0] == "inspect-storage")
{
    var location = new ProductStorageLocation(args[1]);
    var root = location.ReadRoot();
    var inventory = VerifiedDirectoryCopy.ReadInventory(root, StorageOwnership.ControlFiles);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        resourceRoot = root, fileCount = inventory.Files.Count, bytes = inventory.TotalBytes,
        compatibleRuntime = await ProgramUpgradeProbe.CanReuseAsync(InstallPaths.FromProductRoot(root)),
        pendingMigration = MigrationJournal.Read(location)
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length == 2 && args[0] == "verify-storage")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
    var paths = InstallPaths.FromProductRoot(args[1]);
    if (!StorageOwnership.IsOwned(paths.ProductRoot)) throw new InvalidOperationException("Unknown product resource root.");
    var runtime = new AndroidStorageRuntime(new TracingProcessCatalog());
    var verified = false;
    try
    {
        await runtime.VerifyAsync(paths, CancellationToken.None);
        verified = true;
        Console.WriteLine("Fresh storage runtime and Root verification passed.");
    }
    catch (Exception exception) { Console.Error.WriteLine(exception); }
    finally { await runtime.StopAsync(paths, CancellationToken.None); }
    return verified ? 0 : 1;
}

if (args.Length == 2 && args[0] == "stop-storage")
{
    var paths = InstallPaths.FromProductRoot(args[1]);
    if (!StorageOwnership.IsOwned(paths.ProductRoot)) throw new InvalidOperationException("Unknown product resource root.");
    await new AndroidStorageRuntime().StopAsync(paths, CancellationToken.None);
    Console.WriteLine("Product resource processes stopped.");
    return 0;
}

if (args.Length == 3 && args[0] == "migrate-storage")
{
    var lastStage = string.Empty;
    var progress = new Progress<ResourceTransferProgress>(value =>
    {
        if (value.Stage == lastStage) return;
        lastStage = value.Stage;
        Console.WriteLine($"{value.Stage}: {value.CompletedBytes}/{value.TotalBytes}");
    });
    var result = await new ResourceMigrationService(new ProductStorageLocation(args[1]))
        .MigrateAsync(args[2], progress);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return result.HasPendingCleanup ? 1 : 0;
}

if (args.Length == 2 && args[0] == "recover-storage")
{
    var result = await new ResourceMigrationService(new ProductStorageLocation(args[1])).RecoverAsync();
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return result.HasPendingCleanup ? 1 : 0;
}

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

if (args.Length == 7 && args[0] is "verify-apk-export" or "verify-apk-update-export")
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
    "  verify-apk-update-export <sdk-root> <avd-home> <avd-name> <port> <apk> <export-root>\n" +
    "  inspect-storage <control-root>\n" +
    "  stop-storage <product-root>\n" +
    "  verify-storage <product-root>\n" +
    "  migrate-storage <control-root> <target-root>\n" +
    "  recover-storage <control-root>\n" +
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
    var preserveProbe = arguments[0] == "verify-apk-update-export";
    try
    {
        await controller.StartAsync();
        if (preserveProbe)
        {
            var before = await runner.RunAsync(AndroidCommandFactory.RootShell(layout, options,
                $"cat /data/data/{packageName}/files/rgvm_e2e/probe.txt"));
            EnsureSuccess(before, "read preserved E2E private-data probe before APK update");
            if (before.StandardOutput != probeText)
                throw new InvalidDataException("Pre-existing dummy probe output mismatch: " +
                    Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(before.StandardOutput)));
        }
        await controller.InstallApkAsync(apk);
        var packages = await controller.ListThirdPartyPackagesAsync();
        if (!packages.Contains(packageName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Pinned E2E APK was not returned by Package Manager.");
        }

        if (!preserveProbe)
        {
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
                AndroidCommandFactory.RootShell(layout, options, $"sh {remoteScript}")),
                "create E2E private-data probe");
            }

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

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class TracingProcessCatalog : IEmulatorProcessCatalog
{
    private readonly WindowsEmulatorProcessCatalog _inner = new();
    public IReadOnlyList<HostProcessIdentity> FindByExecutable(string path)
    {
        var result = _inner.FindByExecutable(path);
        Console.WriteLine(JsonSerializer.Serialize(new { requestedExecutable = path, processes = result }));
        return result;
    }
    public IReadOnlySet<int> GetListenerOwners(int port) => _inner.GetListenerOwners(port);
    public Task WaitForExitAsync(HostProcessIdentity identity, CancellationToken token) => _inner.WaitForExitAsync(identity, token);
    public Task TerminateAsync(HostProcessIdentity identity, CancellationToken token) => _inner.TerminateAsync(identity, token);
}
