using System.Buffers.Binary;
using ForzaHaptics.Controllers;

namespace ForzaHaptics.Tests;

public sealed class ControllerInputTests
{
    [Theory]
    [InlineData(ControllerTransport.Usb, 64, false)]
    [InlineData(ControllerTransport.Bluetooth, 78, false)]
    [InlineData(ControllerTransport.Bluetooth, 10, true)]
    [InlineData(ControllerTransport.Bluetooth, 78, true)]
    public void LayoutsMapNeutralAxesAndTriggers(ControllerTransport transport, int length, bool simple)
    {
        var report = Report(transport, length, simple);
        Assert.True(DualSenseInputParser.TryParse(report, transport, "physical", 123, out var input));
        Assert.Equal(new ControllerInputState("physical", 123, 0, 0, 0, 0, 42, 193, 0), input);
        if (simple)
        {
            Assert.False(DualSenseBatteryParser.TryParse(report, transport, out var battery, out _));
            Assert.Null(battery);
            Assert.False(DualSenseTriggerFeedbackParser.TryParse(report, transport, out _));
        }
    }

    [Theory]
    [InlineData(0, short.MinValue, short.MaxValue)]
    [InlineData(128, 0, 0)]
    [InlineData(255, short.MaxValue, short.MinValue)]
    public void AxesHaveExactCenterAndFullRange(byte raw, short x, short y)
    {
        var report = Report(ControllerTransport.Usb, 64, false);
        report.AsSpan(1, 4).Fill(raw);
        Assert.True(DualSenseInputParser.TryParse(report, ControllerTransport.Usb, "usb", 1, out var input));
        Assert.Equal((x, y, x, y), (input!.LX, input.LY, input.RX, input.RY));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 9)] [InlineData(2, 8)]
    [InlineData(3, 10)] [InlineData(4, 2)] [InlineData(5, 6)]
    [InlineData(6, 4)] [InlineData(7, 5)] [InlineData(8, 0)]
    public void DpadDirectionsMapToXInput(byte hat, ushort expected)
    {
        var report = Report(ControllerTransport.Usb, 64, false);
        report[8] = hat;
        DualSenseInputParser.TryParse(report, ControllerTransport.Usb, "usb", 1, out var input);
        Assert.Equal(expected, input!.Buttons);
    }

    [Theory]
    [InlineData(0, 0x10, 0x4000)] [InlineData(0, 0x20, 0x1000)]
    [InlineData(0, 0x40, 0x2000)] [InlineData(0, 0x80, 0x8000)]
    [InlineData(1, 0x01, 0x0100)] [InlineData(1, 0x02, 0x0200)]
    [InlineData(1, 0x10, 0x0020)] [InlineData(1, 0x20, 0x0010)]
    [InlineData(1, 0x40, 0x0040)] [InlineData(1, 0x80, 0x0080)]
    [InlineData(2, 0x01, 0x0400)] [InlineData(2, 0x02, 0)]
    public void ButtonsMapWithoutTouchpadOrDigitalTriggerAliases(int offset, byte bit, ushort expected)
    {
        foreach (bool simple in new[] { false, true })
        {
            var transport = simple ? ControllerTransport.Bluetooth : ControllerTransport.Usb;
            var report = Report(transport, simple ? 10 : 64, simple);
            report[(simple ? 5 : 8) + offset] |= bit;
            DualSenseInputParser.TryParse(report, transport, "id", 1, out var input);
            Assert.Equal(expected, input!.Buttons);
        }
    }

    [Fact]
    public void InvalidReportsNeverProduceInput()
    {
        var report = Report(ControllerTransport.Bluetooth, 78, false);
        report[10] ^= 1;
        Assert.False(DualSenseInputParser.TryParse(report, ControllerTransport.Bluetooth, "bt", 1, out _));
        foreach (int length in new[] { 0, 1, 9, 11, 64, 77 })
        {
            var invalid = new byte[length];
            if (length > 0) invalid[0] = 1;
            Assert.False(DualSenseInputParser.TryParse(invalid, ControllerTransport.Bluetooth, "bt", 1, out _));
        }
        Assert.False(DualSenseInputParser.TryParse(Report(ControllerTransport.Usb, 64, false),
            ControllerTransport.None, "none", 1, out _));
    }

    private static byte[] Report(ControllerTransport transport, int length, bool simple)
    {
        var report = new byte[length];
        bool enhancedBt = transport == ControllerTransport.Bluetooth && !simple;
        report[0] = enhancedBt ? (byte)0x31 : (byte)0x01;
        int common = enhancedBt ? 2 : 1;
        report.AsSpan(common, 4).Fill(128);
        report[common + (simple ? 4 : 7)] = 8;
        report[common + (simple ? 7 : 4)] = 42;
        report[common + (simple ? 8 : 5)] = 193;
        if (enhancedBt)
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), DualSenseBatteryParser.ComputeCrc(0xA1, report.AsSpan(0, 74)));
        return report;
    }

    [Fact]
    public async Task ServicePublishesSimpleReportsWithoutInventingStatus()
    {
        using var service = new ControllerService(new InputBackend(), pollMs: 10);
        var received = new TaskCompletionSource<ControllerInputState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.InputReceived += _ => throw new InvalidOperationException("subscriber failure");
        service.InputReceived += input => received.TrySetResult(input);
        var input = await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("bt", input.DeviceId);
        Assert.Equal((byte)42, input.LT);
        Assert.True(service.Snapshot.IsInputFresh);
        Assert.Null(service.Snapshot.BatteryPercent);
        Assert.Null(service.Snapshot.TriggerFeedback);
    }

    private sealed class InputBackend : IControllerBackend
    {
        public IReadOnlyList<ControllerDevice> Find() => [new("bt", "DualSense", ControllerTransport.Bluetooth, false)];
        public IControllerConnection Open(string id) => new InputConnection();
        public void Disconnect(string id) => throw new NotSupportedException();
    }

    private sealed class InputConnection : IControllerConnection
    {
        public byte[] Read()
        {
            Thread.Sleep(5);
            return Report(ControllerTransport.Bluetooth, 10, true);
        }
        public void Dispose() { }
    }
}
