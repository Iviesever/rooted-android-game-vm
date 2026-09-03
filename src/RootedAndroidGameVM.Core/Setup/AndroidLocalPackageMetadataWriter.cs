using System.Xml.Linq;
using RootedAndroidGameVM.Core.Dependencies;

namespace RootedAndroidGameVM.Core.Setup;

public static class AndroidLocalPackageMetadataWriter
{
    public static async Task WriteGenericAsync(
        string path,
        string packagePath,
        DependencyComponent component,
        CancellationToken cancellationToken = default)
    {
        var versionParts = component.Version.Split('.');
        if (versionParts.Length is < 1 or > 3 ||
            versionParts.Any(part => !int.TryParse(part, out _)))
        {
            throw new InvalidDataException(
                $"Android package '{component.Id}' has an invalid revision '{component.Version}'.");
        }

        XNamespace common = "http://schemas.android.com/repository/android/common/02";
        XNamespace generic = "http://schemas.android.com/repository/android/generic/02";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var revision = new XElement("revision",
            new XElement("major", versionParts[0]));
        if (versionParts.Length > 1)
        {
            revision.Add(new XElement("minor", versionParts[1]));
        }
        if (versionParts.Length > 2)
        {
            revision.Add(new XElement("micro", versionParts[2]));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                common + "repository",
                new XAttribute(XNamespace.Xmlns + "generic", generic),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement(
                    "license",
                    new XAttribute("id", "android-sdk-license"),
                    new XAttribute("type", "text")),
                new XElement(
                    "localPackage",
                    new XAttribute("path", packagePath),
                    new XAttribute("obsolete", "false"),
                    new XElement(
                        "type-details",
                        new XAttribute(xsi + "type", "generic:genericDetailsType")),
                    revision,
                    new XElement("display-name", component.Name),
                    new XElement(
                        "uses-license",
                        new XAttribute("ref", "android-sdk-license")))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            document.Declaration + Environment.NewLine +
            document.Root!.ToString(SaveOptions.DisableFormatting),
            cancellationToken);
    }

    public static async Task WriteSystemImageAsync(
        string path,
        string packagePath,
        string sourcePropertiesPath,
        DependencyComponent component,
        CancellationToken cancellationToken = default)
    {
        var properties = File.ReadLines(sourcePropertiesPath)
            .Where(line => line.Contains('=', StringComparison.Ordinal))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        string Required(string key) => properties.TryGetValue(key, out var value) &&
                                       !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidDataException($"System image metadata is missing '{key}'.");

        XNamespace common = "http://schemas.android.com/repository/android/common/02";
        XNamespace systemImage = "http://schemas.android.com/sdk/android/repo/sys-img2/04";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var typeDetails = new XElement(
            "type-details",
            new XAttribute(xsi + "type", "sys-img:sysImgDetailsType"),
            new XElement("api-level", Required("AndroidVersion.ApiLevel")));
        if (properties.TryGetValue("AndroidVersion.ExtensionLevel", out var extensionLevel))
        {
            typeDetails.Add(new XElement("extension-level", extensionLevel.Trim()));
        }
        if (properties.TryGetValue("AndroidVersion.IsBaseSdk", out var isBaseSdk))
        {
            typeDetails.Add(new XElement("base-extension", isBaseSdk.Trim().ToLowerInvariant()));
        }
        typeDetails.Add(
            new XElement("tag",
                new XElement("id", Required("SystemImage.TagId")),
                new XElement("display", Required("SystemImage.TagDisplay"))),
            new XElement("vendor",
                new XElement("id", Required("Addon.VendorId")),
                new XElement("display", Required("Addon.VendorDisplay"))),
            new XElement("abi", Required("SystemImage.Abi")),
            new XElement("abis", Required("SystemImage.Abi")));

        var revisionParts = component.Version.Split('.');
        var revision = new XElement("revision", new XElement("major", revisionParts[0]));
        if (revisionParts.Length > 1) revision.Add(new XElement("minor", revisionParts[1]));
        if (revisionParts.Length > 2) revision.Add(new XElement("micro", revisionParts[2]));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(
                common + "repository",
                new XAttribute(XNamespace.Xmlns + "sys-img", systemImage),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement("license",
                    new XAttribute("id", "android-sdk-license"),
                    new XAttribute("type", "text")),
                new XElement(
                    "localPackage",
                    new XAttribute("path", packagePath),
                    new XAttribute("obsolete", "false"),
                    typeDetails,
                    revision,
                    new XElement("display-name", component.Name),
                    new XElement("uses-license", new XAttribute("ref", "android-sdk-license")))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            document.Declaration + Environment.NewLine +
            document.Root!.ToString(SaveOptions.DisableFormatting),
            cancellationToken);
    }
}
