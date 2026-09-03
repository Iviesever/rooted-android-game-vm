using System.Text.Json;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record InstallJournalState(
    int SchemaVersion,
    string ProductVersion,
    SetupStage Stage,
    string SdkRoot,
    string AvdHome,
    string AvdName,
    string StockRamdiskSha256,
    string PatchedRamdiskSha256,
    DateTimeOffset UpdatedAtUtc);

public sealed class InstallJournalStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public async Task<InstallJournalState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<InstallJournalState>(
            stream,
            cancellationToken: cancellationToken);
    }

    public async Task UpdateAsync(
        SetupStage stage,
        string sdkRoot,
        string avdHome,
        string avdName,
        string? stockRamdiskSha256 = null,
        string? patchedRamdiskSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var previous = await LoadAsync(cancellationToken);
        var state = new InstallJournalState(
            1,
            InstallProfile.ProductVersion,
            stage,
            sdkRoot,
            avdHome,
            avdName,
            ValidateDigest(stockRamdiskSha256 ?? previous?.StockRamdiskSha256 ?? string.Empty),
            ValidateDigest(patchedRamdiskSha256 ?? previous?.PatchedRamdiskSha256 ?? string.Empty),
            DateTimeOffset.UtcNow);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var partial = _path + ".partial";
        try
        {
            await File.WriteAllTextAsync(
                partial,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(partial, _path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static string ValidateDigest(string value)
    {
        if (value.Length == 0) return value;
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Install journal contains an invalid SHA-256 digest.");
        }
        return value.ToLowerInvariant();
    }
}
