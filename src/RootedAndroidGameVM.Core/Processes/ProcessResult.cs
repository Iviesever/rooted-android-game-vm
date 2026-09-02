namespace RootedAndroidGameVM.Core.Processes;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
