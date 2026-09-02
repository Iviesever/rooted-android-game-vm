using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class AndroidUiContractTests
{
    [Fact]
    public void Empty_ui_snapshot_is_a_retryable_data_error()
    {
        Assert.Throws<InvalidDataException>(() => AndroidUiSnapshot.Parse(string.Empty));
    }

    [Fact]
    public void Ui_snapshot_finds_center_by_text_or_content_description()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hierarchy>
              <node text="Install" content-desc="" enabled="true" bounds="[746,586][996,712]" />
              <node text="" content-desc="Show roots" enabled="true" bounds="[0,136][147,262]" />
            </hierarchy>
            """;

        var snapshot = AndroidUiSnapshot.Parse(xml);

        Assert.Equal(new AndroidUiPoint(871, 649), snapshot.FindCenter("Install"));
        Assert.Equal(new AndroidUiPoint(73, 199), snapshot.FindCenter("Show roots"));
    }

    [Fact]
    public void Ui_snapshot_ignores_disabled_matches()
    {
        const string xml = """
            <hierarchy>
              <node text="LET'S GO" content-desc="" enabled="false" bounds="[0,0][100,100]" />
              <node text="LET'S GO" content-desc="" enabled="true" bounds="[100,100][300,300]" />
            </hierarchy>
            """;

        Assert.Equal(new AndroidUiPoint(200, 200), AndroidUiSnapshot.Parse(xml).FindCenter("LET'S GO"));
    }

    [Fact]
    public void Ui_snapshot_finds_controls_by_resource_id()
    {
        const string xml = """
            <hierarchy>
              <node text="" resource-id="com.topjohnwu.magisk:id/policy_indicator"
                    enabled="true" bounds="[870,346][996,472]" />
            </hierarchy>
            """;

        Assert.Equal(
            new AndroidUiPoint(933, 409),
            AndroidUiSnapshot.Parse(xml).FindCenterByResourceId("com.topjohnwu.magisk:id/policy_indicator"));
    }

    [Fact]
    public void Ui_snapshot_reads_checked_state_by_resource_id()
    {
        const string xml = """
            <hierarchy>
              <node resource-id="policy" enabled="true" checked="true" bounds="[0,0][10,10]" />
            </hierarchy>
            """;

        Assert.True(AndroidUiSnapshot.Parse(xml).IsCheckedByResourceId("policy"));
    }
}
