using Android.Emulation.Control;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

[SupportedOSPlatform("windows")]
public sealed class EmulatorDebugTransport(OwnedInstance instance, int port) : IDisposable
{
    private GrpcChannel? _channel;
    private string? _session;
    private EmulatorController.EmulatorControllerClient? _client;
    private Metadata? _headers;
    private System.Diagnostics.Process? _process;
    private long _processStart;
    private bool _cleaned;
    private readonly TouchLedger _touches = new();
    private readonly object _clientLock = new();
    public string Session => $"{instance.Require().ProcessId}:{instance.Require().StartedAtUtcTicks}";
    private EmulatorController.EmulatorControllerClient Client()
    {
        lock (_clientLock) return Connect();
    }
    private EmulatorController.EmulatorControllerClient Connect()
    {
        if (_client is not null && _process is not null)
        {
            _process.Refresh();
            if (!_process.HasExited && _process.StartTime.ToUniversalTime().Ticks == _processStart) return _client;
            Dispose();
        }
        var process = instance.Require();
        var session = $"{process.ProcessId}:{process.StartedAtUtcTicks}";
        var owners = new WindowsEmulatorProcessCatalog().GetListenerOwners(port);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToArray();
        if (owners.Count != 1 || !owners.Contains(process.ProcessId) || listeners.Length == 0 || listeners.Any(e => !IPAddress.IsLoopback(e.Address)))
            throw new DebugException("grpc_unavailable", "认证 gRPC 未在产品进程的回环地址监听；可重新启动虚拟机启用。");
        if (_session == session && _client is not null) return _client;
        Dispose();
        var tokenPath = Path.Combine(Path.GetTempPath(), "avd", "running", $"pid_{process.ProcessId}.ini");
        StoragePathPolicy.RejectReparsePoints(tokenPath);
        if (!File.Exists(tokenPath)) throw new DebugException("grpc_unavailable", "缺少模拟器会话发现文件。");
        var discovery = File.ReadAllLines(tokenPath).Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        if (discovery.GetValueOrDefault("avd.dir") != process.AvdDirectory || discovery.GetValueOrDefault("grpc.port") != port.ToString())
            throw new DebugException("instance_mismatch", "gRPC 发现文件与已验证的进程不匹配。");
        var token = discovery.GetValueOrDefault("grpc.token") ?? "";
        if (token.Length < 16 || token.Length > 4096 || token.Contains('\n')) throw new DebugException("grpc_unavailable", "模拟器认证凭据无效。");
        _headers = new Metadata { { "authorization", "Bearer " + token } };
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        _session = session;
        _process = System.Diagnostics.Process.GetProcessById(process.ProcessId); _processStart = _process.StartTime.ToUniversalTime().Ticks;
        return _client;
    }
    public async Task<Image> ScreenshotAsync(CancellationToken ct) => await Client().getScreenshotAsync(
        new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png }, _headers, DateTime.UtcNow.AddSeconds(8), ct);
    public async Task<(Image Image, int Width, int Height)> PreviewAsync(CancellationToken ct)
    {
        var client = Client();
        var configurations = await client.getDisplayConfigurationsAsync(new Empty(), _headers, DateTime.UtcNow.AddSeconds(3), ct);
        var primary = configurations.Displays.Single(display => display.Display == 0);
        var image = await client.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png, Width = 960, Height = 540 },
            _headers, DateTime.UtcNow.AddSeconds(3), ct);
        return (image, (int)primary.Width, (int)primary.Height);
    }
    public async Task<bool> IsBlankAsync(CancellationToken ct)
    {
        var image = await Client().getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgba8888, Width = 64, Height = 64 }, _headers, DateTime.UtcNow.AddSeconds(5), ct);
        var bytes = image.Image_.Span;
        if (bytes.Length < 4) return true;
        for (var i = 0; i + 3 < bytes.Length; i += 4) if (bytes[i] > 4 || bytes[i + 1] > 4 || bytes[i + 2] > 4) return false;
        return true;
    }
    public async Task SendAsync(IEnumerable<TouchPoint> points, CancellationToken ct)
    {
        await EnsureCleanAsync(ct);
        var batch = points.ToArray();
        // Preserve the last real coordinate for each owned contact; extra zero-coordinate UP events can affect UI gestures.
        _touches.Apply(batch.Where(p => p.Pressure > 0));
        await SendRawAsync(batch, ct);
        _touches.Apply(batch);
    }
    private async Task SendRawAsync(IEnumerable<TouchPoint> points, CancellationToken ct)
    {
        var request = new TouchEvent { Display = 0 };
        request.Touches.AddRange(points.Select(p => new Touch
        {
            X = p.X,
            Y = p.Y,
            Identifier = p.Id,
            Pressure = p.Pressure,
            Expiration = (Touch.Types.EventExpiration)0
        }));
        await Client().sendTouchAsync(request, _headers, DateTime.UtcNow.AddSeconds(3), ct);
    }
    private async Task EnsureCleanAsync(CancellationToken ct)
    {
        Client();
        if (_cleaned) return;
        await SendRawAsync(Enumerable.Range(0, 10).Select(id => new TouchPoint(id, 0, 0, 0)), ct);
        _cleaned = true;
    }
    public async Task ReleaseAllAsync(CancellationToken ct)
    {
        await EnsureCleanAsync(ct);
        var releases = _touches.Releases();
        if (releases.Length > 0) await SendRawAsync(releases, ct);
        _touches.Clear();
    }
    public async Task<string> ClipboardAsync(string? text, CancellationToken ct)
    {
        var client = Client();
        if (text is not null) await client.setClipboardAsync(new ClipData { Text = text }, _headers, DateTime.UtcNow.AddSeconds(5), ct);
        return (await client.getClipboardAsync(new Empty(), _headers, DateTime.UtcNow.AddSeconds(5), ct)).Text;
    }
    public async Task<object> AuditAuthenticationAsync(CancellationToken ct)
    {
        instance.Require(); var client = Client();
        var rejected = new List<bool>();
        foreach (var metadata in new[] { new Metadata(), new Metadata { { "authorization", "Bearer invalid-test-token" } } })
        {
            try { await client.getStatusAsync(new Empty(), metadata, DateTime.UtcNow.AddSeconds(5), ct); rejected.Add(false); }
            catch (RpcException e) when (e.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied) { rejected.Add(true); }
        }
        await client.getStatusAsync(new Empty(), _headers, DateTime.UtcNow.AddSeconds(5), ct);
        if (rejected.Any(r => !r)) throw new DebugException("authentication_not_enforced", "gRPC 没有拒绝缺少或错误认证的请求。");
        return new { missingTokenRejected = rejected[0], wrongTokenRejected = rejected[1], authenticatedCallSucceeded = true, loopbackOnly = true };
    }
    public void Dispose() { _channel?.Dispose(); _process?.Dispose(); _process = null; _channel = null; _client = null; _headers = null; _session = null; _cleaned = false; _touches.Clear(); }
}
