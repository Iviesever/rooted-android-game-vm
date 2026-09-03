using System.IO.Compression;
using System.Security.Cryptography;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ProductArchiveInstallerContractTests
{
    [Fact]
    public async Task Partial_existing_tool_directory_is_repaired_from_a_verified_archive()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-tool-repair", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchive("cmdline-tools/source.properties", "Pkg.Revision=22.0");
            var cache = Path.Combine(root, "downloads");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, "tools.zip"), bytes);
            var target = Path.Combine(root, "sdk", "cmdline-tools", "latest");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "partial.txt"), "broken");
            var component = Component(bytes);

            await new ProductArchiveInstaller(new VerifiedDownloader(new HttpClient(new ThrowingHandler())))
                .InstallAsync(
                    component, cache, Path.Combine(root, "sdk"), "cmdline-tools",
                    Path.Combine("cmdline-tools", "latest"), "source.properties", "Pkg.Revision", "22.0");

            Assert.Equal("Pkg.Revision=22.0", await File.ReadAllTextAsync(
                Path.Combine(target, "source.properties")));
            Assert.False(File.Exists(Path.Combine(target, "partial.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_tool_replacement_rolls_back_the_previous_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-tool-rollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchive("cmdline-tools/source.properties", "Pkg.Revision=wrong");
            var cache = Path.Combine(root, "downloads");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, "tools.zip"), bytes);
            var target = Path.Combine(root, "sdk", "cmdline-tools", "latest");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "old-proof.txt"), "keep");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProductArchiveInstaller(new VerifiedDownloader(new HttpClient(new ThrowingHandler())))
                    .InstallAsync(
                        Component(bytes), cache, Path.Combine(root, "sdk"), "cmdline-tools",
                        Path.Combine("cmdline-tools", "latest"), "source.properties", "Pkg.Revision", "22.0"));

            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "old-proof.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static DependencyComponent Component(byte[] bytes) => new(
        "tools", "Tools", "15859902", "https://example.invalid/tools.zip", "tools.zip",
        Convert.ToHexStringLower(SHA256.HashData(bytes)), string.Empty, bytes.Length,
        true, "NOASSERTION", "direct");

    private static byte[] CreateArchive(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write(content);
        }
        return stream.ToArray();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Verified cached archive should avoid the network.");
    }
}
