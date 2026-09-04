using System.Net;
using System.Net.Sockets;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class StorageRuntimeIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-identity-tests", Guid.NewGuid().ToString("N"));
    private InstallPaths Paths => InstallPaths.FromProductRoot(_root);
    private string Qemu => Path.Combine(Paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe");
    private string AvdDirectory => Path.Combine(Paths.AvdHome, "rooted_android_game_vm_api35.avd");

    [Fact]
    public async Task Same_name_device_in_another_sdk_is_left_alone_and_verification_uses_a_new_port()
    {
        var host = new FakeCatalog();
        host.Add(new(100, 50, @"D:\other-sdk\qemu-system-x86_64.exe", 1000, "rooted_android_game_vm_api35", @"D:\personal.avd", 5554));
        var factory = new FakeFactory(host, Qemu, AvdDirectory);
        await Runtime(host, factory).VerifyAsync(Paths, CancellationToken.None);
        Assert.NotEqual(5554, factory.StartedOptions!.Port);
        Assert.InRange(factory.StartedOptions.Port, 5570, 5584);
        Assert.True(factory.GuestPrepared);
        Assert.Contains(host.Processes, process => process.ProcessId == 100);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Port_theft_or_other_datadir_is_rejected_before_guest_preparation(bool stealPort, bool wrongDirectory)
    {
        var host = new FakeCatalog();
        var factory = new FakeFactory(host, Qemu, AvdDirectory) { StealPort = stealPort, WrongDirectory = wrongDirectory };
        await Assert.ThrowsAsync<IOException>(() => Runtime(host, factory).VerifyAsync(Paths, CancellationToken.None));
        Assert.False(factory.GuestPrepared);
        Assert.False(factory.Diagnosed);
    }

    [Fact]
    public async Task Reused_pid_is_not_allowed_to_replace_the_verified_process()
    {
        var host = new FakeCatalog();
        var factory = new FakeFactory(host, Qemu, AvdDirectory) { ReusePidDuringDiagnosis = true };
        var runtime = Runtime(host, factory);
        await Assert.ThrowsAsync<IOException>(() => runtime.VerifyAsync(Paths, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => runtime.StopAsync(Paths, CancellationToken.None));
        Assert.Empty(factory.StoppedPorts);
    }

    [Fact]
    public async Task Stop_rejects_a_same_sdk_instance_with_a_different_avd_directory()
    {
        var host = new FakeCatalog();
        host.Add(new(200, 50, Qemu, 1000, "rooted_android_game_vm_api35", Path.Combine(_root, "other.avd"), 5554));
        var factory = new FakeFactory(host, Qemu, AvdDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(host, factory).StopAsync(Paths, CancellationToken.None));
        Assert.Empty(factory.StoppedPorts);
    }

    [Fact]
    public async Task Recovery_stops_only_the_recorded_dedicated_instance()
    {
        var host = new FakeCatalog();
        var target = new HostProcessIdentity(200, 777, Qemu, 1000, "rooted_android_game_vm_api35", AvdDirectory, 5572);
        host.Add(target);
        host.Add(new(100, 50, @"D:\other-sdk\qemu-system-x86_64.exe", 500, "rooted_android_game_vm_api35", @"D:\other.avd", 5554));
        var factory = new FakeFactory(host, Qemu, AvdDirectory);
        var binding = new StorageVerificationBinding(777, 200, 1000, 5572, AvdDirectory, Qemu);
        var runtime = Runtime(host, factory, binding);
        await runtime.StopAsync(Paths, CancellationToken.None);
        Assert.Equal([5572], factory.StoppedPorts);
        Assert.Contains(host.Processes, process => process.ProcessId == 100);
        Assert.DoesNotContain(host.Processes, process => process.ProcessId == 200);
    }

    [Fact]
    public void Windows_listener_catalog_reports_the_current_process_for_a_real_socket()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(Environment.ProcessId, new WindowsEmulatorProcessCatalog().GetListenerOwners(port));
    }

    [Fact]
    public void Windows_process_catalog_reads_a_real_process_identity_and_creation_time()
    {
        if (!OperatingSystem.IsWindows()) return;
        var identities = new WindowsEmulatorProcessCatalog().FindByExecutable(Environment.ProcessPath!);
        var current = Assert.Single(identities, process => process.ProcessId == Environment.ProcessId);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks / 10, current.StartedAtUtcTicks / 10);
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), current.ExecutablePath, ignoreCase: true);
    }

    private AndroidStorageRuntime Runtime(FakeCatalog host, FakeFactory factory, StorageVerificationBinding? binding = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "install.json"), "{}");
        return new(host, new SuccessRunner(), factory.Create, readBinding: () => binding);
    }

    private sealed class FakeCatalog : IEmulatorProcessCatalog
    {
        public List<HostProcessIdentity> Processes { get; } = [];
        public Dictionary<int, HashSet<int>> Owners { get; } = [];
        public void Add(HostProcessIdentity process)
        {
            Processes.Add(process);
            if (process.ConsolePort is { } port) { Owners[port] = [process.ProcessId]; Owners[port + 1] = [process.ProcessId]; }
        }
        public IReadOnlyList<HostProcessIdentity> FindByExecutable(string path) => Processes.Where(process => process.ExecutablePath == path).ToList();
        public IReadOnlySet<int> GetListenerOwners(int port) => Owners.GetValueOrDefault(port) ?? [];
        public Task WaitForExitAsync(HostProcessIdentity process, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TerminateAsync(HostProcessIdentity process, CancellationToken cancellationToken) => throw new InvalidOperationException("No adb process should be selected by these tests.");
    }

    private sealed class FakeFactory(FakeCatalog host, string executable, string directory)
    {
        public bool StealPort { get; init; }
        public bool WrongDirectory { get; init; }
        public bool ReusePidDuringDiagnosis { get; init; }
        public bool GuestPrepared { get; private set; }
        public bool Diagnosed { get; private set; }
        public AndroidVmOptions? StartedOptions { get; private set; }
        public List<int> StoppedPorts { get; } = [];

        public IAndroidVmLifecycle Create(AndroidSdkLayout layout, AndroidVmOptions options, Func<int, CancellationToken, Task>? guard) =>
            new FakeLifecycle(async token =>
            {
                StartedOptions = options;
                host.Add(new(200, 777, executable, 1000, options.AvdName,
                    WrongDirectory ? directory + "-other" : directory, options.Port));
                if (StealPort) host.Owners[options.Port] = [999];
                if (guard is not null) await guard(777, token);
                GuestPrepared = true;
            }, () =>
            {
                Diagnosed = true;
                if (ReusePidDuringDiagnosis) host.Processes[0] = host.Processes[0] with { StartedAtUtcTicks = 2000 };
                return "Root：正常（uid=0）";
            }, () =>
            {
                StoppedPorts.Add(options.Port);
                host.Processes.RemoveAll(process => process.ConsolePort == options.Port && process.ExecutablePath == executable);
                host.Owners.Remove(options.Port);
                host.Owners.Remove(options.Port + 1);
            });
    }

    private sealed class FakeLifecycle(Func<CancellationToken, Task> start, Func<string> diagnose, Action stop) : IAndroidVmLifecycle
    {
        public Task<VmStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(VmStatus.Running);
        public Task StartAsync(CancellationToken cancellationToken = default) => start(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) { stop(); return Task.CompletedTask; }
        public Task<string> DiagnoseAsync(CancellationToken cancellationToken = default) => Task.FromResult(diagnose());
    }

    private sealed class SuccessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(new ProcessResult(0, "", ""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
