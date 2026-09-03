using System.Reflection;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SecurityAndReleaseContractTests
{
    private static readonly Assembly CoreAssembly = typeof(RootedAndroidGameVM.Core.Class1).Assembly;

    [Fact]
    public void Release_auditor_rejects_commercial_content_images_and_credentials()
    {
        var auditorType = CoreAssembly.GetType("RootedAndroidGameVM.Core.Release.ReleaseContentAuditor");
        Assert.NotNull(auditorType);

        var root = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "RootedAndroidGameVM.exe"), "allowed");
            File.WriteAllText(Path.Combine(root, "nested", "game.APK"), "forbidden");
            File.WriteAllText(Path.Combine(root, "system.img"), "forbidden");
            File.WriteAllText(Path.Combine(root, "userdata.qcow2"), "forbidden");
            File.WriteAllText(Path.Combine(root, "signing.pfx"), "forbidden");

            var method = auditorType.GetMethod("FindViolations", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            var violations = Assert.IsAssignableFrom<IReadOnlyList<string>>(
                method.Invoke(null, new object[] { root }));

            Assert.Equal(4, violations.Count);
            Assert.DoesNotContain(violations, item => item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(violations, item => item.EndsWith("game.APK", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Log_redactor_removes_paths_and_tokens()
    {
        var redactorType = CoreAssembly.GetType("RootedAndroidGameVM.Core.Security.LogRedactor");
        Assert.NotNull(redactorType);
        var method = redactorType.GetMethod("Redact", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var input = @"Failed in C:\Users\Alice\AppData with token abc123";
        var secrets = new[] { @"C:\Users\Alice", "abc123" };
        var output = Assert.IsType<string>(method.Invoke(null, new object[] { input, secrets }));

        Assert.DoesNotContain("Alice", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Sha256_verifier_computes_a_stable_lowercase_digest()
    {
        var verifierType = CoreAssembly.GetType("RootedAndroidGameVM.Core.Security.Sha256Verifier");
        Assert.NotNull(verifierType);
        var method = verifierType.GetMethod("ComputeAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "hello");
            var invocation = method.Invoke(null, new object[] { path, CancellationToken.None });
            var task = Assert.IsAssignableFrom<Task>(invocation);
            await task;
            var digest = Assert.IsType<string>(task.GetType().GetProperty("Result")?.GetValue(task));
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
