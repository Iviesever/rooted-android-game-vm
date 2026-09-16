using System.Diagnostics;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ProcessInventoryTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Inventory_preserves_quoted_unicode_arguments_parent_and_creation_time()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "rgvm-process-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "fixture.ps1");
        File.WriteAllText(script, "param([string]$avd,[string]$datadir,[int]$port)\nStart-Sleep -Seconds 30\n");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        var directory = Path.Combine(root, "guest space 中文.avd");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-avd", "test 中文 guest", "-datadir", directory, "-port", "5580" }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        try
        {
            var row = Assert.Single(new WindowsEmulatorProcessCatalog().FindByExecutable(executable), row => row.ProcessId == child.Id);
            Assert.Equal(Environment.ProcessId, row.ParentProcessId);
            Assert.Equal(child.StartTime.ToUniversalTime().Ticks / 10, row.StartedAtUtcTicks / 10);
            Assert.Equal("test 中文 guest", row.AvdName);
            Assert.Equal(directory, row.AvdDirectory);
            Assert.Equal(5580, row.ConsolePort);
        }
        finally
        {
            if (!child.HasExited) child.Kill(true);
            child.WaitForExit();
            Directory.Delete(root, true);
        }
    }
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Bulk_inventory_checks_exact_paths_and_does_not_duplicate_a_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = Process.GetCurrentProcess();
        var path = current.MainModule!.FileName;
        var unrelated = Path.Combine(Path.GetTempPath(), "not-the-test-host", Path.GetFileName(path));
        var catalog = new WindowsEmulatorProcessCatalog();
        Assert.Empty(catalog.FindByExecutables([]));
        Assert.Empty(catalog.FindByExecutable(unrelated));
        var rows = catalog.FindByExecutables([path, path.ToUpperInvariant(), unrelated]);
        var row = Assert.Single(rows, row => row.ProcessId == current.Id);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(row.ExecutablePath), ignoreCase: true);
        Assert.Equal(current.StartTime.ToUniversalTime().Ticks / 10, row.StartedAtUtcTicks / 10);
        Assert.Equal(rows.Count, rows.Select(row => row.ProcessId).Distinct().Count());
        Assert.Throws<ArgumentException>(() => catalog.FindByExecutables(["injected' OR Name='bad.exe"]));
    }
}
