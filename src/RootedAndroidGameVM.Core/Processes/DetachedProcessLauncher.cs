using System.Diagnostics;

namespace RootedAndroidGameVM.Core.Processes;

public sealed class DetachedProcessLauncher
{
    public Process Start(
        ProcessSpec spec,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start '{spec.FileName}'.");
    }
}
