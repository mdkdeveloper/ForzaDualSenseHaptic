using ForzaHaptics.Emulation;

namespace ForzaHaptics.Tests;

public sealed class HidMaestroPnpTests
{
    private static HidMaestroPnpNode Root(bool started = true) => new(HidMaestroPnp.RootId, "HTREE\\ROOT\\0", started, 0, false);
    private static HidMaestroPnpNode Child(bool started = true, uint problem = 0, bool hid = true)
        => new("HID\\OWNED\\CHILD", HidMaestroPnp.RootId, started, problem, hid);

    [Fact]
    public void NativeProbeCanReadPresentDeviceTreeWithoutChangingDevices()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FORZAHAPTICS_PNP_PROBE") != "1") return;
        var nodes = new HidMaestroPnp().Capture();
        Assert.All(nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Id)));
    }
    [Fact]
    public void StableIdentityUsesExactTaggedSdkRoot()
    {
        Assert.Equal("SWD\\HIDMAESTRO_VID_045E_PID_0B13&IG_00\\HM_" + HidMaestroPnp.Token[3..], HidMaestroPnp.RootId);
        Assert.Equal("HM_8C0E78D6315D27F5", HidMaestroPnp.Token);
    }

    [Fact]
    public void ReadinessWaitsUntilRootChildAndInterfaceAreReady()
    {
        long now = 0;
        var pnp = new HidMaestroPnp(() => now switch
        {
            0 => [],
            50 => [Root(false), Child(false)],
            100 => [Root(), Child(hid: false)],
            _ => [Root(), Child()]
        }, ms => now += ms, () => now);
        pnp.Wait(false, _ => { });
        Assert.Equal(150, now);
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 28, true)]
    [InlineData(true, 0, false)]
    public void BrokenOrMissingInterfaceDoesNotBecomeReady(bool started, uint problem, bool hid)
    {
        long now = 0;
        var pnp = new HidMaestroPnp(() => [Root(), Child(started, problem, hid)], ms => now += ms, () => now);
        Assert.Throws<InvalidOperationException>(() => pnp.Wait(false, _ => { }));
        Assert.Equal(5000, now);
    }

    [Fact]
    public void RemovalRemembersOrphanedDescendantsAfterRootDisappears()
    {
        long now = 0;
        var pnp = new HidMaestroPnp(() => now switch
        {
            0 => [Root(), Child()],
            50 => [Child() with { ParentId = null }],
            _ => []
        }, ms => now += ms, () => now);
        pnp.Wait(true, _ => { });
        Assert.Equal(100, now);
    }

    [Fact]
    public void RemovalTimeoutCanBeCheckedAgainWithoutCreatingOrRemovingAnything()
    {
        long now = 0;
        bool present = true;
        var pnp = new HidMaestroPnp(() => present ? [Root(), Child()] : [], ms => now += ms, () => now);
        Assert.Throws<VirtualXboxRemovalPendingException>(() => pnp.Wait(true, _ => { }));
        present = false;
        pnp.Wait(true, _ => { });
        Assert.Equal(5000, now);
    }

    [Fact]
    public void SameVidPidForeignControllerDoesNotCountAsOwned()
    {
        long now = 0;
        var foreign = new HidMaestroPnpNode("SWD\\HIDMAESTRO_VID_045E_PID_0B13&IG_00\\HM_OTHER", null, true, 0, true);
        var pnp = new HidMaestroPnp(() => [foreign], ms => now += ms, () => now);
        pnp.Wait(true, _ => { });
        Assert.Equal(0, now);
        Assert.Throws<InvalidOperationException>(() => pnp.Wait(false, _ => { }));
    }

    [Fact]
    public void OwnershipIncludesTransitiveChildrenRegardlessOfEnumerationOrder()
    {
        var pnp = new HidMaestroPnp(() => []);
        var grandchild = new HidMaestroPnpNode("HID\\OWNED\\GRANDCHILD", Child().Id, true, 0, true);
        Assert.Equal(3, pnp.Owned([grandchild, Child(), Root()]).Length);
    }
}
