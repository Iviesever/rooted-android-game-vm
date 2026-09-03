namespace RootedAndroidGameVM.Core.Tests;

public sealed class ReleaseScriptContractTests
{
    private static string ProjectRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task Uninstaller_never_deletes_global_android_studio_avds()
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "installer", "RootedAndroidGameVM.iss"));

        Assert.DoesNotContain("{userprofile}\\.android\\avd", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signed_release_rejects_reused_e2e_state()
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));

        Assert.Contains("$SigningCertificateThumbprint -and $ReuseE2EState", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_gate_invokes_the_pinned_official_spdx_validator()
    {
        var buildScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));
        var validatorScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Validate-Spdx.ps1"));
        var prepareScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Prepare-SpdxValidator.ps1"));

        Assert.Contains("Validate-Spdx.ps1", buildScript, StringComparison.Ordinal);
        Assert.Contains("repository hash lock", validatorScript, StringComparison.Ordinal);
        Assert.Contains("validate_full_spdx_document", validatorScript, StringComparison.Ordinal);
        Assert.Contains("--require-hashes", prepareScript, StringComparison.Ordinal);
        Assert.Contains("--no-index", prepareScript, StringComparison.Ordinal);
        Assert.Contains("-Offline", buildScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Github_release_requires_a_signed_clean_self_hosted_gate()
    {
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, ".github", "workflows", "signed-release.yml"));

        Assert.Contains("runs-on: [self-hosted, Windows, X64, rgvm-hyperv]", workflow, StringComparison.Ordinal);
        Assert.Contains("RGVM_CODESIGN_PFX_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("-SigningCertificateThumbprint", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-ReuseE2EState", workflow, StringComparison.Ordinal);
        Assert.Contains("$signature.Status -ne 'Valid'", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("--draft", workflow, StringComparison.Ordinal);
        Assert.Contains("-DeleteKey", workflow, StringComparison.Ordinal);
        Assert.Contains("attest-build-provenance", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_publication_requires_a_separate_protected_manual_workflow()
    {
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, ".github", "workflows", "publish-reviewed-draft.yml"));

        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: release-publish", workflow, StringComparison.Ordinal);
        Assert.Contains("$signature.Status -ne 'Valid'", workflow, StringComparison.Ordinal);
        Assert.Contains("RGVM_RELEASE_CERT_THUMBPRINT", workflow, StringComparison.Ordinal);
        Assert.Contains("attestations: read", workflow, StringComparison.Ordinal);
        Assert.Contains("gh attestation verify", workflow, StringComparison.Ordinal);
        Assert.Contains("FileVersionRaw", workflow, StringComparison.Ordinal);
        Assert.Contains("--draft=false", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_owns_only_the_post_success_start_menu_shortcut()
    {
        var service = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "src", "RootedAndroidGameVM.Setup", "ShortcutService.cs"));
        var e2e = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "src", "RootedAndroidGameVM.Setup", "App.xaml.cs"));

        Assert.Contains("CreateLauncherStartMenuShortcut", e2e, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopDirectory", service, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_repository_contains_no_game_specific_identifiers()
    {
        var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "artifacts", "bin", "obj", "tasks", "TestResults"
        };
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty, ".cs", ".csproj", ".gitignore", ".iss", ".json", ".md",
            ".props", ".ps1", ".sln", ".txt", ".xaml", ".yaml", ".yml"
        };
        var files = Directory.EnumerateFiles(ProjectRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(ProjectRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(excludedDirectories.Contains))
            .Where(path => textExtensions.Contains(Path.GetExtension(path)));
        var gameSpecificIdentifiers = new[]
        {
            string.Concat("Arc", "aea"),
            string.Concat("moe", ".low", ".arc"),
            string.Concat("质", "感"),
            string.Concat("谱", "面")
        };

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            foreach (var identifier in gameSpecificIdentifiers)
            {
                Assert.False(
                    content.Contains(identifier, StringComparison.OrdinalIgnoreCase),
                    $"Game-specific identifier found in {Path.GetRelativePath(ProjectRoot, file)}.");
            }
        }
    }
}
