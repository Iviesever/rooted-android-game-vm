namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidVmStartupPolicy(
    TimeSpan Timeout,
    TimeSpan PollInterval)
{
    public static AndroidVmStartupPolicy Default { get; } = new(
        TimeSpan.FromMinutes(8),
        TimeSpan.FromSeconds(2));
}
