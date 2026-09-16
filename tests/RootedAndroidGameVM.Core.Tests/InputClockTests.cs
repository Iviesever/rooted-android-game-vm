using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;
public sealed class InputClockTests
{
    [Fact]
    public void Legacy_sequences_start_now_and_absolute_schedules_do_not_replay_missed_actions()
    {
        const long frequency = 10_000_000, now = 100_000_000;
        Assert.Equal(now, InputClock.ResolveStart(null, now, frequency));
        Assert.Equal(now + frequency * 3, InputClock.ResolveStart(now + frequency * 3, now, frequency));
        Assert.Equal("input_schedule_missed", Assert.Throws<DebugException>(() => InputClock.ResolveStart(now - frequency, now, frequency)).Code);
        Assert.Throws<ArgumentException>(() => InputClock.ResolveStart(now + frequency * 121, now, frequency));
        Assert.Throws<ArgumentException>(() => InputClock.ResolveStart(-1, now, frequency));
    }
}
