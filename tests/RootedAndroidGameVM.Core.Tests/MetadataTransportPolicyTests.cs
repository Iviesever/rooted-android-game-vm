using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class MetadataTransportPolicyTests
{
    [Theory]
    [InlineData(255, false, "", "", true)]
    [InlineData(1, false, "", "", false)]
    [InlineData(255, true, "", "", false)]
    [InlineData(255, false, "partial-json", "", false)]
    [InlineData(255, false, "", "permission denied", false)]
    public void Only_a_captured_empty_transport_exit_is_eligible(int exit, bool cancelled, string output, string errorText, bool retry)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-metadata-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var stdout = Path.Combine(directory, "stdout.txt"); var stderr = Path.Combine(directory, "stderr.txt");
            File.WriteAllText(stdout, output); File.WriteAllText(stderr, errorText);
            var path = Path.Combine(directory, "tool.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { exitCode = exit, cancelled, stdoutComplete = true, stderrComplete = true, stdoutPath = stdout, stderrPath = stderr }));
            var error = new DebugException("tool_failed", "exit"); error.Data["toolEvidencePath"] = path;
            Assert.Equal(retry, MetadataTransportPolicy.CanRetryRead(error));
            File.Delete(stdout); Assert.False(MetadataTransportPolicy.CanRetryRead(error));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void Missing_or_untrusted_incomplete_evidence_does_not_trigger_replay() =>
        Assert.False(MetadataTransportPolicy.CanRetryRead(new DebugException("tool_failed", "exit 255")));
}
