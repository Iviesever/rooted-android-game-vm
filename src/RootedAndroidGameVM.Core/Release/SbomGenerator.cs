using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Release;

public sealed record SbomInputFile(string Name, string Path);

public static class SbomGenerator
{
    public static async Task GenerateAsync(
        DependencyManifest manifest,
        string productVersion,
        IReadOnlyList<SbomInputFile> files,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var packages = new JsonArray
        {
            Package(
                "SPDXRef-Package-RootedAndroidGameVM",
                "RootedAndroidGameVM",
                productVersion,
                "MIT",
                "NOASSERTION")
        };
        var relationships = new JsonArray
        {
            Relationship(
                "SPDXRef-DOCUMENT",
                "DESCRIBES",
                "SPDXRef-Package-RootedAndroidGameVM")
        };
        foreach (var component in manifest.Components)
        {
            var packageId = $"SPDXRef-Dependency-{SanitizeId(component.Id)}";
            packages.Add(Package(
                packageId,
                component.Name,
                component.Version,
                component.License,
                component.Url));
            relationships.Add(component.Delivery switch
            {
                "build-tool" => Relationship(
                    packageId,
                    "BUILD_TOOL_OF",
                    "SPDXRef-Package-RootedAndroidGameVM"),
                "test-only" => Relationship(
                    packageId,
                    "TEST_DEPENDENCY_OF",
                    "SPDXRef-Package-RootedAndroidGameVM"),
                _ => Relationship(
                    "SPDXRef-Package-RootedAndroidGameVM",
                    "DEPENDS_ON",
                    packageId)
            });
        }

        var spdxFiles = new JsonArray();
        var fileSha1Digests = new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
            {
                throw new FileNotFoundException("SBOM input file is missing.", file.Path);
            }

            var digest = await Sha256Verifier.ComputeAsync(file.Path, cancellationToken);
            var sha1Digest = await ComputeSha1Async(file.Path, cancellationToken);
            fileSha1Digests.Add(sha1Digest);
            var fileId = $"SPDXRef-File-{SanitizeId(file.Name)}";
            spdxFiles.Add(new JsonObject
            {
                ["SPDXID"] = fileId,
                ["fileName"] = file.Name,
                ["checksums"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["algorithm"] = "SHA256",
                        ["checksumValue"] = digest
                    },
                    new JsonObject
                    {
                        ["algorithm"] = "SHA1",
                        ["checksumValue"] = sha1Digest
                    }
                },
                ["licenseConcluded"] = "NOASSERTION",
                ["copyrightText"] = "NOASSERTION"
            });
            relationships.Add(Relationship(
                "SPDXRef-Package-RootedAndroidGameVM",
                "CONTAINS",
                fileId));
        }

        var packageVerificationInput = string.Concat(
            fileSha1Digests.Order(StringComparer.Ordinal));
        var packageVerificationCode = Convert.ToHexStringLower(
            SHA1.HashData(Encoding.ASCII.GetBytes(packageVerificationInput)));
        var rootPackage = (JsonObject)packages[0]!;
        rootPackage["filesAnalyzed"] = true;
        rootPackage["packageVerificationCode"] = new JsonObject
        {
            ["packageVerificationCodeValue"] = packageVerificationCode
        };

        var document = new JsonObject
        {
            ["spdxVersion"] = "SPDX-2.3",
            ["dataLicense"] = "CC0-1.0",
            ["SPDXID"] = "SPDXRef-DOCUMENT",
            ["name"] = $"RootedAndroidGameVM-{productVersion}",
            ["documentNamespace"] =
                $"https://github.com/RootedAndroidGameVM/RootedAndroidGameVM/sbom/{productVersion}/{Guid.NewGuid():N}",
            ["creationInfo"] = new JsonObject
            {
                ["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["creators"] = new JsonArray("Tool: RootedAndroidGameVM.ReleaseTool")
            },
            ["packages"] = packages,
            ["files"] = spdxFiles,
            ["relationships"] = relationships
        };

        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await File.WriteAllTextAsync(
            fullOutput,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static JsonObject Package(
        string id,
        string name,
        string version,
        string license,
        string downloadLocation) =>
        new()
        {
            ["SPDXID"] = id,
            ["name"] = name,
            ["versionInfo"] = version,
            ["downloadLocation"] = downloadLocation,
            ["filesAnalyzed"] = false,
            ["licenseConcluded"] = license,
            ["licenseDeclared"] = license,
            ["copyrightText"] = "NOASSERTION"
        };

    private static JsonObject Relationship(string source, string type, string target) =>
        new()
        {
            ["spdxElementId"] = source,
            ["relationshipType"] = type,
            ["relatedSpdxElement"] = target
        };

    private static string SanitizeId(string value) =>
        string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-'));

    private static async Task<string> ComputeSha1Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();
        return Convert.ToHexStringLower(
            await sha1.ComputeHashAsync(stream, cancellationToken));
    }
}
