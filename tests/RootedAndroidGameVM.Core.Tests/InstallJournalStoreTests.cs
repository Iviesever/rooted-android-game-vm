using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class InstallJournalStoreTests
{
    [Fact]
    public async Task Stage_updates_preserve_recorded_stock_and_patched_ramdisk_hashes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rgvm-journal-{Guid.NewGuid():N}.json");
        try
        {
            var store = new InstallJournalStore(path);
            await store.UpdateAsync(
                SetupStage.Root,
                "sdk",
                "avd",
                "name",
                stockRamdiskSha256: new string('a', 64),
                patchedRamdiskSha256: new string('b', 64));
            await store.UpdateAsync(SetupStage.Verify, "sdk", "avd", "name");

            var state = await store.LoadAsync();
            Assert.NotNull(state);
            Assert.Equal(SetupStage.Verify, state.Stage);
            Assert.Equal(new string('a', 64), state.StockRamdiskSha256);
            Assert.Equal(new string('b', 64), state.PatchedRamdiskSha256);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
