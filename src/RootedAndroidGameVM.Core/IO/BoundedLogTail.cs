using System.Text;
using System.Text.Json;

namespace RootedAndroidGameVM.Core.IO;

public sealed class BoundedLogTail
{
    private string? _path;
    private long _offset;
    private string _pending = "";
    private readonly Queue<string> _lines = new();
    private int _characters;
    public async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        if (_path != path) { _path = path; _offset = 0; _pending = ""; _lines.Clear(); _characters = 0; }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (file.Length < _offset) { _offset = 0; _pending = ""; _lines.Clear(); _characters = 0; }
        var count = (int)Math.Min(65536, file.Length - _offset);
        if (count <= 0) return null;
        file.Position = _offset; var bytes = new byte[count]; await file.ReadExactlyAsync(bytes, cancellationToken); _offset += count;
        var parts = (_pending + Encoding.UTF8.GetString(bytes)).Split('\n'); _pending = parts[^1];
        if (_pending.Length > 65536) _pending = "[单行超过界面上限]";
        foreach (var line in parts.SkipLast(1))
        {
            if (line.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                var kind = entry.TryGetProperty("type", out var type) ? type.GetString() : "log";
                _lines.Enqueue(entry.TryGetProperty("raw", out var raw) ? $"[{kind}] {raw.GetString()}" : line);
            }
            catch (JsonException) { _lines.Enqueue(line); }
        }
        _characters = _lines.Sum(line => line.Length);
        while (_lines.Count > 1500 || _characters > 512000) _characters -= _lines.Dequeue().Length;
        return string.Join('\n', _lines);
    }
}
