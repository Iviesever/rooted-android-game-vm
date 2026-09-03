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

    [Fact]
    public async Task Downloader_retries_transient_http_failures_before_succeeding()
    {
        var payload = Encoding.UTF8.GetBytes("eventual");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var handler = new TransientFailureHandler(payload, failures: 2);
        using var client = new HttpClient(handler);
        var downloader = new RootedAndroidGameVM.Core.Downloads.VerifiedDownloader(
            client,
            _ => TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), "rgvm-retry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "payload.bin");
            await downloader.DownloadAsync(
                new Uri("https://example.invalid/payload.bin"),
                destination,
                expectedHash);

            Assert.Equal(3, handler.Attempts);
            Assert.Equal("eventual", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Downloader_promotes_a_complete_verified_partial_without_a_network_request()
    {
        var payload = Encoding.UTF8.GetBytes("already complete");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var root = Path.Combine(Path.GetTempPath(), "rgvm-complete-partial", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(destination + ".partial", payload);
            using var client = new HttpClient(new ThrowingHandler());

            await new RootedAndroidGameVM.Core.Downloads.VerifiedDownloader(client)
                .DownloadAsync(new Uri("https://example.invalid/payload.bin"), destination, expectedHash);

            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Downloader_restarts_from_zero_after_a_range_not_satisfiable_response()
    {
        var payload = Encoding.UTF8.GetBytes("fresh payload");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var root = Path.Combine(Path.GetTempPath(), "rgvm-http-416", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "payload.bin");
            await File.WriteAllTextAsync(destination + ".partial", "stale partial");
            var handler = new RangeNotSatisfiableThenFullHandler(payload);
            using var client = new HttpClient(handler);

            await new RootedAndroidGameVM.Core.Downloads.VerifiedDownloader(client)
                .DownloadAsync(new Uri("https://example.invalid/payload.bin"), destination, expectedHash);

            Assert.Equal(2, handler.Attempts);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
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

    private sealed class TransientFailureHandler(byte[] payload, int failures) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= failures)
            {
                throw new HttpRequestException("transient TLS failure");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A verified complete partial must not use the network.");
    }

    private sealed class RangeNotSatisfiableThenFullHandler(byte[] payload) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                Assert.NotNull(request.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }
            Assert.Null(request.Headers.Range);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }
}
