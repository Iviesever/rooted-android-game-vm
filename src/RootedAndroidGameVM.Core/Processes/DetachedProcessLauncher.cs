using System.Diagnostics;

namespace RootedAndroidGameVM.Core.Processes;

public sealed record DetachedProcessHandle(
    Process Process,
    Task CaptureCompletion) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CaptureCompletion.ConfigureAwait(false);
        }
        finally
        {
            Process.Dispose();
        }
    }
}

public sealed class DetachedProcessLauncher
{
    public DetachedProcessHandle Start(
        ProcessSpec spec,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string? diagnosticLogPath = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty
        };
        StreamWriter? diagnosticWriter = null;
        if (!string.IsNullOrWhiteSpace(diagnosticLogPath))
        {
            var fullLogPath = Path.GetFullPath(diagnosticLogPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLogPath)!);
            diagnosticWriter = new StreamWriter(fullLogPath, append: false) { AutoFlush = true };
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{spec.FileName}'.");
            var capture = diagnosticWriter is null
                ? Task.CompletedTask
                : CaptureDiagnosticsAsync(process, diagnosticWriter);
            return new(process, capture);
        }
        catch
        {
            diagnosticWriter?.Dispose();
            throw;
        }
    }

    private static async Task CaptureDiagnosticsAsync(Process process, StreamWriter writer)
    {
        await using var ownedWriter = writer;
        using var writeGate = new SemaphoreSlim(1, 1);

        async Task DrainAsync(StreamReader reader, string channel)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try
                    {
                        await ownedWriter.WriteLineAsync($"[{channel}] {line}").ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Continue draining redirected pipes even if diagnostic storage fails.
                    }
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }

        await Task.WhenAll(
            DrainAsync(process.StandardOutput, "stdout"),
            DrainAsync(process.StandardError, "stderr")).ConfigureAwait(false);
    }
}
