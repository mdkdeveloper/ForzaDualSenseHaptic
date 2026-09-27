using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForzaHaptics.Emulation;

internal sealed class HidHideControl : IHidHideControl
{
    private readonly SafeFileHandle _handle;
    public HidHideControl()
    {
        _handle = HidHideNative.CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (!_handle.IsInvalid) return;
        int code = Marshal.GetLastWin32Error();
        _handle.Dispose();
        throw new InvalidOperationException(code switch
        {
            2 or 3 => "HidHide is not installed or its driver is not running. Install HidHide and restart Windows if requested.",
            32 or 33 => "HidHide is busy. Close its Configuration Client or other configuration tools and check again.",
            _ => $"HidHide is unavailable: {new Win32Exception(code).Message} (error {code})."
        });
    }

    // Official CTL_CODE(32769, function, METHOD_BUFFERED, FILE_READ_DATA).
    private static uint Code(uint function) => (32769u << 16) | (1u << 14) | (function << 2);
    private byte[] Read(uint function, int initial = 4096)
    {
        for (int size = initial; size <= 4 * 1024 * 1024; size *= 2)
        {
            var output = new byte[size];
            if (HidHideNative.DeviceIoControl(_handle, Code(function), null, 0, output,
                    output.Length, out int count, IntPtr.Zero))
                return output.AsSpan(0, count).ToArray();
            int error = Marshal.GetLastWin32Error();
            if (error is not (122 or 234)) throw new Win32Exception(error, "HidHide could not read its configuration.");
        }
        throw new InvalidOperationException("HidHide returned an excessively large configuration.");
    }
    private void Write(uint function, byte[] input)
    {
        if (!HidHideNative.DeviceIoControl(_handle, Code(function), input, input.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide could not update its configuration.");
    }
    private List<string> ReadList(uint function) => Encoding.Unicode.GetString(Read(function))
        .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    private void WriteList(uint function, List<string> values)
        => Write(function, Encoding.Unicode.GetBytes(string.Join('\0', values) + "\0\0"));
    private bool ReadFlag(uint function)
    {
        var value = Read(function, 1);
        if (value.Length != 1) throw new InvalidOperationException("HidHide returned an invalid status.");
        return value[0] != 0;
    }
    public List<string> Applications { get => ReadList(2048); set => WriteList(2049, value); }
    public List<string> Devices { get => ReadList(2050); set => WriteList(2051, value); }
    public bool Active { get => ReadFlag(2052); set => Write(2053, [value ? (byte)1 : (byte)0]); }
    public bool Inverse => ReadFlag(2054);
    public void Dispose() => _handle.Dispose();
}

internal static class HidHideNative
{
    public static string ResolveInstanceId(string interfacePath)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var iface = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiOpenDeviceInterface(set, interfacePath, 0, ref iface))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the selected HID interface.");
            var info = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int size, ref info);
            if (size <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out _, ref info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var instance = new StringBuilder(4096);
                if (!SetupDiGetDeviceInstanceId(set, ref info, instance, instance.Capacity, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return instance.ToString();
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static string ToDevicePath(string path)
    {
        // Resolve junctions and drive aliases to the native path HidHide compares against process images.
        using var file = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve executable path for HidHide.");
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 2); // VOLUME_NAME_NT
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve executable device path for HidHide.");
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterfaceData
    { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoData
    { public int Size; public Guid ClassGuid; public uint DeviceInstance; public IntPtr Reserved; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        [Out] byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInterface(IntPtr set, string path, uint flags, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data,
        IntPtr detail, int size, out int required, ref DeviceInfoData info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfoData info,
        StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}