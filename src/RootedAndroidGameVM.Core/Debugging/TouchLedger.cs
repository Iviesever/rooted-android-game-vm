namespace RootedAndroidGameVM.Core.Debugging;

public sealed class TouchLedger
{
    private readonly Dictionary<int, TouchPoint> _active = new();
    public void Apply(IEnumerable<TouchPoint> points)
    {
        foreach (var point in points) { if (point.Pressure > 0) _active[point.Id] = point; else _active.Remove(point.Id); }
    }
    public TouchPoint[] Releases() => _active.Values.OrderBy(p => p.Id).Select(p => p with { Pressure = 0 }).ToArray();
    public void Clear() => _active.Clear();
}
