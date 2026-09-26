using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForzaHaptics.Controllers;

internal static class BluetoothControllerDisconnect
{
    // bthioctl.h: CTL_CODE(FILE_DEVICE_BLUETOOTH, 3, METHOD_BUFFERED, FILE_ANY_ACCESS).
    private const uint IoctlDisconnectDevice = 0x0041000C;

    internal static bool TryParseAddress(string serial, out ulong address)
    {
        string value = serial.Replace(":", "").Replace("-", "").Trim();
        address = 0;
        return value.Length == 12 && ulong.TryParse(value, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out address) && address != 0 && address != 0xFFFFFFFFFFFF;
    }

    internal static void Disconnect(ulong address)
    {
        var parameters = new RadioSearch { Size = (uint)Marshal.SizeOf<RadioSearch>() };
        IntPtr search = BluetoothFindFirstRadio(ref parameters, out var radio);
        if (search == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "No Bluetooth radio is available.");
        int lastError = 1167;
        try
        {
            do
            {
                using (radio)
                {
                    if (DeviceIoControl(radio, IoctlDisconnectDevice, ref address, sizeof(ulong),
                        IntPtr.Zero, 0, out _, IntPtr.Zero)) return;
                    lastError = Marshal.GetLastWin32Error();
                }
            } while (BluetoothFindNextRadio(search, out radio));
        }
        finally { BluetoothFindRadioClose(search); }
        throw new Win32Exception(lastError, "Windows could not disconnect the selected Bluetooth controller.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RadioSearch { public uint Size; }
    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref RadioSearch parameters, out SafeFileHandle radio);
    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(IntPtr search, out SafeFileHandle radio);
    [DllImport("BluetoothAPIs.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref ulong input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
