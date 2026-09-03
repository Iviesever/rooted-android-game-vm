using System.Reflection;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ProcessRunnerContractTests
{
    [Fact]
    public async Task Process_runner_captures_output_and_exit_code()
    {
        var assembly = typeof(RootedAndroidGameVM.Core.Class1).Assembly;
        var runnerType = assembly.GetType("RootedAndroidGameVM.Core.Processes.ProcessRunner");
        var specType = assembly.GetType("RootedAndroidGameVM.Core.Processes.ProcessSpec");

        Assert.NotNull(runnerType);
        Assert.NotNull(specType);

        var runner = Activator.CreateInstance(runnerType);
        var spec = Activator.CreateInstance(specType, "dotnet", new[] { "--version" }, null);
        Assert.NotNull(runner);
        Assert.NotNull(spec);

        var runMethod = runnerType.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(runMethod);

        var invocation = runMethod.Invoke(runner, new[] { spec, CancellationToken.None });
        var task = Assert.IsAssignableFrom<Task>(invocation);
        await task;

        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);

        var exitCode = Assert.IsType<int>(result.GetType().GetProperty("ExitCode")?.GetValue(result));
        var standardOutput = Assert.IsType<string>(result.GetType().GetProperty("StandardOutput")?.GetValue(result));
        var standardError = Assert.IsType<string>(result.GetType().GetProperty("StandardError")?.GetValue(result));

        Assert.Equal(0, exitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", standardOutput.Trim());
        Assert.True(string.IsNullOrWhiteSpace(standardError));
    }

    [Fact]
    public async Task Detached_launcher_can_capture_diagnostics_without_a_console_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-detached-log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "process.log");
            var handle = new RootedAndroidGameVM.Core.Processes.DetachedProcessLauncher().Start(
                new RootedAndroidGameVM.Core.Processes.ProcessSpec(
                    "dotnet",
                    ["--version"],
                    root),
                diagnosticLogPath: log);

            await handle.Process.WaitForExitAsync();
            await handle.CaptureCompletion;

            Assert.Equal(0, handle.Process.ExitCode);
            Assert.Matches(@"\d+\.\d+\.\d+", await File.ReadAllTextAsync(log));
            await handle.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
