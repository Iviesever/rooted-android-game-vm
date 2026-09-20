using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DebugOperationAdmissionTests
{
    [Fact]
    public async Task Status_polling_cannot_overtake_a_waiting_stop()
    {
        var gate = new DebugOperationAdmission();
        var currentStatus = await gate.EnterAsync(false, CancellationToken.None);
        var stop = gate.EnterAsync(true, CancellationToken.None).AsTask();
        Assert.True(gate.ExclusiveRequested);
        var polls = Enumerable.Range(0, 20).Select(_ => gate.EnterAsync(false, CancellationToken.None).AsTask()).ToArray();
        Assert.All(polls, poll => Assert.False(poll.IsCompleted));
        currentStatus.Dispose();
        using (await stop.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.All(polls, poll => Assert.False(poll.IsCompleted));
        foreach (var poll in polls) (await poll.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.False(gate.ExclusiveRequested);
    }

    [Fact]
    public async Task Cancelling_a_waiting_stop_reopens_reader_admission()
    {
        var gate = new DebugOperationAdmission();
        using var current = await gate.EnterAsync(false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var stop = gate.EnterAsync(true, cancellation.Token).AsTask();
        var nextStatus = gate.EnterAsync(false, CancellationToken.None).AsTask();
        Assert.False(nextStatus.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        using var next = await nextStatus.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.ExclusiveRequested);
    }

    [Fact]
    public async Task Cancelling_a_reader_does_not_release_the_running_exclusive_operation()
    {
        var gate = new DebugOperationAdmission();
        var stop = await gate.EnterAsync(true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var status = gate.EnterAsync(false, cancellation.Token).AsTask();
        var nextWriter = gate.EnterAsync(true, CancellationToken.None).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => status);
        Assert.False(nextWriter.IsCompleted);
        stop.Dispose();
        using var next = await nextWriter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disposing_one_reader_twice_does_not_end_another_reader()
    {
        var gate = new DebugOperationAdmission();
        var first = await gate.EnterAsync(false, CancellationToken.None);
        var second = await gate.EnterAsync(false, CancellationToken.None);
        var stop = gate.EnterAsync(true, CancellationToken.None).AsTask();
        first.Dispose(); first.Dispose();
        Assert.False(stop.IsCompleted);
        second.Dispose();
        using var entered = await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
