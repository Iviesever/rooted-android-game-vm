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
    public async Task Github_release_requires_a_signed_clean_hosted_gate()
    {
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, ".github", "workflows", "signed-release.yml"));

        Assert.Contains("runs-on: windows-2025", workflow, StringComparison.Ordinal);
        Assert.Contains("RGVM_CODESIGN_PFX_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("-SigningCertificateThumbprint", workflow, StringComparison.Ordinal);
        Assert.Contains("-HeadlessE2E", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-ReuseE2EState", workflow, StringComparison.Ordinal);
        Assert.Contains("$signature.Status -ne 'Valid'", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
        Assert.Contains("--draft", workflow, StringComparison.Ordinal);
        Assert.Contains("-DeleteKey", workflow, StringComparison.Ordinal);
        Assert.Contains("attest-build-provenance", workflow, StringComparison.Ordinal);
        Assert.True(
            workflow.IndexOf("Prepare-InnoSetup.ps1", StringComparison.Ordinal) <
            workflow.IndexOf("Import trusted Authenticode certificate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hosted_unsigned_workflow_runs_the_full_clean_release_gate_without_publishing()
    {
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, ".github", "workflows", "unsigned-release-e2e.yml"));

        Assert.Contains("runs-on: windows-2025", workflow, StringComparison.Ordinal);
        Assert.Contains("Prepare-InnoSetup.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Build-Release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-AllowUnsignedLocalCandidate", workflow, StringComparison.Ordinal);
        Assert.Contains("-HeadlessE2E", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release create", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("upload-artifact", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inno_preparation_verifies_the_exact_manifest_release_tag()
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Prepare-InnoSetup.ps1"));

        Assert.Contains("$releaseTag", script, StringComparison.Ordinal);
        Assert.Contains("release verify-asset $releaseTag $installer", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_version_parser_accepts_windows_crlf_checkouts()
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));

        Assert.Contains(
            "'(?m)^#define AppVersion \"([^\"]+)\"\\r?$'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_e2e_test_propagates_the_explicit_headless_environment_gate()
    {
        var integrationTest = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "tests", "RootedAndroidGameVM.Core.Tests", "CleanInstallIntegrationTests.cs"));
        var cleanE2eSection = integrationTest[
            integrationTest.IndexOf("[Trait(\"Category\", \"CleanE2E\")]", StringComparison.Ordinal)..];

        Assert.Contains("RGVM_E2E_HEADLESS", cleanE2eSection, StringComparison.Ordinal);
        Assert.Contains("Headless:", cleanE2eSection, StringComparison.Ordinal);
        Assert.Contains("Verbose:", cleanE2eSection, StringComparison.Ordinal);
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
    public async Task Release_signing_discovers_the_x64_windows_sdk_signtool()
    {
        var buildScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, ".github", "workflows", "signed-release.yml"));

        Assert.Contains("Windows Kits\\10\\bin", buildScript, StringComparison.Ordinal);
        Assert.Contains("Windows Kits\\10\\bin", workflow, StringComparison.Ordinal);
        Assert.Contains("x64\\signtool.exe", buildScript, StringComparison.Ordinal);
        Assert.Contains("x64\\signtool.exe", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_release_uses_inno_to_sign_the_installer_and_embedded_uninstaller()
    {
        var innoScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "installer", "RootedAndroidGameVM.iss"));
        var buildScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));
        var signingPolicy = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "CODE_SIGNING_POLICY.md"));

        Assert.Contains("#ifdef RgvmSignedBuild", innoScript, StringComparison.Ordinal);
        Assert.Contains("SignTool=rgvm", innoScript, StringComparison.Ordinal);
        Assert.Contains("SignedUninstaller=yes", innoScript, StringComparison.Ordinal);
        Assert.Contains("SignToolRetryCount=3", innoScript, StringComparison.Ordinal);
        Assert.Contains("SignedUninstaller=no", innoScript, StringComparison.Ordinal);

        Assert.Contains("/DRgvmSignedBuild=1", buildScript, StringComparison.Ordinal);
        Assert.Contains("/Srgvm=$innoSignCommand", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--signtool", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-signing", buildScript, StringComparison.Ordinal);
        Assert.Contains("'$q' + $signToolPath + '$q sign", buildScript, StringComparison.Ordinal);
        Assert.Contains(" /td SHA256 $f'", buildScript, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-AuthenticodeValid $installer.FullName $SigningCertificateThumbprint",
            buildScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-AuthenticodeSign $installer.FullName", buildScript, StringComparison.Ordinal);
        Assert.Contains("embedded uninstaller", signingPolicy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signed_post_package_e2e_verifies_every_installed_executable_signer()
    {
        var buildScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Build-Release.ps1"));
        var e2eScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Invoke-PostPackageE2E.ps1"));

        Assert.Contains("-ExpectedSignerThumbprint $SigningCertificateThumbprint", buildScript, StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedSignerThumbprint", e2eScript, StringComparison.Ordinal);
        Assert.Contains("function Normalize-CertificateThumbprint", e2eScript, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $Path", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$resolvedInstaller", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$installedLauncher", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$installedSetup", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$uninstaller", e2eScript, StringComparison.Ordinal);
        Assert.Contains("SignerCertificate.Thumbprint", e2eScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_package_gui_smoke_waits_for_the_real_window_condition()
    {
        var e2eScript = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Invoke-PostPackageE2E.ps1"));

        Assert.DoesNotContain("Start-Sleep -Seconds 3", e2eScript, StringComparison.Ordinal);
        Assert.Contains("function Wait-ForGuiWindow", e2eScript, StringComparison.Ordinal);
        Assert.Contains("AddSeconds(30)", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$process.Refresh()", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$process.MainWindowHandle -ne 0", e2eScript, StringComparison.Ordinal);
        Assert.Contains("$process.MainWindowTitle -eq $ExpectedTitle", e2eScript, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 250", e2eScript, StringComparison.Ordinal);
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
            string.Concat("arc", "moe"),
            string.Concat("low", "iro"),
            string.Concat("moe", ".low", ".arc"),
            string.Concat(".", "aff"),
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

    [Theory]
    [InlineData("artifacts")]
    [InlineData("downloads")]
    [InlineData("exports")]
    [InlineData("cache")]
    [InlineData("tasks")]
    public async Task Root_generated_data_ignores_are_anchored_and_cannot_hide_source_folders(
        string directory)
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(ProjectRoot, ".gitignore"));

        Assert.Contains($"/{directory}/", lines);
        Assert.DoesNotContain($"{directory}/", lines);
    }

    [Fact]
    public async Task Hosted_workflows_use_the_bounded_isolated_product_cleanup()
    {
        var cleanup = await File.ReadAllTextAsync(
            Path.Combine(ProjectRoot, "build", "Remove-IsolatedProductRoot.ps1"));
        Assert.Contains("StartsWith($resolvedParent", cleanup, StringComparison.Ordinal);
        Assert.Contains("StartsWith($RequiredLeafPrefix", cleanup, StringComparison.Ordinal);
        Assert.Contains("Stop-Process", cleanup, StringComparison.Ordinal);

        foreach (var workflowName in new[] { "signed-release.yml", "unsigned-release-e2e.yml" })
        {
            var workflow = await File.ReadAllTextAsync(
                Path.Combine(ProjectRoot, ".github", "workflows", workflowName));
            Assert.Contains("Remove-IsolatedProductRoot.ps1", workflow, StringComparison.Ordinal);
        }
    }
}
