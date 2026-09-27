using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ForzaHaptics.Emulation;

public interface IVirtualXboxLifecycleFactory
{
    bool HasPendingRemoval { get; }
    void SetPhaseCallback(Action<string> phase);
    void VerifyPreviousRemoval();
}

public sealed class VirtualXboxRemovalPendingException(string message) : InvalidOperationException(message);

internal sealed record HidMaestroPnpNode(string Id, string? ParentId, bool Started, uint Problem, bool HidInterface);

internal sealed class HidMaestroPnp
{
    internal const string Identity = "ForzaHaptics.XboxSeries";
    // SDK v1.9.0 Internal/DeviceIdentity.cs and xbox-series-xs-bt profile.
    internal static readonly string Token = "HM_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("HIDMaestro.DeviceIdentity.v1\n" + Identity)).AsSpan(0, 8));
    internal static readonly string RootId = @"SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\" + Token;
    private readonly Func<HidMaestroPnpNode[]> _probe;
    private readonly Action<int> _sleep;
    private readonly Func<long> _now;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    internal HidMaestroPnp(Func<HidMaestroPnpNode[]>? probe = null, Action<int>? sleep = null, Func<long>? now = null)
    {
        _probe = probe ?? ReadPresent;
        _sleep = sleep ?? Thread.Sleep;
        _now = now ?? (() => Environment.TickCount64);
    }

    internal HidMaestroPnpNode[] Owned(HidMaestroPnpNode[] nodes)
    {
        _known.Add(RootId);
        bool added;
        do
        {
            added = false;
            foreach (var node in nodes)
                if (node.ParentId is not null && _known.Contains(node.ParentId)) added |= _known.Add(node.Id);
        } while (added);
        return nodes.Where(n => _known.Contains(n.Id)).ToArray();
    }

    internal HidMaestroPnpNode[] Capture() => Owned(_probe());

    internal void Wait(bool removal)
    {
        long start = _now();
        while (true)
        {
            var nodes = Owned(_probe());
            bool ready = removal ? nodes.Length == 0 :
                nodes.Any(n => n.Id.Equals(RootId, StringComparison.OrdinalIgnoreCase)) &&
                nodes.All(n => n.Started && n.Problem == 0) && nodes.Any(n => n.HidInterface);
            long elapsed = _now() - start;
            if (ready) return;
            if (elapsed >= 5000)
            {
                if (removal) throw new VirtualXboxRemovalPendingException("The previous HIDMaestro Xbox device is still present in Windows. Use Check again to verify removal; a new device will not be created.");
                throw new InvalidOperationException("HIDMaestro returned a controller, but Windows did not start its device tree and HID interface within 5 seconds.");
            }
            _sleep(50);
        }
    }

    private static HidMaestroPnpNode[] ReadPresent()
    {
        var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        IntPtr set = SetupDiGetClassDevs(ref hid, null, IntPtr.Zero, 0x02 | 0x10);
        CheckSet(set);
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new InterfaceInfo { Size = (uint)Marshal.SizeOf<InterfaceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref iface)) { CheckEnd(); break; }
                if ((iface.Flags & 1) == 0) continue; // SPINT_ACTIVE, not only a registered interface.
                var info = NewInfo();
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, ref info);
                int error = Marshal.GetLastWin32Error();
                if (error != 122) throw new Win32Exception(error);
                IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    interfaces.Add(GetId(set, ref info));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        Guid empty = Guid.Empty;
        set = SetupDiGetClassDevs(ref empty, null, IntPtr.Zero, 0x02 | 0x04);
        CheckSet(set);
        try
        {
            var result = new List<HidMaestroPnpNode>();
            for (uint i = 0; ; i++)
            {
                var info = NewInfo();
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) { CheckEnd(); break; }
                string id = GetId(set, ref info);
                string? parentId = null;
                if (CM_Get_Parent(out uint parent, info.DevInst, 0) == 0)
                {
                    var parentText = new StringBuilder(4096);
                    if (CM_Get_Device_ID(parent, parentText, parentText.Capacity, 0) == 0) parentId = parentText.ToString();
                }
                uint cr = CM_Get_DevNode_Status(out uint status, out uint problem, info.DevInst, 0);
                result.Add(new(id, parentId, cr == 0 && (status & 8) != 0, cr == 0 ? problem : uint.MaxValue, interfaces.Contains(id)));
            }
            return result.ToArray();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static DeviceInfo NewInfo() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    private static void CheckSet(IntPtr set) { if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void CheckEnd() { int e = Marshal.GetLastWin32Error(); if (e != 259) throw new Win32Exception(e); }
    private static string GetId(IntPtr set, ref DeviceInfo info)
    {
        var id = new StringBuilder(4096);
        if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return id.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceInfo { public uint Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceInfo iface, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")] private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
