using System.Diagnostics;
using System.Reflection;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class FoundationContractTests
{
    private static readonly Assembly CoreAssembly = typeof(RootedAndroidGameVM.Core.Class1).Assembly;

    [Fact]
    public void Process_factory_creates_a_hidden_redirected_process_with_exact_arguments()
    {
        var specType = CoreAssembly.GetType("RootedAndroidGameVM.Core.Processes.ProcessSpec");
        var factoryType = CoreAssembly.GetType("RootedAndroidGameVM.Core.Processes.ProcessStartInfoFactory");

        Assert.NotNull(specType);
        Assert.NotNull(factoryType);

        var arguments = new[] { "--name", "value with spaces", "&literal" };
        var spec = Activator.CreateInstance(specType, "tool.exe", arguments, @"C:\safe work");
        Assert.NotNull(spec);

        var createMethod = factoryType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(createMethod);

        var startInfo = Assert.IsType<ProcessStartInfo>(createMethod.Invoke(null, new[] { spec }));
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(@"C:\safe work", startInfo.WorkingDirectory);
        Assert.Equal(arguments, startInfo.ArgumentList);
    }

    [Fact]
    public void Path_boundary_accepts_descendants_and_rejects_sibling_prefixes()
    {
        var boundaryType = CoreAssembly.GetType("RootedAndroidGameVM.Core.IO.PathBoundary");
        Assert.NotNull(boundaryType);

        var method = boundaryType.GetMethod("EnsureWithinRoot", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var root = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM", "safe");
        var descendant = Path.Combine(root, "exports", "app");
        var accepted = Assert.IsType<string>(method.Invoke(null, new object[] { root, descendant }));
        Assert.Equal(Path.GetFullPath(descendant), accepted);

        var siblingWithSamePrefix = root + "-outside";
        var exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, new object[] { root, siblingWithSamePrefix }));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Process_request_can_add_hidden_standard_input_and_scoped_environment()
    {
        var spec = new RootedAndroidGameVM.Core.Processes.ProcessSpec(
            "tool.exe", ["--accept"], @"C:\safe");
        var request = new RootedAndroidGameVM.Core.Processes.ProcessRequest(
            spec,
            "yes\n",
            new Dictionary<string, string> { ["ANDROID_HOME"] = @"C:\Android\Sdk" });

        var startInfo = RootedAndroidGameVM.Core.Processes.ProcessStartInfoFactory.CreateRequest(request);

        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(@"C:\Android\Sdk", startInfo.Environment["ANDROID_HOME"]);
    }
}
