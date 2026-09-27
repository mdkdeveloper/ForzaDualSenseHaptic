using ForzaHaptics.Emulation;
using Xunit;

namespace ForzaHaptics.Tests;

public sealed class HidHideDriverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaHidHideTests", Guid.NewGuid().ToString("N"));
    private readonly FakeControl _control = new();
    private HidHideService Create() => new(() => _control, _ => @"HID\SELECTED", () => @"\Device\Volume\Forza.exe",
        Path.Combine(_directory, "journal.json"));

    [Fact]
    public void RestorePreservesExistingEntriesAndInitialState()
    {
        _control.Applications = ["existing.exe"];
        _control.Devices = [@"HID\OTHER"];
        _control.Active = true;
        using var service = Create();
        service.AllowApplication();
        service.Hide("selected-interface");
        Assert.Contains(@"HID\SELECTED", _control.Devices);
        service.Restore();
        Assert.Equal(["existing.exe"], _control.Applications);
        Assert.Equal([@"HID\OTHER"], _control.Devices);
        Assert.True(_control.Active);
    }

    [Fact]
    public void FreshInstanceRecoversWriteAheadJournal()
    {
        var crashed = Create();
        crashed.AllowApplication();
        crashed.Hide("selected-interface");
        Assert.True(_control.Active);
        using var restarted = Create();
        restarted.Recover();
        Assert.Empty(_control.Devices);
        Assert.Empty(_control.Applications);
        Assert.False(_control.Active);
        restarted.Recover();
    }

    [Fact]
    public void RestoreDoesNotDisableAnotherApplicationsDevice()
    {
        using var service = Create();
        service.AllowApplication();
        service.Hide("selected-interface");
        _control.Devices = [.. _control.Devices, @"HID\ADDED_BY_OTHER"];
        _control.Applications = [.. _control.Applications, "new-other.exe"];
        service.Restore();
        Assert.Equal([@"HID\ADDED_BY_OTHER"], _control.Devices);
        Assert.Equal(["new-other.exe"], _control.Applications);
        Assert.True(_control.Active);
    }

    [Fact]
    public void PreexistingApplicationAndDeviceAreNotOwned()
    {
        _control.Applications = [@"\Device\Volume\Forza.exe"];
        _control.Devices = [@"HID\SELECTED"];
        using var service = Create();
        service.AllowApplication();
        service.Hide("selected-interface");
        service.Restore();
        Assert.Single(_control.Applications);
        Assert.Single(_control.Devices);
        Assert.False(_control.Active);
    }

    [Fact]
    public void InverseModeIsRejectedWithoutChanges()
    {
        _control.Inverse = true;
        using var service = Create();
        Assert.Throws<InvalidOperationException>(service.CheckAvailable);
        Assert.Throws<InvalidOperationException>(service.AllowApplication);
        Assert.Empty(_control.Applications);
        Assert.Empty(_control.Devices);
    }

    [Fact]
    public void FailedDriverWriteCanBeRecovered()
    {
        using var service = Create();
        service.AllowApplication();
        _control.FailNextDeviceWrite = true;
        Assert.Throws<IOException>(() => service.Hide("selected-interface"));
        service.Restore();
        Assert.Empty(_control.Applications);
        Assert.Empty(_control.Devices);
        Assert.False(_control.Active);
    }

    [Fact]
    public void ExistingJournalMustBeRecoveredBeforeNewSession()
    {
        var crashed = Create();
        crashed.AllowApplication();
        using var restarted = Create();
        Assert.Throws<InvalidOperationException>(restarted.AllowApplication);
        restarted.Recover();
        restarted.AllowApplication();
        Assert.Single(_control.Applications);
    }

    [Fact]
    public void InactiveUnrelatedBlacklistIsNotActivated()
    {
        _control.Devices = [@"HID\OTHER"];
        using var service = Create();
        service.AllowApplication();
        Assert.Throws<InvalidOperationException>(() => service.Hide("selected-interface"));
        Assert.False(_control.Active);
        Assert.Equal([@"HID\OTHER"], _control.Devices);
        service.Restore();
    }

    [Theory]
    [InlineData("{\"AddedDevices\":null}")]
    [InlineData("not-json")]
    public void CorruptJournalDoesNotModifyDriver(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "journal.json"), contents);
        _control.Devices = [@"HID\OTHER"];
        _control.Applications = ["other.exe"];
        var service = Create();
        Assert.Throws<InvalidOperationException>(service.Recover);
        Assert.Equal([@"HID\OTHER"], _control.Devices);
        Assert.Equal(["other.exe"], _control.Applications);
        Assert.True(File.Exists(Path.Combine(_directory, "journal.json")));
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FakeControl : IHidHideControl
    {
        private List<string> _devices = [];
        private List<string> _applications = [];
        public bool FailNextDeviceWrite { get; set; }
        public List<string> Applications { get => [.. _applications]; set => _applications = [.. value]; }
        public List<string> Devices
        {
            get => [.. _devices];
            set
            {
                if (FailNextDeviceWrite)
                {
                    FailNextDeviceWrite = false;
                    throw new IOException("Driver write failed");
                }
                _devices = [.. value];
            }
        }
        public bool Active { get; set; }
        public bool Inverse { get; set; }
        public void Dispose() { }
    }
}