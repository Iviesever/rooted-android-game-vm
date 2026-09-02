namespace RootedAndroidGameVM.Core.Processes;

public sealed record ProcessRequest(
    ProcessSpec Spec,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
