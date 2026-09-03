using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SdkArchiveInstallerContractTests
{
    [Fact]
    public async Task Verified_sdk_archive_is_extracted_into_the_pinned_target_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-archive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceZip = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("platform-tools/source.properties");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("Pkg.Revision=37.0.1");
            }
            var bytes = await File.ReadAllBytesAsync(sourceZip);
            var component = new DependencyComponent(
                "android-platform-tools",
                "Android SDK Platform Tools",
                "37.0.1",
                "https://example.invalid/platform-tools.zip",
                "platform-tools.zip",
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                string.Empty,
                bytes.Length,
                true,
                "LicenseRef-Android-SDK",
                "direct");
            using var client = new HttpClient(new StaticHandler(bytes));
            var installer = new SdkArchiveInstaller(new VerifiedDownloader(client));

            await installer.InstallAsync(
                component,
                Path.Combine(root, "downloads"),
                Path.Combine(root, "sdk"),
                "platform-tools",
                "platform-tools");

            Assert.Equal(
                "Pkg.Revision=37.0.1",
                await File.ReadAllTextAsync(Path.Combine(root, "sdk", "platform-tools", "source.properties")));
            var packageXml = await File.ReadAllTextAsync(
                Path.Combine(root, "sdk", "platform-tools", "package.xml"));
            Assert.Contains("path=\"platform-tools\"", packageXml, StringComparison.Ordinal);
            Assert.Contains("<major>37</major>", packageXml, StringComparison.Ordinal);
            var localPackage = XDocument.Parse(packageXml)
                .Descendants()
                .Single(element => element.Name.LocalName == "localPackage");
            Assert.Equal(XNamespace.None, localPackage.Name.Namespace);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Valid_cached_archive_is_used_without_a_network_request()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchiveBytes("emulator/source.properties", "Pkg.Revision=37.1.11");
            var cache = Path.Combine(root, "downloads");
            Directory.CreateDirectory(cache);
            var cachedArchive = Path.Combine(cache, "emulator.zip");
            await File.WriteAllBytesAsync(cachedArchive, bytes);
            var component = new DependencyComponent(
                "android-emulator",
                "Android Emulator",
                "37.1.11",
                "https://example.invalid/emulator.zip",
                "emulator.zip",
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                string.Empty,
                bytes.Length,
                true,
                "LicenseRef-Android-SDK",
                "direct");
            using var client = new HttpClient(new ThrowingHandler());

            await new SdkArchiveInstaller(new VerifiedDownloader(client)).InstallAsync(
                component,
                cache,
                Path.Combine(root, "sdk"),
                "emulator",
                "emulator");

            Assert.True(File.Exists(Path.Combine(root, "sdk", "emulator", "source.properties")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_component_without_package_xml_can_be_registered_in_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-register", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "emulator");
        Directory.CreateDirectory(target);
        try
        {
            var component = new DependencyComponent(
                "android-emulator",
                "Android Emulator",
                "37.1.11",
                "https://example.invalid/emulator.zip",
                "emulator.zip",
                new string('a', 64),
                string.Empty,
                1,
                true,
                "LicenseRef-Android-SDK",
                "direct");
            using var client = new HttpClient(new ThrowingHandler());
            var installer = new SdkArchiveInstaller(new VerifiedDownloader(client));

            await installer.EnsureGenericRegistrationAsync(component, root, "emulator");

            Assert.True(File.Exists(Path.Combine(target, "package.xml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task System_image_registration_uses_sysimg_type_details_from_source_properties()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-system-image-register", Guid.NewGuid().ToString("N"));
        var relative = Path.Combine("system-images", "android-35", "google_apis_playstore", "x86_64");
        var target = Path.Combine(root, relative);
        Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(target, "source.properties"),
                """
                Pkg.Revision=9
                AndroidVersion.ApiLevel=35
                AndroidVersion.ExtensionLevel=13
                AndroidVersion.IsBaseSdk=true
                SystemImage.Abi=x86_64
                SystemImage.TagId=google_apis_playstore
                SystemImage.TagDisplay=Google Play
                Addon.VendorId=google
                Addon.VendorDisplay=Google Inc.
                """);
            var component = new DependencyComponent(
                "android-system-image-api35-playstore-x86_64",
                "Android System Image",
                "9",
                "https://example.invalid/image.zip",
                "image.zip",
                new string('a', 64),
                string.Empty,
                1,
                true,
                "NOASSERTION",
                "direct");
            var installer = new SdkArchiveInstaller(
                new VerifiedDownloader(new HttpClient(new ThrowingHandler())));

            await installer.EnsureGenericRegistrationAsync(component, root, relative);

            var document = XDocument.Load(Path.Combine(target, "package.xml"));
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            var typeDetails = document.Descendants().Single(element => element.Name.LocalName == "type-details");
            Assert.EndsWith(":sysImgDetailsType", typeDetails.Attribute(xsi + "type")?.Value);
            Assert.Equal("35", typeDetails.Elements().Single(element => element.Name.LocalName == "api-level").Value);
            Assert.Equal("google_apis_playstore", typeDetails.Descendants().Single(element =>
                element.Name.LocalName == "tag").Elements().Single(element => element.Name.LocalName == "id").Value);
            Assert.Equal("x86_64", typeDetails.Elements().Single(element => element.Name.LocalName == "abi").Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Broken_existing_component_is_replaced_from_the_verified_archive()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-repair", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchiveBytes("emulator/source.properties", "Pkg.Revision=37.1.11");
            var cache = Path.Combine(root, "downloads");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, "emulator.zip"), bytes);
            var target = Path.Combine(root, "sdk", "emulator");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "source.properties"), "Pkg.Revision=1.0.0");
            await File.WriteAllTextAsync(Path.Combine(target, "keep-proof.txt"), "broken old component");
            var component = new DependencyComponent(
                "android-emulator", "Android Emulator", "37.1.11",
                "https://example.invalid/emulator.zip", "emulator.zip",
                Convert.ToHexStringLower(SHA256.HashData(bytes)), string.Empty, bytes.Length,
                true, "LicenseRef-Android-SDK", "direct");
            using var client = new HttpClient(new ThrowingHandler());

            await new SdkArchiveInstaller(new VerifiedDownloader(client)).InstallAsync(
                component, cache, Path.Combine(root, "sdk"), "emulator", "emulator");

            Assert.Equal(
                "Pkg.Revision=37.1.11",
                await File.ReadAllTextAsync(Path.Combine(target, "source.properties")));
            Assert.False(File.Exists(Path.Combine(target, "keep-proof.txt")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "sdk", ".rgvm-backup")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_replacement_restores_the_previous_component()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-sdk-rollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchiveBytes("emulator/source.properties", "Pkg.Revision=invalid.version.value.more");
            var cache = Path.Combine(root, "downloads");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, "emulator.zip"), bytes);
            var target = Path.Combine(root, "sdk", "emulator");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "old-proof.txt"), "restore me");
            var component = new DependencyComponent(
                "android-emulator", "Android Emulator", "invalid.version.value.more",
                "https://example.invalid/emulator.zip", "emulator.zip",
                Convert.ToHexStringLower(SHA256.HashData(bytes)), string.Empty, bytes.Length,
                true, "LicenseRef-Android-SDK", "direct");
            using var client = new HttpClient(new ThrowingHandler());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new SdkArchiveInstaller(new VerifiedDownloader(client)).InstallAsync(
                    component, cache, Path.Combine(root, "sdk"), "emulator", "emulator"));

            Assert.Equal("restore me", await File.ReadAllTextAsync(Path.Combine(target, "old-proof.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateArchiveBytes(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write(content);
        }
        return stream.ToArray();
    }

    private sealed class StaticHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network should not be used for a valid cached archive.");
    }
}
