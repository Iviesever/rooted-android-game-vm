using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ImportContentVerifierTests
{
    [Fact]
    public void Changing_directory_enumeration_deduplicates_identical_rows_but_exposes_conflicting_hashes()
    {
        var a = new string('a', 64); var b = new string('b', 64);
        var snapshot = ImportContentVerifier.ParseHashes($"RGVM_DIRECTORY_PRESENT\n{a}  /target/script.lua\n{a}  /target/script.lua\n{a}  /target/info.json\n{b}  /target/info.json\n", "/target");
        Assert.True(snapshot.TargetExists); Assert.Equal(2, snapshot.Files.Count); Assert.Equal(2, snapshot.DuplicateRows);
        Assert.Equal(["info.json"], snapshot.ConflictingPaths);
        Assert.Throws<InvalidDataException>(() => ImportContentVerifier.ParseHashes($"{a}  /elsewhere/script.lua\n", "/target"));
    }
    [Fact]
    public void No_extraction_is_incomplete_and_never_activation_pending()
    {
        var result = ImportContentVerifier.Compare("target", new Dictionary<string, string> { ["script.lua"] = "one" }, new Dictionary<string, string>());
        Assert.False(result.Verified); Assert.Equal("unpacking_incomplete", result.Reason); Assert.Equal(["script.lua"], result.MissingFiles);
    }

    [Theory]
    [InlineData("script.lua")] [InlineData("resource.png")] [InlineData("signature.bin")] [InlineData("info.asm")]
    public void Resource_and_signature_changes_fail_even_with_a_known_metadata_rewrite(string name)
    {
        var expected = new Dictionary<string, string> { ["info.json"] = "source", [name] = "good" };
        var actual = new Dictionary<string, string> { ["info.json"] = "regenerated", [name] = "bad" };
        var result = ImportContentVerifier.Compare("target", expected, actual, true);
        Assert.False(result.Verified); Assert.Equal([name], result.DifferentFiles); Assert.Equal(["info.json"], result.MetadataRewrites);
    }

    [Theory]
    [InlineData("{\"creator\":\"author\",\"title\":\"fixture\"}", true)]
    [InlineData("{\"title\":\"fixture\",\"creator\":\"author\",\"mode\":0,\"key\":6,\"special\":null}", true)]
    [InlineData("{\"title\":\"changed\",\"creator\":\"author\"}", false)]
    [InlineData("{\"title\":\"fixture\",\"creator\":\"author\",\"signature\":\"modified\"}", false)]
    [InlineData("{\"title\":\"fixture\",\"creator\":\"author\",\"version\":\"script\"}", false)]
    [InlineData("{\"title\":\"fixture\",\"title\":\"fixture\",\"creator\":\"author\"}", false)]
    [InlineData("{\"title\":\"fixture\"}", false)]
    [InlineData("not-json", false)]
    public void Metadata_requires_unchanged_source_fields_and_only_known_typed_additions(string actual, bool allowed) =>
        Assert.Equal(allowed, ImportContentVerifier.IsKnownMetadataRewrite("{\"title\":\"fixture\",\"creator\":\"author\"}", actual));

    [Fact]
    public void Metadata_exception_never_hides_missing_files_or_unexpected_scripts()
    {
        var expected = new Dictionary<string, string> { ["info.json"] = "one", ["script.lua"] = "two" };
        var actual = new Dictionary<string, string> { ["info.json"] = "different", ["injected.lua"] = "three" };
        var result = ImportContentVerifier.Compare("target", expected, actual, true);
        Assert.False(result.Verified); Assert.Equal(["script.lua"], result.MissingFiles); Assert.Equal(["injected.lua"], result.ExtraFiles);
    }

    [Fact]
    public void Known_metadata_is_reported_separately_from_exact_hash_matches()
    {
        var expected = new Dictionary<string, string> { ["info.json"] = "one", ["script.lua"] = "two" };
        var actual = new Dictionary<string, string> { ["info.json"] = "different", ["script.lua"] = "two" };
        var result = ImportContentVerifier.Compare("target", expected, actual, true);
        Assert.True(result.Verified); Assert.Equal(1, result.VerifiedFiles); Assert.Equal(2, result.ExpectedFiles);
        Assert.Equal("known_metadata_rewrite", result.Reason);
    }

    [Fact]
    public void Observed_client_version_rewrite_is_narrow_and_preserves_signatures()
    {
        const string source = "{\"title\":\"skin\",\"version\":393216,\"signature\":\"valid\"}";
        Assert.True(ImportContentVerifier.IsKnownMetadataRewrite(source, source.Replace("393216", "394764")));
        Assert.False(ImportContentVerifier.IsKnownMetadataRewrite(source, source.Replace("393216", "394765")));
        Assert.False(ImportContentVerifier.IsKnownMetadataRewrite(source, source.Replace("393216", "394764").Replace("valid", "broken")));
    }

    [Theory]
    [InlineData("private", null, "600")] [InlineData("private", "640", "640")]
    [InlineData("private", "777", "660")]
    [InlineData("external", null, "660")] [InlineData("external", "600", "660")]
    [InlineData("external", "777", "660")]
    [InlineData("shared", null, "644")] [InlineData("shared", "664", "664")]
    public void New_and_replaced_files_have_scope_access_without_execute_or_world_write(string scope, string? previous, string expected)
        => Assert.Equal(expected, FileAccessPolicy.FileMode(scope, previous));
}
