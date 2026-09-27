using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ForzaHaptics.Controllers;
using HIDMaestro;
using Microsoft.Win32;

namespace ForzaHaptics.Emulation;

public sealed class HidMaestroXboxFactory : IVirtualXboxFactory, IVirtualXboxLifecycleFactory
{
    public const string ProfileId = "xbox-series-xs-bt";
    public const string SdkVersion = "1.9.0";
    private static readonly object Gate = new();
    private static readonly object DiagnosticsGate = new();
    private readonly HidMaestroPnp _pnp = new();
    private string _operationId = "initial";
    private Action<string>? _phase;
    public bool HasPendingRemoval { get; private set; }

    public void SetOperation(string operationId, Action<string> phase)
    {
        _operationId = operationId;
        _phase = phase;
    }

    private void Phase(string name)
    {
        _phase?.Invoke(name);
        Record("hidmaestro_phase", new { operationId = _operationId, phase = name });
    }

    public void VerifyPreviousRemoval()
    {
        Phase("Waiting for Windows device removal");
        HasPendingRemoval = true;
        _pnp.Wait(true, data => Record("hidmaestro_pnp", new { operationId = _operationId, snapshot = data }));
        HasPendingRemoval = false;
    }
    private static readonly Lazy<string> BundleHash = new(ComputeBundleHash);
    private static bool _sdkContextCreated, _sdkDiagnosticsEnabled;
    private readonly Action<string, object>? _record;
    private readonly Func<bool>? _diagnosticsEnabled;

    public HidMaestroXboxFactory(Action<string, object>? record = null, Func<bool>? diagnosticsEnabled = null)
    {
        _record = record;
        _diagnosticsEnabled = diagnosticsEnabled;
    }

    public sealed record SdkDiagnosticStatus(bool Requested, bool Enabled, bool RequiresRestart, string LogPath);

    // SDK 1.9.0 snapshots its switch in a static initializer. Setting it after
    // the first context cannot reliably enable logging until the next process.
    public static SdkDiagnosticStatus ConfigureSdkDiagnostics(bool requested)
    {
        lock (DiagnosticsGate)
        {
            if (!_sdkContextCreated && requested)
            {
                Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1", EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("HIDMAESTRO_TIMING", "1", EnvironmentVariableTarget.Process);
            }
            bool enabled = _sdkContextCreated ? _sdkDiagnosticsEnabled
                : Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG") == "1" && Environment.GetEnvironmentVariable("HIDMAESTRO_TIMING") == "1";
            return new(requested, enabled, requested && !enabled && _sdkContextCreated,
                Path.Combine(Path.GetTempPath(), "HIDMaestro", "teardown_diag.log"));
        }
    }

    private static HMContext CreateContext()
    {
        lock (DiagnosticsGate)
        {
            if (!_sdkContextCreated)
                _sdkDiagnosticsEnabled = Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG") == "1"
                    && Environment.GetEnvironmentVariable("HIDMAESTRO_TIMING") == "1";
            _sdkContextCreated = true;
        }
        return new HMContext();
    }

    private void Record(string kind, object data)
    {
        // Diagnostic failures must not interfere with device cleanup or input.
        try { _record?.Invoke(kind, data); } catch { }
    }

