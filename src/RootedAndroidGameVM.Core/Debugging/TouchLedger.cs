namespace RootedAndroidGameVM.Core.Debugging;

public sealed class TouchLedger
{
    private readonly Dictionary<int, TouchPoint> _active = new();
    private readonly object _gate = new();
    public void Apply(IEnumerable<TouchPoint> points)
    {
        lock (_gate) foreach (var point in points) { if (point.Pressure > 0) _active[point.Id] = point; else _active.Remove(point.Id); }
    }
    public TouchPoint[] Releases() { lock (_gate) return _active.Values.OrderBy(p => p.Id).Select(p => p with { Pressure = 0 }).ToArray(); }
    public void Clear() { lock (_gate) _active.Clear(); }
}
