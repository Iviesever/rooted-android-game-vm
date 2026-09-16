using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class FrameTimingsTests
{
    [Fact]
    public void Nonoverlapping_history_is_not_silently_treated_as_continuous_coverage()
    {
        Assert.False(FrameTimingSummary.HistoryGap([1, 2, 3], [3, 4, 5]));
        Assert.False(FrameTimingSummary.HistoryGap([1, 2, 3], [1, 2, 3]));
        Assert.True(FrameTimingSummary.HistoryGap([1, 2, 3], [4, 5, 6]));
        Assert.False(FrameTimingSummary.HistoryGap([], [1, 2]));
    }
    [Fact]
    public void Android_15_layer_debug_wrapper_is_removed_but_sequence_id_is_preserved()
    {
        Assert.Equal("SurfaceView[test.app/Main](BLAST)#129", FrameTimingSummary.NormalizeLayerName("RequestedLayerState{SurfaceView[test.app/Main](BLAST)#129 parentId=128}"));
    }
    [Fact]
    public void Pending_and_empty_frames_are_not_reported_as_zero_latency()
    {
        var times = FrameTimingSummary.ParsePresentedTimes("8333333\n0 0 0\n100 9223372036854775807 100\n");
        var summary = FrameTimingSummary.From(times);
        Assert.Empty(times); Assert.Null(summary.ObservedFps); Assert.Null(summary.P95Ms);
    }
    [Fact]
    public void Actual_present_column_and_unique_frames_determine_timing()
    {
        var times = FrameTimingSummary.ParsePresentedTimes("16666666\n1 1000000000 2\n2 1010000000 3\n3 1020000000 4\n3 1020000000 4\n");
        var summary = FrameTimingSummary.From(times);
        Assert.Equal(3, summary.Frames); Assert.Equal(100, summary.ObservedFps); Assert.Equal(10, summary.P95Ms);
    }
}