    public sealed record InstallationStatus(bool MainPackagePresent, bool CompanionPackagePresent,
        string ExpectedManifestSha256, string? InstalledManifestSha256,
        string[] InstalledDriverVersions, string? ProbeError)
    {
        public bool MatchesBundle => ProbeError is null && MainPackagePresent && CompanionPackagePresent
            && string.Equals(ExpectedManifestSha256, InstalledManifestSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only evidence for preflight and session diagnostics. The registry
    /// manifest is the pinned SDK's successful-deployment receipt, not the SDK DLL hash.</summary>
    public static InstallationStatus ReadInstallationStatus()
    {
        string expectedHash = BundleHash.Value;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\HIDMaestro", writable: false);
            string? installedHash = key?.GetValue("InstalledManifestSha256") as string;
            string repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            string[] main = Directory.Exists(repository)
                ? Directory.GetDirectories(repository, "hidmaestro.inf_*") : [];
            string[] companion = Directory.Exists(repository)
                ? Directory.GetDirectories(repository, "hidmaestro_xusb.inf_*") : [];
            var versions = new List<string>();
            foreach (string directory in main.Concat(companion))
            {
                foreach (string filename in new[] { "HIDMaestro.dll", "HMXInput.dll" })
                {
                    string path = Path.Combine(directory, filename);
                    if (File.Exists(path))
                        versions.Add($"{Path.GetFileName(directory)}/{filename}: {FileVersionInfo.GetVersionInfo(path).FileVersion}");
                }
            }
            return new(main.Length > 0, companion.Length > 0, expectedHash, installedHash, versions.ToArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, false, expectedHash, null, [], ex.Message);
        }
    }

    private static string ComputeBundleHash()
    {
        // Public embedded resources let us implement the tagged 1.9.0 manifest
        // contract without reflection into SDK internals or constructing HMContext.
        string prefix = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "HIDMaestro.Native.arm64." : "HIDMaestro.Native.x64.";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string filename in new[] { "HIDMaestro.dll", "HMXInput.dll", "hidmaestro.inf", "hidmaestro_xusb.inf", "hmswd.exe" })
        {
            string name = prefix + filename;
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            using var resource = typeof(HMContext).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Pinned HIDMaestro SDK is missing {name}.");
            byte[] buffer = new byte[65536];
            int count;
            while ((count = resource.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void CheckAvailable()
    {
        RequireAdministrator();
        // Do not construct HMContext here: its constructor starts warm-up tasks,
        // including GameInput service startup. DriverStore probing is read-only.
        var installation = ReadInstallationStatus();
        if (!installation.MatchesBundle)
            throw new InvalidOperationException("HIDMaestro driver is missing, outdated, or its installation cannot be verified against SDK 1.9.0. Use Install / repair HIDMaestro driver first."
                + (installation.ProbeError is null ? "" : $" Probe failed: {installation.ProbeError}"));
    }

    public static void InstallOrRepair()
    {
        lock (Gate)
        {
            RequireAdministrator();
            RequireNoPresentControllers();
            // Explicit user action only: SDK installs its signing certificate and
            // driver packages, and sweeps existing HM devices during repair.
            using var context = CreateContext();
            context.InstallDriver();
        }
    }

    public IVirtualXbox Create()
    {
        lock (Gate)
        {
            CheckAvailable();
            VerifyPreviousRemoval();
            Phase("Creating Xbox device");
            // SDK 1.9.0 allocates shared-memory indices per process, not globally.
            // Refuse concurrent HM consumers instead of colliding with index 0.
            RequireNoPresentControllers();
            Record("hidmaestro_sdk_diagnostics", ConfigureSdkDiagnostics(_diagnosticsEnabled?.Invoke() == true));
            var elapsed = Stopwatch.StartNew();
            Record("hidmaestro_create_started", new { operationId = _operationId, profile = ProfileId, identity = HidMaestroPnp.Identity });
            HMContext? context = null;
            try
            {
                context = CreateContext();
                context.LoadDefaultProfiles();
                var profile = context.GetProfile(ProfileId)
                    ?? throw new InvalidOperationException($"HIDMaestro profile {ProfileId} is missing.");
                var controller = context.CreateController(profile, HidMaestroPnp.Identity);
                Phase("Checking Windows device readiness");
                _pnp.Wait(false, data => Record("hidmaestro_pnp", new { operationId = _operationId, snapshot = data }));
                var device = new VirtualXbox(context, controller, this);
                Record("hidmaestro_created", new { operationId = _operationId, profile = ProfileId, identity = controller.IdentityKey,
                    elapsedMs = elapsed.ElapsedMilliseconds,
                    readiness = "Windows device tree started with an active HID interface; Steam/game recognition is not verified" });
                return device;
            }
            catch (Exception ex)
            {
                Record("hidmaestro_create_failed", new { operationId = _operationId, elapsedMs = elapsed.ElapsedMilliseconds, error = ex.ToString() });
                if (context is not null)
                {
                    HasPendingRemoval = true;
                    Phase("Removing failed Xbox device");
                    CaptureBeforeRemoval();
                    try
                    {
                        context.Dispose();
                        Record("hidmaestro_sdk_dispose_returned", new { operationId = _operationId, failedCreate = true });
                        VerifyPreviousRemoval();
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("HIDMaestro creation and its cleanup both failed.", ex, cleanupError);
                    }
                }
                throw;
            }
        }
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Xbox Series — HIDMaestro requires running ForzaHaptics as administrator.");
    }

    private void CaptureBeforeRemoval()
    {
        try { Record("hidmaestro_pnp_before_removal", new { operationId = _operationId, nodes = _pnp.Capture() }); }
        catch (Exception ex) { Record("hidmaestro_pnp_probe_failed", new { operationId = _operationId, error = ex.ToString() }); }
    }

    private static void RequireNoPresentControllers()
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x02 | 0x04);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var id = new StringBuilder(4096);
                if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                string value = id.ToString();
                if (value.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(@"ROOT\HIDCLASS\HM_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A HIDMaestro controller is already present. Stop PadForge and other HIDMaestro consumers before creating a controller or repairing the driver. Restart Windows if a crashed consumer left a device behind.");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    internal static float Axis(short value) => value < 0 ? .5f + value / 65536f : .5f + value / 65534f;

    internal static HMButton MapButtons(ushort buttons)
    {
        HMButton result = HMButton.None;
        if ((buttons & 0x1000) != 0) result |= HMButton.A;
        if ((buttons & 0x2000) != 0) result |= HMButton.B;
        if ((buttons & 0x4000) != 0) result |= HMButton.X;
        if ((buttons & 0x8000) != 0) result |= HMButton.Y;
        if ((buttons & 0x0100) != 0) result |= HMButton.LeftBumper;
        if ((buttons & 0x0200) != 0) result |= HMButton.RightBumper;
        if ((buttons & 0x0010) != 0) result |= HMButton.Start;
        if ((buttons & 0x0020) != 0) result |= HMButton.Back;
        if ((buttons & 0x0040) != 0) result |= HMButton.LeftStick;
        if ((buttons & 0x0080) != 0) result |= HMButton.RightStick;
        if ((buttons & 0x0400) != 0) result |= HMButton.Guide;
        return result;
    }

    internal static HMHat MapHat(ushort buttons) => (buttons & 0xF) switch
    {
        1 => HMHat.North, 9 => HMHat.NorthEast, 8 => HMHat.East,
        10 => HMHat.SouthEast, 2 => HMHat.South, 6 => HMHat.SouthWest,
        4 => HMHat.West, 5 => HMHat.NorthWest, _ => HMHat.None
    };

    private sealed class VirtualXbox : IVirtualXbox
    {
        private readonly HMContext _context;
        private readonly HMController _controller;
        private readonly Action<string, object> _record;
        private readonly HidMaestroXboxFactory _owner;
        private bool _disposed;
        public event Action<XboxFeedback>? FeedbackReceived;

        public VirtualXbox(HMContext context, HMController controller, HidMaestroXboxFactory owner)
        {
            _context = context;
            _controller = controller;
            _owner = owner;
            _record = owner.Record;
            _controller.OutputReceived += OnOutput;
        }

        private void OnOutput(HMController controller, HMOutputPacket packet)
        {
            long timestamp = Environment.TickCount64;
            DateTimeOffset utc = DateTimeOffset.UtcNow;
            // Decode copies the SDK-owned scratch buffer before this callback exits.
            var feedback = XboxFeedbackDecoder.Decode(packet.Source, packet.ReportId,
                packet.Data.Span, packet.SeqNo, timestamp, utc);
            FeedbackReceived?.Invoke(feedback);
        }

        public void Submit(ControllerInputState state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var input = new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(_controller.Profile,
                    Axis(state.LX), 1 - Axis(state.LY), Axis(state.RX), 1 - Axis(state.RY),
                    state.LT / 255f, state.RT / 255f),
                Buttons = MapButtons(state.Buttons), Hat = MapHat(state.Buttons)
            };
            _controller.SubmitState(input);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.OutputReceived -= OnOutput;
            _owner.HasPendingRemoval = true;
            // Dispose this owned context; never call the SDK global removal API.
            var elapsed = Stopwatch.StartNew();
            _record("hidmaestro_dispose_started", new { operationId = _owner._operationId, identity = _controller.IdentityKey });
            try
            {
                _owner.Phase("Removing Xbox device");
                _owner.CaptureBeforeRemoval();
                _context.Dispose();
                _record("hidmaestro_sdk_dispose_returned", new { identity = _controller.IdentityKey,
                    elapsedMs = elapsed.ElapsedMilliseconds, operationId = _owner._operationId });
                _owner.VerifyPreviousRemoval();
            }
            catch (Exception ex)
            {
                _record("hidmaestro_dispose_failed", new { identity = _controller.IdentityKey,
                    elapsedMs = elapsed.ElapsedMilliseconds, operationId = _owner._operationId, error = ex.ToString() });
                throw;
            }
        }
    }
}
