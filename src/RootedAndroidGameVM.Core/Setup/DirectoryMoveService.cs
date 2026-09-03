namespace RootedAndroidGameVM.Core.Setup;

public sealed class DirectoryMoveService
{
    private readonly Action<string, string> _move;
    private readonly Func<int, TimeSpan> _retryDelay;

    public DirectoryMoveService(
        Action<string, string>? move = null,
        Func<int, TimeSpan>? retryDelay = null)
    {
        _move = move ?? Directory.Move;
        _retryDelay = retryDelay ?? (attempt =>
            TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)));
    }

    public async Task MoveAsync(
        string source,
        string target,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _move(source, target);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt < 7)
            {
                if (!Directory.Exists(source) && Directory.Exists(target)) return;
                if (!Directory.Exists(source) ||
                    Directory.Exists(target) ||
                    File.Exists(target))
                {
                    throw;
                }
                await Task.Delay(_retryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
