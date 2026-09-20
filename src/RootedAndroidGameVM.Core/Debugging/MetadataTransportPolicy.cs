using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

public static class MetadataTransportPolicy
{
    public static bool CanRetryRead(DebugException error)
    {
        if (error.Code != "tool_failed" || error.Data["toolEvidencePath"] is not string path) return false;
        try
        {
            if (new FileInfo(path).Length > 65536) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var evidence = document.RootElement;
            if (evidence.GetProperty("exitCode").GetInt32() != 255 || evidence.GetProperty("cancelled").GetBoolean() ||
                !evidence.GetProperty("stdoutComplete").GetBoolean() || !evidence.GetProperty("stderrComplete").GetBoolean()) return false;
            // Empty output alone is not enough: require the captured tool exit and both drained pipes.
            return new FileInfo(evidence.GetProperty("stdoutPath").GetString()!).Length == 0 &&
                new FileInfo(evidence.GetProperty("stderrPath").GetString()!).Length == 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return false; }
    }
}
