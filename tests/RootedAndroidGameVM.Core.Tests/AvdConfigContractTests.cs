using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class AvdConfigContractTests
{
    [Fact]
    public async Task Avd_config_upsert_is_idempotent_and_removes_duplicate_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rgvm-config-{Guid.NewGuid():N}.ini");
        try
        {
            await File.WriteAllTextAsync(path, "hw.gpu.mode=auto\nhw.ramSize=2048\nhw.gpu.mode=host\n");
            await AvdConfigEditor.UpsertAsync(path, new Dictionary<string, string>
            {
                ["hw.gpu.mode"] = "swiftshader_indirect",
                ["hw.ramSize"] = "4096"
            });

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(1, lines.Count(line => line.StartsWith("hw.gpu.mode=", StringComparison.Ordinal)));
            Assert.Contains("hw.gpu.mode=swiftshader_indirect", lines);
            Assert.Contains("hw.ramSize=4096", lines);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
