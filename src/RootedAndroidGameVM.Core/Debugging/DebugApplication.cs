using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    public static string RequirePackage(DebugRequest request)
    {
        var package = request.Text("package");
        if (string.IsNullOrWhiteSpace(package))
            throw new DebugException("app_required", "请选择应用，或在请求中明确提供 package。", "resolving_application");
        AndroidPackageName.Parse(package);
        return package;
    }

    private async Task<(string Pid, DebugError? LaunchDiagnostic)> LaunchProcessAsync(string package, string directory, CancellationToken ct)
    {
        AndroidPackageName.Parse(package);
        Progress.Value?.Invoke(new { stage = "launch_requested", package, directory });
        DebugError? diagnostic = null;
        try { await AdbAsync(["shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"], ct); }
        catch (DebugException error) when (error.Code == "tool_failed")
        {
            diagnostic = DebugReply.Failure(error).Error;
            await File.WriteAllTextAsync(Path.Combine(directory, "launch-tool-error.json"), DebugJson.Write(diagnostic!), ct);
        }
        Progress.Value?.Invoke(new { stage = "waiting_for_process", package, directory, launchDiagnostic = diagnostic });
        var pid = await ApplicationProcessReadiness.WaitAsync(
            token => ShellAsync("pidof " + Q(package) + " || true", false, token), TimeSpan.FromSeconds(30), ct);
        Progress.Value?.Invoke(new { stage = "process_observed", package, directory, pid });
        return (pid, diagnostic);
    }

    public async Task<object> LaunchAsync(DebugRequest request, CancellationToken ct)
    {
        var package = RequirePackage(request);
        AndroidPackageName.Parse(package); Instance.Require();
        var directory = NewRecord("launch");
        var launch = await LaunchProcessAsync(package, directory, ct);
        ApplicationReadiness? observation = null;
        if (request.Flag("waitForActivity"))
        {
            Progress.Value?.Invoke(new { stage = "waiting_for_activity", package, directory, pid = launch.Pid });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                do
                {
                    observation = await ObserveApplicationAsync(package, directory, deadline.Token);
                    if (observation.ActivityReady) break;
                    await Task.Delay(500, deadline.Token);
                } while (true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new DebugException("app_not_ready", "已观察到应用进程，但30秒内没有确认前台Activity就绪。", "waiting_for_activity", directory); }
        }
        var stage = observation?.ActivityReady == true ? "activity_ready" : "process_observed";
        Progress.Value?.Invoke(new { stage, directory, package, pid = launch.Pid });
        var result = new { package, pid = launch.Pid, stage, interactiveReady = false, directory,
            activity = observation, launchDiagnostic = launch.LaunchDiagnostic };
        await File.WriteAllTextAsync(Path.Combine(directory, "launch.json"), DebugJson.Write(result), ct);
        return result;
    }
}
