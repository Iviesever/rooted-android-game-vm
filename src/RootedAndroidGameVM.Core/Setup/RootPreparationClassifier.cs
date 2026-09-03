using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Setup;

public enum RootPreparationState
{
    NeedsPatch,
    PolicyPending,
    Working
}

public static class RootPreparationClassifier
{
    public static RootPreparationState Classify(
        bool ramdiskMatchesStock,
        ProcessResult identity,
        ProcessResult whichSu)
    {
        if (ramdiskMatchesStock) return RootPreparationState.NeedsPatch;
        if (identity.ExitCode == 0 &&
            identity.StandardOutput.Contains("uid=0", StringComparison.Ordinal))
        {
            return RootPreparationState.Working;
        }

        var identityDetail = $"{identity.StandardOutput}\n{identity.StandardError}";
        var explicitPolicyDenial =
            identityDetail.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
        var suExists = whichSu.ExitCode == 0 && !string.IsNullOrWhiteSpace(whichSu.StandardOutput);
        return explicitPolicyDenial && suExists
            ? RootPreparationState.PolicyPending
            : RootPreparationState.NeedsPatch;
    }
}
