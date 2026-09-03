using System.Text.Json;

namespace RootedAndroidGameVM.Core.Dependencies;

public sealed record DependencyComponent(
    string Id,
    string Name,
    string Version,
    string Url,
    string ArchiveFileName,
    string Sha256,
    string UpstreamSha1,
    long Size,
    bool DownloadAtInstall,
    string License,
    string Delivery);

public sealed record DependencyManifest(
    int SchemaVersion,
    IReadOnlyList<string> GeneratedFrom,
    IReadOnlyList<DependencyComponent> Components)
{
    public static DependencyManifest LoadEmbedded()
    {
        var assembly = typeof(DependencyManifest).Assembly;
        using var stream = assembly.GetManifestResourceStream("RootedAndroidGameVM.dependencies.json")
            ?? throw new InvalidOperationException("Embedded dependency manifest is missing.");
        return JsonSerializer.Deserialize<DependencyManifest>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("Embedded dependency manifest is invalid.");
    }

    public DependencyComponent Required(string id) =>
        Components.SingleOrDefault(component => component.Id == id)
        ?? throw new InvalidDataException($"Dependency manifest is missing '{id}'.");
}
