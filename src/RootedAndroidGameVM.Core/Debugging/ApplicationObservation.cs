using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record ApplicationReadiness(string Package, string? Pid, string? Foreground, bool ActivityReady,
    bool InteractiveReady, string Stage, DateTimeOffset ObservedAt, string EvidencePath);

public sealed partial class AndroidDebugService
{
    public async Task<ApplicationReadiness> ObserveApplicationAsync(string package, string directory, CancellationToken ct)
    {
        Android.AndroidPackageName.Parse(package);
        var pid = (await ShellAsync("pidof " + Q(package) + " || true", false, ct)).Trim();
        if (DebugOperation.Current.Value is { } operation) operation.Pid = pid.Length == 0 ? null : pid;
        var state = await StateAsync(ct, force: true);
        var activity = await ShellAsync("dumpsys activity activities", false, ct);
        var path = Path.Combine(directory, "activity-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        await File.WriteAllTextAsync(path, activity, ct);
        var resumed = Regex.IsMatch(activity, @"(?:mResumedActivity|topResumedActivity)[^\r\n]*\b" + Regex.Escape(package) + @"/");
        var ready = pid.Length > 0 && resumed && state.Foreground == package && state.Boot && state.Awake && !state.Locked;
        return new(package, pid.Length == 0 ? null : pid, state.Foreground, ready, false,
            ready ? "activity_ready" : pid.Length > 0 ? "process_observed" : "process_absent", DateTimeOffset.UtcNow, path);
    }

}
