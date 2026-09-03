using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public static class AndroidBootReadiness
{
    public static bool IsPackageServiceReady(ProcessResult result) =>
        result.ExitCode == 0 &&
        result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(
                line.Trim(),
                "Service package: found",
                StringComparison.Ordinal));
}
