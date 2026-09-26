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
        backend.Devices = [];
        await Until(() => !service.Snapshot.IsConnected);
        Assert.Null(service.Snapshot.BatteryPercent);
        backend.Devices = [Bluetooth];
        await Until(() => service.Snapshot.DeviceId == "bt" && service.Snapshot.BatteryPercent == 55);
        Assert.True(service.Snapshot.CanDisconnect);
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

    private static readonly ControllerDevice Usb = new("usb", "DualSense", ControllerTransport.Usb, false);
    private static readonly ControllerDevice Bluetooth = new("bt", "DualSense", ControllerTransport.Bluetooth, true);
    private static byte[] Report(ControllerTransport transport, byte status)
    {
        bool bt = transport == ControllerTransport.Bluetooth;
        var report = new byte[bt ? 78 : 64];
        report[0] = bt ? (byte)0x31 : (byte)0x01;
        report[bt ? 54 : 53] = status;
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
        public IControllerConnection Open(string id) => LastConnection = new FakeConnection(Devices.Single(d => d.Id == id).Transport);
        public void Disconnect(string id)
        {
            Interlocked.Increment(ref DisconnectCount);
            DisconnectGate?.Wait(TimeSpan.FromSeconds(5));
            if (FailDisconnect) throw new IOException("Native disconnect failed");
            Devices = Devices.Where(d => d.Id != id).ToArray();
        }
    }
    private sealed class FakeConnection(ControllerTransport transport) : IControllerConnection
    {
        public volatile bool Disposed;
        public byte[]? Read()
        {
            Thread.Sleep(2);
            ObjectDisposedException.ThrowIf(Disposed, this);
            return Report(transport, 0x05);
        }
        public void Dispose() => Disposed = true;
    }
}
