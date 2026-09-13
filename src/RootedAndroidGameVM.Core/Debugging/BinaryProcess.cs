using System.Diagnostics;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Debugging;

public static class BinaryProcess
{
    public static async Task<byte[]> RunAsync(ProcessSpec spec, int limit, CancellationToken ct)
    {
        using var process = Process.Start(ProcessStartInfoFactory.Create(spec)) ?? throw new IOException("无法启动工具。");
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Bound both channels, including failure output. Never decode binary stdout.
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 1024 * 1024, ct);
        try
        {
            var bytes = await ReadBoundedAsync(process.StandardOutput.BaseStream, limit, ct);
            await process.WaitForExitAsync(ct);
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                var detail = System.Text.Encoding.UTF8.GetString(error);
                var code = detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "permission_denied" :
                    detail.Contains("device offline", StringComparison.OrdinalIgnoreCase) || detail.Contains("device not found", StringComparison.OrdinalIgnoreCase) ? "device_offline" :
                    detail.Contains("No space left", StringComparison.OrdinalIgnoreCase) ? "disk_full" : "tool_failed";
                throw new DebugException(code, detail.Length == 0 ? "工具返回非零退出码：" + process.ExitCode : detail);
            }
            return bytes;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            try { await stderr; } catch { /* Original error has precedence. */ }
        }
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        using var target = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + count > limit) throw new DebugException("output_limit", "输出超过安全上限；本次结果已截断并拒绝使用。");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return target.ToArray();
    }
}
