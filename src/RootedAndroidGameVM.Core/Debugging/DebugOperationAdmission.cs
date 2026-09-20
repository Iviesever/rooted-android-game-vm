namespace RootedAndroidGameVM.Core.Debugging;

/// <summary>Drains existing readers before an exclusive operation, without admitting new readers ahead of it.</summary>
public sealed class DebugOperationAdmission
{
    private readonly object _gate = new();
    private int _readers;
    private int _waitingExclusive;
    private bool _exclusive;
    private TaskCompletionSource _changed = NewSignal();

    public bool ExclusiveRequested { get { lock (_gate) return _exclusive || _waitingExclusive > 0; } }

    public async ValueTask<IDisposable> EnterAsync(bool exclusive, CancellationToken ct)
    {
        var waiting = false;
        try
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (exclusive) { _waitingExclusive++; waiting = true; }
            }
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_exclusive && (exclusive ? _readers == 0 : _waitingExclusive == 0))
                    {
                        if (exclusive) { _waitingExclusive--; waiting = false; _exclusive = true; }
                        else _readers++;
                        return new Lease(this, exclusive);
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(ct);
            }
        }
        finally
        {
            if (waiting) lock (_gate) { _waitingExclusive--; Signal(); }
        }
    }

    private void Exit(bool exclusive)
    {
        lock (_gate)
        {
            if (exclusive) _exclusive = false;
            else _readers--;
            Signal();
        }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }
    private sealed class Lease(DebugOperationAdmission owner, bool exclusive) : IDisposable
    {
        private DebugOperationAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(exclusive);
    }
}
