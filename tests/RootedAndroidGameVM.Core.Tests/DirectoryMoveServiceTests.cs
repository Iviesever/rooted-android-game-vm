using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DirectoryMoveServiceTests
{
    [Fact]
    public async Task Move_retries_transient_windows_access_errors_while_target_is_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-directory-move", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "proof.txt"), "moved");
        var attempts = 0;
        var mover = new DirectoryMoveService(
            (from, to) =>
            {
                attempts++;
                if (attempts < 3) throw new IOException("transient scanner handle");
                Directory.Move(from, to);
            },
            _ => TimeSpan.Zero);

        try
        {
            await mover.MoveAsync(source, target);

            Assert.Equal(3, attempts);
            Assert.Equal("moved", await File.ReadAllTextAsync(Path.Combine(target, "proof.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
