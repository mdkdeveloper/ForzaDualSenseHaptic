using System.Buffers.Binary;
using ForzaHaptics.Controllers;

namespace ForzaHaptics.Tests;

public sealed class ControllerBackendTests
{
    [Theory]
    [InlineData(0x00, 5, false)]
    [InlineData(0x05, 55, false)]
    [InlineData(0x0A, 100, false)]
    [InlineData(0x13, 35, true)]
    [InlineData(0x1A, 100, true)]
    [InlineData(0x20, 100, false)]
    [InlineData(0xA0, null, false)]
    [InlineData(0xF0, null, false)]
    [InlineData(0x0F, null, false)]
    public void UsbAndBluetoothDecodeStatus(int status, int? expected, bool charging)
    {
        foreach (var transport in new[] { ControllerTransport.Usb, ControllerTransport.Bluetooth })
        {
            Assert.True(DualSenseBatteryParser.TryParse(Report(transport, (byte)status), transport, out var percent, out var actualCharging));
            Assert.Equal(expected, percent);
            Assert.Equal(charging, actualCharging);
        }
    }

    [Fact]
    public void BadCrcShortPacketsAndMismatchedTransportAreRejected()
    {
        var bt = Report(ControllerTransport.Bluetooth, 0x16);
        bt[54] ^= 1;
        Assert.False(DualSenseBatteryParser.TryParse(bt, ControllerTransport.Bluetooth, out var percent, out _));
        Assert.Null(percent);
        Assert.False(DualSenseBatteryParser.TryParse(bt.AsSpan(0, 60), ControllerTransport.Bluetooth, out _, out _));
        Assert.False(DualSenseBatteryParser.TryParse(new byte[] { 1, 0 }, ControllerTransport.Usb, out _, out _));
        Assert.False(DualSenseBatteryParser.TryParse(Report(ControllerTransport.Usb, 0x10), ControllerTransport.Bluetooth, out _, out _));
        Assert.Equal(0xCBF43926u, DualSenseBatteryParser.ComputeCrc((byte)'1', "23456789"u8));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFul)]
    [InlineData("01-23-45-67-89-ab", 0x0123456789ABul)]
    public void BluetoothAddressUsesSerialMac(string serial, ulong expected)
    {
        Assert.True(BluetoothControllerDisconnect.TryParseAddress(serial, out var address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000000")]
    [InlineData("FFFFFFFFFFFF")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("controller1234")]
    public void InvalidAddressCannotDisconnect(string serial) => Assert.False(BluetoothControllerDisconnect.TryParseAddress(serial, out _));

    [Fact]
    public async Task MonitoringTracksConnectDisconnectReconnectWithoutAnEngine()
    {
        var backend = new FakeBackend();
        using var service = new ControllerService(backend, pollMs: 10);
        Assert.False(service.Snapshot.IsConnected);
        backend.Devices = [Usb];
        await Until(() => service.Snapshot.BatteryPercent == 55);
        Assert.Equal("usb", service.Snapshot.DeviceId);
        Assert.NotNull(service.Snapshot.TriggerFeedback);
        Assert.Equal(41, service.Snapshot.LeftTrigger);
        Assert.Equal(183, service.Snapshot.RightTrigger);
        Assert.True(service.Snapshot.IsInputFresh);
        Assert.Contains("L2 off/other", service.Snapshot.TriggerFeedbackText);
        backend.Devices = [];
        await Until(() => !service.Snapshot.IsConnected);
        Assert.Null(service.Snapshot.BatteryPercent);
        Assert.Null(service.Snapshot.TriggerFeedback);
        backend.Devices = [Bluetooth];
        await Until(() => service.Snapshot.DeviceId == "bt" && service.Snapshot.BatteryPercent == 55);
        Assert.True(service.Snapshot.CanDisconnect);
    }

    [Fact]
    public async Task InvalidReportStreamCannotRefreshPhysicalInput()
    {
        var backend = new FakeBackend { Devices = [Bluetooth] };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.IsInputFresh);
        backend.InvalidReports = true;
        await Until(() => !service.Snapshot.IsInputFresh);
        Assert.Equal("physical L2/R2: unavailable", service.Snapshot.PhysicalTriggerText);
        backend.InvalidReports = false;
        await Until(() => service.Snapshot.IsInputFresh);
        Assert.Equal(183, service.Snapshot.RightTrigger);
    }

    [Fact]
    public async Task RunningDeviceOverridesOutputSettingAndUsbPreference()
    {
        var backend = new FakeBackend { Devices = [Usb, Bluetooth] };
        using var service = new ControllerService(backend, () => "usb", () => "bt", pollMs: 10);
        await Until(() => service.Snapshot.DeviceId == "bt");
        backend.Devices = [Usb];
        await Until(() => !service.Snapshot.IsConnected);
    }

    [Fact]
    public async Task ExplicitOutputModeSelectsCorrectTransport()
    {
        var backend = new FakeBackend { Devices = [Usb, Bluetooth] };
        using var service = new ControllerService(backend, () => "bt", pollMs: 10);
        await Until(() => service.Snapshot.DeviceId == "bt");
    }

    [Fact]
    public async Task UsbAndStaleIdentityNeverReachNativeDisconnect()
    {
        var backend = new FakeBackend { Devices = [Usb] };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisconnectAsync("usb", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisconnectAsync("other", TestContext.Current.CancellationToken));
        Assert.Equal(0, backend.DisconnectCount);
    }

    [Fact]
    public async Task DisconnectFailurePreservesActualConnectionAndAllowsRetry()
    {
        var backend = new FakeBackend { Devices = [Bluetooth], FailDisconnect = true };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => service.DisconnectAsync("bt", TestContext.Current.CancellationToken));
        Assert.True(service.Snapshot.IsConnected);
        backend.FailDisconnect = false;
        await service.DisconnectAsync("bt", TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsConnected);
    }

    [Fact]
    public async Task ConcurrentDisconnectIsRejectedAndInputClosesBeforeNativeCall()
    {
        using var gate = new ManualResetEventSlim();
        var backend = new FakeBackend { Devices = [Bluetooth], DisconnectGate = gate };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.BatteryPercent.HasValue);
        var first = service.DisconnectAsync("bt", TestContext.Current.CancellationToken);
        try
        {
            await Until(() => backend.DisconnectCount == 1);
            Assert.True(backend.LastConnection!.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisconnectAsync("bt", TestContext.Current.CancellationToken));
        }
        finally { gate.Set(); }
        await first;
        Assert.False(service.Snapshot.IsConnected);
    }

    [Fact]
    public async Task StaleDiscoveryCannotResurrectDisconnectedController()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend { Devices = [Bluetooth], FindEntered = entered, FindRelease = release };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.BatteryPercent.HasValue);
        Volatile.Write(ref backend.BlockNextFind, 1);
        try
        {
            await Until(() => entered.IsSet);
            await service.DisconnectAsync("bt", TestContext.Current.CancellationToken);
            Assert.False(service.Snapshot.IsConnected);
        }
        finally { release.Set(); }
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(service.Snapshot.IsConnected);
        Assert.True(backend.LastConnection!.Disposed);
    }

    [Fact]
    public async Task AddressAvailabilityRefreshesForSameDevice()
    {
        var backend = new FakeBackend { Devices = [Bluetooth with { CanDisconnect = false }] };
        using var service = new ControllerService(backend, pollMs: 10);
        await Until(() => service.Snapshot.IsConnected);
        Assert.False(service.Snapshot.CanDisconnect);
        backend.Devices = [Bluetooth];
        await Until(() => service.Snapshot.CanDisconnect);
    }

    [Theory]
    [InlineData(ControllerTransport.Usb)]
    [InlineData(ControllerTransport.Bluetooth)]
    public void TriggerFeedbackUsesDistinctModeAndMotorStateNibbles(ControllerTransport transport)
    {
        var report = Report(transport, 0x05);
        int common = transport == ControllerTransport.Bluetooth ? 2 : 1;
        report[common + 41] = 0x19; // Right loaded, stop zone 9.
        report[common + 42] = 0x07; // Left not vibrating, stop zone 7.
        report[common + 47] = 0x31; // Left vibration, right feedback.
        if (transport == ControllerTransport.Bluetooth)
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), DualSenseBatteryParser.ComputeCrc(0xA1, report.AsSpan(0, 74)));
        Assert.True(DualSenseTriggerFeedbackParser.TryParse(report, transport, out var feedback));
        Assert.Equal(new ControllerTriggerFeedback(3, 1, 0, 1), feedback);
        Assert.Equal("L2 vibration state=0x0 / R2 feedback state=0x1", feedback!.ToString());
        Assert.False(DualSenseTriggerFeedbackParser.TryParse(report.AsSpan(0, report.Length - 1), transport, out _));
        Assert.False(DualSenseTriggerFeedbackParser.TryParse([], transport, out _));
        Assert.False(DualSenseTriggerFeedbackParser.TryParse(report, ControllerTransport.None, out _));
        report[0] = 0x02;
        Assert.False(DualSenseTriggerFeedbackParser.TryParse(report, transport, out _));
    }

    [Fact]
    public void CorruptBluetoothTriggerFeedbackIsNeverPublished()
    {
        var report = Report(ControllerTransport.Bluetooth, 0x05);
        report[49] = 0x11;
        Assert.False(DualSenseTriggerFeedbackParser.TryParse(report, ControllerTransport.Bluetooth, out var feedback));
        Assert.Null(feedback);
    }

    [Fact]
    public void UnknownControllerEffectsRetainRawCodesAndStaleFeedbackIsUnavailable()
    {
        var report = Report(ControllerTransport.Usb, 0x05);
        report[48] = 0xA7;
        report[43] = 0xF0;
        Assert.True(DualSenseTriggerFeedbackParser.TryParse(report, ControllerTransport.Usb, out var feedback));
        Assert.Equal(new ControllerTriggerFeedback(10, 7, 15, 0), feedback);
        var snapshot = new ControllerSnapshot { IsConnected = true, TriggerFeedback = feedback, LastInputTick = Environment.TickCount64 };
        Assert.Contains("unknown(0xA) state=0xF", snapshot.TriggerFeedbackText);
        Assert.Contains("unknown(0x7)", snapshot.TriggerFeedbackText);
        Assert.Equal("controller reported: unavailable", (snapshot with { LastInputTick = Environment.TickCount64 - 501 }).TriggerFeedbackText);
        Assert.Equal("controller reported: unavailable", (snapshot with { IsConnected = false }).TriggerFeedbackText);
        Assert.Equal("controller reported: unavailable", (snapshot with { TriggerFeedback = null }).TriggerFeedbackText);
    }
    [Theory]
    [InlineData(ControllerTransport.Usb)]
    [InlineData(ControllerTransport.Bluetooth)]
    public void PhysicalTravelUsesValidatedCommonInputBytes(ControllerTransport transport)
    {
        var report = Report(transport, 0x05);
        int common = transport == ControllerTransport.Bluetooth ? 2 : 1;
        report[common + 4] = 37;
        report[common + 5] = 219;
        if (transport == ControllerTransport.Bluetooth)
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), DualSenseBatteryParser.ComputeCrc(0xA1, report.AsSpan(0, 74)));
        Assert.True(DualSensePhysicalInputParser.TryParse(report, transport, out byte left, out byte right));
        Assert.Equal(37, left);
        Assert.Equal(219, right);
        Assert.False(DualSensePhysicalInputParser.TryParse(report.AsSpan(0, 10), transport, out _, out _));
        Assert.False(DualSensePhysicalInputParser.TryParse(report, ControllerTransport.None, out _, out _));
        if (transport == ControllerTransport.Bluetooth)
        {
            report[common + 4] ^= 1;
            Assert.False(DualSensePhysicalInputParser.TryParse(report, transport, out left, out right));
            Assert.Equal(0, left);
            Assert.Equal(0, right);
        }
    }

    [Fact]
    public void PhysicalInputFreshnessIsBoundedAndNeverInferredFromConnection()
    {
        var snapshot = new ControllerSnapshot { IsConnected = true, LastInputTick = 1000, LeftTrigger = 200 };
        Assert.True(snapshot.IsInputFreshAt(1300));
        Assert.False(snapshot.IsInputFreshAt(1301));
        Assert.False(snapshot.IsInputFreshAt(999));
        Assert.False((snapshot with { LastInputTick = 0 }).IsInputFreshAt(100));
        Assert.False((snapshot with { IsConnected = false }).IsInputFreshAt(1000));
    }

    private static readonly ControllerDevice Usb = new("usb", "DualSense", ControllerTransport.Usb, false);
    private static readonly ControllerDevice Bluetooth = new("bt", "DualSense", ControllerTransport.Bluetooth, true);
    private static byte[] Report(ControllerTransport transport, byte status)
    {
        bool bt = transport == ControllerTransport.Bluetooth;
        var report = new byte[bt ? 78 : 64];
        report[0] = bt ? (byte)0x31 : (byte)0x01;
        report[bt ? 54 : 53] = status;
        report[(bt ? 2 : 1) + 4] = 41;
        report[(bt ? 2 : 1) + 5] = 183;
        if (bt) BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), DualSenseBatteryParser.ComputeCrc(0xA1, report.AsSpan(0, 74)));
        return report;
    }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        Assert.True(condition(), "Controller state did not converge within two seconds.");
    }
    private sealed class FakeBackend : IControllerBackend
    {
        public volatile ControllerDevice[] Devices = [];
        public bool FailDisconnect;
        public volatile bool InvalidReports;
        public int DisconnectCount;
        public ManualResetEventSlim? DisconnectGate;
        public FakeConnection? LastConnection;
        public int BlockNextFind;
        public ManualResetEventSlim? FindEntered, FindRelease;
        public IReadOnlyList<ControllerDevice> Find()
        {
            var snapshot = Devices;
            if (Interlocked.Exchange(ref BlockNextFind, 0) == 1)
            {
                FindEntered!.Set();
                FindRelease!.Wait(TimeSpan.FromSeconds(5));
            }
            return snapshot;
        }
        public IControllerConnection Open(string id) => LastConnection = new FakeConnection(Devices.Single(d => d.Id == id).Transport, () => InvalidReports);
        public void Disconnect(string id)
        {
            Interlocked.Increment(ref DisconnectCount);
            DisconnectGate?.Wait(TimeSpan.FromSeconds(5));
            if (FailDisconnect) throw new IOException("Native disconnect failed");
            Devices = Devices.Where(d => d.Id != id).ToArray();
        }
    }
    private sealed class FakeConnection(ControllerTransport transport, Func<bool> invalidReports) : IControllerConnection
    {
        public volatile bool Disposed;
        public byte[]? Read()
        {
            Thread.Sleep(2);
            ObjectDisposedException.ThrowIf(Disposed, this);
            return invalidReports() ? [0x01, 0] : Report(transport, 0x05);
        }
        public void Dispose() => Disposed = true;
    }
}
