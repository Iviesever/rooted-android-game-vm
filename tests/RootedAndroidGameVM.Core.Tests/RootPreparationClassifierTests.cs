using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class RootPreparationClassifierTests
{
    [Fact]
    public void Explicit_policy_denial_with_su_binary_is_policy_pending()
    {
        var state = RootPreparationClassifier.Classify(
            ramdiskMatchesStock: false,
            new ProcessResult(13, string.Empty, "Permission denied"),
            new ProcessResult(0, "/debug_ramdisk/su\n", string.Empty));

        Assert.Equal(RootPreparationState.PolicyPending, state);
    }

    [Fact]
    public void Unusable_su_without_explicit_policy_denial_requires_repatch()
    {
        var state = RootPreparationClassifier.Classify(
            ramdiskMatchesStock: false,
            new ProcessResult(1, string.Empty, "daemon unavailable"),
            new ProcessResult(0, "/debug_ramdisk/su\n", string.Empty));

        Assert.Equal(RootPreparationState.NeedsPatch, state);
    }
}
