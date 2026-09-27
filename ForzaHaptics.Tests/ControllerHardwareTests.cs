using ForzaHaptics.Controllers;
using HidSharp;

namespace ForzaHaptics.Tests;

/// <summary>Explicit, opt-in hardware diagnostic. Ordinary test runs never touch a controller.</summary>
public sealed class ControllerHardwareTests
{
    [Fact]
    public async Task ConnectedControllerHardwareDiagnostic()
    {
        if (Environment.GetEnvironmentVariable("FORZAHAPTICS_HARDWARE_TEST") != "1")
            return;
        string mode = Environment.GetEnvironmentVariable("FORZAHAPTICS_HARDWARE_MODE") ?? "passive";
        Assert.Contains(mode, new[] { "passive", "haptics", "disconnect" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var engine = new HapticEngine(() => new AppConfig());
        using var controller = new ControllerService(activeDeviceId: () => engine.ActiveControllerDeviceId);
        Console.WriteLine($"Hardware diagnostic mode={mode}; engine running={engine.IsRunning}");

        for (int sample = 0; sample < 10; sample++)
        {
            await Task.Delay(500, timeout.Token);
            PrintSnapshot("engine stopped", controller.Snapshot);
            Assert.False(engine.IsRunning);
        }
        var selected = controller.Snapshot;
        Assert.True(selected.IsConnected, "No controller discovered during the five-second passive observation.");
        Assert.False(string.IsNullOrEmpty(selected.DeviceId));
        string deviceId = selected.DeviceId!;
        var hid = DeviceList.Local.GetHidDevices().SingleOrDefault(device => device.DevicePath == deviceId);
        Assert.NotNull(hid);
        Console.WriteLine($"Selected HID input={hid.GetMaxInputReportLength()}, output={hid.GetMaxOutputReportLength()}");
        PeekReports(hid, selected.Transport);

        if (mode == "haptics")
        {
            try
            {
                Assert.True(engine.Start(new EngineOptions { Test = true, ControllerDeviceId = deviceId }),
                    "Haptic session could not start for the monitored controller.");
                // Discovery and output attachment continue asynchronously after the session starts.
                for (int attempt = 0; attempt < 20 && engine.ActiveControllerDeviceId != deviceId; attempt++)
                    await Task.Delay(250, timeout.Token);
                Assert.Equal(deviceId, engine.ActiveControllerDeviceId);
                for (int sample = 0; sample < 6; sample++)
                {
                    await Task.Delay(500, timeout.Token);
                    PrintSnapshot("haptics active", controller.Snapshot);
                    Assert.True(controller.Snapshot.IsConnected);
                    Assert.Equal(deviceId, controller.Snapshot.DeviceId);
                }
                PeekReports(hid, selected.Transport);
            }
            finally
            {
                engine.Stop();
                Console.WriteLine("Haptic engine stopped and output disposed.");
            }
            Assert.False(engine.IsRunning);
            PrintSnapshot("after haptics", controller.Snapshot);
        }
        else if (mode == "disconnect")
        {
            Assert.True(selected.CanDisconnect, "Selected controller does not support Bluetooth disconnect.");
            Assert.False(engine.IsRunning);
            Console.WriteLine($"Disconnecting only selected controller: {deviceId}");
            await controller.DisconnectAsync(deviceId, timeout.Token);
            for (int sample = 0; sample < 20; sample++)
            {
                await Task.Delay(250, timeout.Token);
                bool stillEnumerated = DeviceList.Local.GetHidDevices().Any(device => device.DevicePath == deviceId);
                PrintSnapshot($"after disconnect; HID present={stillEnumerated}", controller.Snapshot);
                if (!stillEnumerated)
                    break;
            }
            Assert.DoesNotContain(DeviceList.Local.GetHidDevices(), device => device.DevicePath == deviceId);
            Assert.False(controller.Snapshot.DeviceId == deviceId && controller.Snapshot.IsConnected);
            Console.WriteLine("Target HID disappeared. Physical power state requires user observation.");
        }
    }

    private static void PrintSnapshot(string phase, ControllerSnapshot state) => Console.WriteLine(
        $"{DateTime.UtcNow:O} {phase}: connected={state.IsConnected}, transport={state.Transport}, " +
        $"battery={(state.BatteryPercent is { } percent ? percent + "%" : "unknown")}, charging={state.IsCharging}, " +
        $"canDisconnect={state.CanDisconnect}, device={state.DeviceId ?? "none"}");

    private static void PeekReports(HidDevice device, ControllerTransport transport)
    {
        using var stream = device.Open();
        stream.ReadTimeout = 250;
        var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 78)];
        for (int sample = 0; sample < 3; sample++)
        {
            try
            {
                int length = stream.Read(buffer, 0, buffer.Length);
                bool valid = DualSenseBatteryParser.TryParse(buffer.AsSpan(0, length), transport, out int? percent, out bool charging);
                string classification = length > 0 && buffer[0] == 0x01 && transport == ControllerTransport.Bluetooth
                    ? "Bluetooth simple report (battery unavailable)"
                    : valid ? "valid battery report" : "unsupported length/status or invalid CRC";
                Console.WriteLine($"Raw HID: bytes={length}, reportId={(length > 0 ? buffer[0].ToString("X2") : "none")}, " +
                    $"classification={classification}, battery={percent?.ToString() ?? "unknown"}, charging={charging}");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Raw HID: read timed out at 250 ms; no report observed.");
            }
        }
    }
}
