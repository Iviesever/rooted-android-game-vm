namespace RootedAndroidGameVM.Core.Debugging;

public static class InputSessionPolicy
{
    public static void RequireSame(string? expected, string actual)
    {
        if (expected is not null && expected != actual)
            throw new DebugException("instance_mismatch", "输入绑定的虚拟机会话已改变；拒绝向新实例发送或释放旧会话的触点。", "input_session_check");
    }
}

public sealed record InputReleaseEvidence(string State, bool Acknowledged, string? Session,
    DateTimeOffset ObservedAt, int[] RemainingOwnedSlots, string? ErrorCode, string EvidencePath);
