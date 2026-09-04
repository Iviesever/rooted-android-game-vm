using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record SetupGuiTestOptions(string ControlRoot, int Port)
{
    public static SetupGuiTestOptions? TryParse(string[] args, Func<string, string?> readEnvironment)
    {
        if (!args.Contains("--e2e-gui", StringComparer.Ordinal)) return null;
        if (readEnvironment("RGVM_E2E_ACCEPT_SDK_LICENSE") != "1")
            throw new ArgumentException("GUI E2E mode requires the explicit SDK license test gate.");
        if (args.Length != 5 || args[0] != "--e2e-gui" || args[1] != "--control-root" || args[3] != "--port" ||
            !int.TryParse(args[4], out var port) || port < 5554 || port > 5682 || port % 2 != 0)
            throw new ArgumentException("Invalid GUI E2E arguments.");
        return new(StoragePathPolicy.NormalizeRoot(args[2]), port);
    }
}
