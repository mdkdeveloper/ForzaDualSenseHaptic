using ForzaHaptics.Controllers;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ForzaHaptics.Emulation;

public interface IVirtualXboxFactory
{
    void CheckAvailable();
    IVirtualXbox Create();
}

public interface IVirtualXbox : IDisposable
{
    event Action<byte, byte>? RumbleReceived;
    void Submit(ControllerInputState state);
}

public sealed class VirtualXboxFactory : IVirtualXboxFactory
{
    public void CheckAvailable()
    {
        using var client = Open();
    }

    public IVirtualXbox Create()
    {
        var client = Open();
        try { return new VirtualXbox(client); }
        catch { client.Dispose(); throw; }
    }

    private static ViGEmClient Open()
    {
        try { return new ViGEmClient(); }
        catch (Exception ex)
        {
            var missing = ex.GetType().Name.Contains("BusNotFound", StringComparison.Ordinal);
            throw new InvalidOperationException(missing
                ? "ViGEmBus is not installed or its driver is not running. Install ViGEmBus and restart Windows if requested."
                : $"ViGEmBus is unavailable: {ex.Message}", ex);
        }
    }

    private sealed class VirtualXbox : IVirtualXbox
    {
        private readonly ViGEmClient _client;
        private readonly IXbox360Controller _controller;
        public event Action<byte, byte>? RumbleReceived;
        private bool _disposed;

        public VirtualXbox(ViGEmClient client)
        {
            _client = client;
            _controller = client.CreateXbox360Controller();
            try
            {
                _controller.AutoSubmitReport = false;
                _controller.FeedbackReceived += OnFeedback;
                _controller.Connect();
            }
            catch
            {
                _controller.FeedbackReceived -= OnFeedback;
                (_controller as IDisposable)?.Dispose();
                throw;
            }
        }

        private void OnFeedback(object sender, Xbox360FeedbackReceivedEventArgs e)
            => RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);

        public void Submit(ControllerInputState state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _controller.SetButtonsFull(state.Buttons);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, state.LX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, state.LY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, state.RX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, state.RY);
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, state.LT);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, state.RT);
            _controller.SubmitReport();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.FeedbackReceived -= OnFeedback;
            try { _controller.Disconnect(); }
            finally
            {
                (_controller as IDisposable)?.Dispose();
                _client.Dispose();
            }
        }
    }
}