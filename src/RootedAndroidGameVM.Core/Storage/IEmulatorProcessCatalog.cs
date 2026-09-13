namespace RootedAndroidGameVM.Core.Storage;

public sealed record HostProcessIdentity(int ProcessId, int ParentProcessId, string ExecutablePath,
    long StartedAtUtcTicks, string? AvdName, string? AvdDirectory, int? ConsolePort);

public interface IEmulatorProcessCatalog
{
    IReadOnlyList<HostProcessIdentity> FindByExecutable(string executablePath);
    IReadOnlySet<int> GetListenerOwners(int port);
    Task WaitForExitAsync(HostProcessIdentity process, CancellationToken cancellationToken);
    Task TerminateAsync(HostProcessIdentity process, CancellationToken cancellationToken);
}

public sealed record StorageVerificationBinding(int StarterProcessId, int QemuProcessId, long StartedAtUtcTicks,
    int ConsolePort, string AvdDirectory, string ExecutablePath);
