using RootedAndroidGameVM.Core.IO;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class BoundedLogTailTests
{
    [Fact]
    public async Task Tail_reads_only_new_content_and_preserves_partial_records()
    {
        var path = Path.GetTempFileName();
        try
        {
            var reader = new BoundedLogTail();
            await File.WriteAllTextAsync(path, "{\"type\":\"app\",\"raw\":\"fir");
            Assert.Equal("", await reader.ReadAsync(path));
            await File.AppendAllTextAsync(path, "st\"}\n");
            Assert.Equal("[app] first", await reader.ReadAsync(path));
            Assert.Null(await reader.ReadAsync(path));
            await File.WriteAllTextAsync(path, "reset\n");
            Assert.Equal("reset", await reader.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Long_log_retention_is_bounded()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(0, 4000).Select(i => i + ":" + new string('x', 500) + "\n")));
            var reader = new BoundedLogTail(); string? text;
            do { text = await reader.ReadAsync(path); if (text is not null) Assert.True(text.Length <= 513500); } while (text is not null);
        }
        finally { File.Delete(path); }
    }
}
