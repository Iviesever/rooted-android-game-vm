using System.Text.Json;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Release;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SbomGeneratorContractTests
{
    [Fact]
    public async Task Generated_spdx_contains_every_manifest_component_and_actual_file_sha256()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sbom", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifact = Path.Combine(root, "RootedAndroidGameVM.exe");
            var output = Path.Combine(root, "SBOM.spdx.json");
            await File.WriteAllTextAsync(artifact, "binary");

            await SbomGenerator.GenerateAsync(
                DependencyManifest.LoadEmbedded(),
                "0.1.0",
                [new SbomInputFile("RootedAndroidGameVM.exe", artifact)],
                output);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var packages = document.RootElement.GetProperty("packages");
            Assert.Equal(
                DependencyManifest.LoadEmbedded().Components.Count + 1,
                packages.GetArrayLength());
            var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal("RootedAndroidGameVM.exe", file.GetProperty("fileName").GetString());
            Assert.Equal(
                64,
                file.GetProperty("checksums")[0].GetProperty("checksumValue").GetString()!.Length);
            Assert.Contains(
                file.GetProperty("checksums").EnumerateArray(),
                checksum => checksum.GetProperty("algorithm").GetString() == "SHA1" &&
                            checksum.GetProperty("checksumValue").GetString()!.Length == 40);
            var rootPackage = packages.EnumerateArray().Single(item =>
                item.GetProperty("SPDXID").GetString() == "SPDXRef-Package-RootedAndroidGameVM");
            Assert.True(rootPackage.GetProperty("filesAnalyzed").GetBoolean());
            Assert.Equal(
                40,
                rootPackage.GetProperty("packageVerificationCode")
                    .GetProperty("packageVerificationCodeValue").GetString()!.Length);
            Assert.DoesNotContain(
                packages.EnumerateArray(),
                package => package.GetProperty("licenseDeclared").GetString()!
                    .StartsWith("LicenseRef-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
