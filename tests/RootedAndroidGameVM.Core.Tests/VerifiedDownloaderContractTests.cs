using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class VerifiedDownloaderContractTests
{
    [Fact]
    public async Task Downloader_only_commits_content_when_sha256_matches()
    {
        var downloaderType = typeof(RootedAndroidGameVM.Core.Class1).Assembly
            .GetType("RootedAndroidGameVM.Core.Downloads.VerifiedDownloader");
        Assert.NotNull(downloaderType);

        var payload = Encoding.UTF8.GetBytes("hello");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var client = new HttpClient(new StaticContentHandler(payload));
        var downloader = Activator.CreateInstance(downloaderType, client);
        Assert.NotNull(downloader);

        var method = downloaderType.GetMethod("DownloadAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var root = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "payload.bin");
            var invocation = method.Invoke(downloader, new object[]
            {
                new Uri("https://example.invalid/payload.bin"), destination, expectedHash, CancellationToken.None
            });
            var task = Assert.IsAssignableFrom<Task>(invocation);
            await task;

            Assert.Equal("hello", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Downloader_deletes_partial_content_when_sha256_is_wrong()
    {
        var downloaderType = typeof(RootedAndroidGameVM.Core.Class1).Assembly
            .GetType("RootedAndroidGameVM.Core.Downloads.VerifiedDownloader");
        Assert.NotNull(downloaderType);

        using var client = new HttpClient(new StaticContentHandler(Encoding.UTF8.GetBytes("wrong")));
        var downloader = Activator.CreateInstance(downloaderType, client);
        var method = downloaderType.GetMethod("DownloadAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(downloader);
        Assert.NotNull(method);

        var root = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "payload.bin");
        try
        {
            var invocation = method.Invoke(downloader, new object[]
            {
                new Uri("https://example.invalid/payload.bin"), destination, new string('0', 64), CancellationToken.None
            });
            var task = Assert.IsAssignableFrom<Task>(invocation);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await task);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Downloader_resumes_a_partial_file_with_an_http_range_request()
    {
        var root = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "payload.bin");
            await File.WriteAllTextAsync(destination + ".partial", "hel");
            using var client = new HttpClient(new RangeContentHandler());
            var downloader = new RootedAndroidGameVM.Core.Downloads.VerifiedDownloader(client);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello")));

            await downloader.DownloadAsync(
                new Uri("https://example.invalid/payload.bin"),
                destination,
                expectedHash);

            Assert.Equal("hello", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticContentHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class RangeContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(3, request.Headers.Range?.Ranges.Single().From);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("lo"))
            });
        }
    }
}
