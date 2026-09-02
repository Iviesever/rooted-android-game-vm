namespace RootedAndroidGameVM.Core.Processes;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);
