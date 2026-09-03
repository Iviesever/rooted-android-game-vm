using System.Net;
using System.Net.Http.Headers;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Downloads;

public sealed class VerifiedDownloader
{
    private readonly HttpClient _httpClient;
    private readonly Func<int, TimeSpan> _retryDelay;

    public VerifiedDownloader(HttpClient httpClient)
        : this(httpClient, null)
    {
    }

    public VerifiedDownloader(
        HttpClient httpClient,
        Func<int, TimeSpan>? retryDelay)
    {
        _httpClient = httpClient;
        _retryDelay = retryDelay ?? (attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt) - 1));
    }

    public async Task DownloadAsync(
        Uri source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(source, destination, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < 4 &&
                !cancellationToken.IsCancellationRequested &&
                (exception is HttpRequestException ||
                 exception is TaskCanceledException ||
                 exception is IOException))
            {
                await Task.Delay(_retryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadOnceAsync(
        Uri source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
        }

        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(directory);

        var partialPath = fullDestination + ".partial";
        try
        {
            var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existingLength > 0)
            {
                var partialHash = await Sha256Verifier.ComputeAsync(partialPath, cancellationToken)
                    .ConfigureAwait(false);
                if (partialHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(partialPath, fullDestination, overwrite: true);
                    return;
                }
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await SendAsync(source, existingLength, cancellationToken).ConfigureAwait(false);
                if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.Dispose();
                    File.Delete(partialPath);
                    existingLength = 0;
                    response = await SendAsync(source, 0, cancellationToken).ConfigureAwait(false);
                }

                response.EnsureSuccessStatusCode();
                var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (existingLength > 0 && !append)
                {
                    File.Delete(partialPath);
                }

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(
                    partialPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                response?.Dispose();
            }

            var actualSha256 = await Sha256Verifier.ComputeAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new InvalidDataException(
                    $"SHA-256 mismatch for '{source}'. Expected {expectedSha256}, got {actualSha256}.");
            }

            File.Move(partialPath, fullDestination, overwrite: true);
        }
        catch (InvalidDataException)
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        long existingLength,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }
}
