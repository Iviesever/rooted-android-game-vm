using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Android;

public interface IAndroidVmLifecycle
{
    Task<VmStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<string> DiagnoseAsync(CancellationToken cancellationToken = default);
}
