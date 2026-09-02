using System.Diagnostics;

namespace RootedAndroidGameVM.Core.Processes;

public static class ProcessStartInfoFactory
{
    public static ProcessStartInfo Create(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static ProcessStartInfo CreateRequest(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = Create(request.Spec);
        startInfo.RedirectStandardInput = request.StandardInput is not null;
        if (request.EnvironmentVariables is not null)
        {
            foreach (var (name, value) in request.EnvironmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }
}
