using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Launcher.Workstation;

public sealed class BrokerWorkstationApi : IWorkstationApi
{
    private readonly DebugClient _client = new();
    public Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null)
        => _client.ExecuteAndWaitAsync(request, cancellationToken, progress);
    public Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken) => _client.ReadPreviewAsync(cancellationToken);
}
