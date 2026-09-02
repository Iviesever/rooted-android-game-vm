namespace RootedAndroidGameVM.Core.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);

    Task<ProcessResult> RunRequestAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(request.Spec, cancellationToken);
}
