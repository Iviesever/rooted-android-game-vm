using System.Diagnostics;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ProcessInventoryTests
{
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
