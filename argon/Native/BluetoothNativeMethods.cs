using System;
using System.Runtime.InteropServices;

namespace bluetooth_turnon
{
    /// <summary>
    /// P/Invoke declarations for SetupDi, Bluetooth radio discovery, and IOCTL device control.
    /// </summary>
    internal static class NativeMethods
    {
        public const uint DIGCF_PRESENT    = 0x00000002;
        public const uint DIGCF_ALLCLASSES = 0x00000004;
        public const uint DEVPROP_TYPE_BYTE = 0x00000003;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            [Out] char[] DeviceInstanceId,
            uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(
            IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey, out uint PropertyType,
            [Out] byte[] PropertyBuffer,
            uint PropertyBufferSize, out uint RequiredSize, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiOpenDeviceInfo(
            IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);

        public const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000C;

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref long lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        public static bool DisconnectDeviceViaIoctl(ulong address)
        {
            if (address == 0) return false;

            var radioHandles = new System.Collections.Generic.List<IntPtr>();
            IntPtr hFind = IntPtr.Zero;
            try
            {
                var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS();
                radioParams.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));

                IntPtr hRadio = IntPtr.Zero;
                hFind = BluetoothFindFirstRadio(ref radioParams, out hRadio);
                if (hFind != IntPtr.Zero && hRadio != IntPtr.Zero)
                {
                    radioHandles.Add(hRadio);
                    while (BluetoothFindNextRadio(hFind, out hRadio))
                    {
                        if (hRadio != IntPtr.Zero)
                        {
                            radioHandles.Add(hRadio);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  DisconnectDeviceViaIoctl radio enumeration exception: {ex.Message}");
            }
            finally
            {
                if (hFind != IntPtr.Zero)
                {
                    try { BluetoothFindRadioClose(hFind); } catch { }
                }
            }

            try
            {
                if (radioHandles.Count == 0)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, "  DisconnectDeviceViaIoctl: No local Bluetooth radio handle found.");
                    return false;
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectDeviceViaIoctl: Enumerated {radioHandles.Count} radio(s). Sending IOCTL_BTH_DISCONNECT_DEVICE for {address:X12}...");
                bool anyDisconnected = false;
                long inBuffer = (long)address;

                for (int i = 0; i < radioHandles.Count; i++)
                {
                    IntPtr hRadio = radioHandles[i];
                    int bytesReturned = 0;
                    bool success = DeviceIoControl(
                        hRadio,
                        IOCTL_BTH_DISCONNECT_DEVICE,
                        ref inBuffer,
                        sizeof(long),
                        IntPtr.Zero,
                        0,
                        out bytesReturned,
                        IntPtr.Zero);

                    if (success)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectDeviceViaIoctl: IOCTL succeeded on radio #{i + 1}");
                        anyDisconnected = true;
                    }
                    else
                    {
                        int win32Err = Marshal.GetLastWin32Error();
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  DisconnectDeviceViaIoctl: Radio #{i + 1} DeviceIoControl returned false. Win32 error={win32Err}");
                    }
                }

                return anyDisconnected;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  DisconnectDeviceViaIoctl exception: {ex.Message}");
                return false;
            }
            finally
            {
                foreach (var hRadio in radioHandles)
                {
                    if (hRadio != IntPtr.Zero)
                    {
                        try { CloseHandle(hRadio); } catch { }
                    }
                }
            }
        }
    }
}
