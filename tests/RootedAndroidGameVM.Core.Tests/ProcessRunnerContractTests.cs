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
}
