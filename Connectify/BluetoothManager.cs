using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace bluetooth_turnon
{
    /// <summary>
    /// Extension methods for converting WinRT asynchronous operations to .NET Tasks safely.
    /// Handles synchronous completion, error states, and cancellations without deadlocking.
    /// </summary>
    internal static class WinRtAsyncExtensions
    {
        public static Task<T> AsTask<T>(this IAsyncOperation<T> asyncOp, CancellationToken ct = default)
        {
            if (asyncOp == null) throw new ArgumentNullException(nameof(asyncOp));

            if (asyncOp.Status == AsyncStatus.Completed)
                return Task.FromResult(asyncOp.GetResults());
            if (asyncOp.Status == AsyncStatus.Error)
            {
                var t = new TaskCompletionSource<T>();
                t.TrySetException(asyncOp.ErrorCode ?? new Exception("WinRT error."));
                return t.Task;
            }
            if (asyncOp.Status == AsyncStatus.Canceled)
            {
                var t = new TaskCompletionSource<T>();
                t.TrySetCanceled();
                return t.Task;
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => { try { asyncOp.Cancel(); } catch { } tcs.TrySetCanceled(); });

            asyncOp.Completed = (info, status) =>
            {
                ctr.Dispose();
                switch (status)
                {
                    case AsyncStatus.Completed: tcs.TrySetResult(info.GetResults()); break;
                    case AsyncStatus.Canceled: tcs.TrySetCanceled(); break;
                    case AsyncStatus.Error: tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error.")); break;
                }
            };
            return tcs.Task;
        }

        public static Task AsTask(this IAsyncAction asyncAction, CancellationToken ct = default)
        {
            if (asyncAction == null) throw new ArgumentNullException(nameof(asyncAction));

            if (asyncAction.Status == AsyncStatus.Completed) return Task.FromResult(true);
            if (asyncAction.Status == AsyncStatus.Error)
            {
                var t = new TaskCompletionSource<bool>();
                t.TrySetException(asyncAction.ErrorCode ?? new Exception("WinRT error."));
                return t.Task;
            }
            if (asyncAction.Status == AsyncStatus.Canceled)
            {
                var t = new TaskCompletionSource<bool>();
                t.TrySetCanceled();
                return t.Task;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
                ctr = ct.Register(() => { try { asyncAction.Cancel(); } catch { } tcs.TrySetCanceled(); });

            asyncAction.Completed = (info, status) =>
            {
                ctr.Dispose();
                switch (status)
                {
                    case AsyncStatus.Completed: info.GetResults(); tcs.TrySetResult(true); break;
                    case AsyncStatus.Canceled: tcs.TrySetCanceled(); break;
                    case AsyncStatus.Error: tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error.")); break;
                }
            };
            return tcs.Task;
        }
    }

    /// <summary>
    /// Event arguments for Bluetooth adapter state changes.
    /// </summary>
    public sealed class AdapterStateChangedEventArgs : EventArgs
    {
        public bool? IsEnabled { get; }
        public string StatusMessage { get; }

        public AdapterStateChangedEventArgs(bool? isEnabled, string statusMessage = null)
        {
            IsEnabled = isEnabled;
            StatusMessage = statusMessage ?? (isEnabled == true ? "Enabled" : (isEnabled == false ? "Disabled" : "No Adapter"));
        }
    }

    /// <summary>
    /// Categorizes a connected Bluetooth device so the UI can show the right icon.
    /// </summary>
    public enum BluetoothDeviceType
    {
        Unknown = 0,
        Earbuds,      // Wireless earbuds — AirPods, Galaxy Buds, Nothing Ear, Soundcore
        Headphones,   // Over-ear / on-ear — Sony WH, Bose QC, SteelSeries
        Headset,      // Headset with mic (hands-free)
        Speaker,      // Bluetooth speaker — JBL, Sony, Anker
        Microphone,   // Standalone BT mic
        Mouse,        // Bluetooth mouse
        Keyboard,     // Bluetooth keyboard
        Controller,   // Game controller — PS5 DualSense, Xbox, 8BitDo
        Phone,        // Smartphone
        Smartwatch,   // Wearable / smartwatch
        Computer,     // PC, laptop, tablet
        AudioDevice,  // Generic audio device
    }

    /// <summary>
    /// Represents a connected Bluetooth device with type classification and optional battery info.
    /// Battery is nullable — many BLE devices do not expose the battery service.
    /// </summary>
    public sealed class DeviceInfo
    {
        public string Name { get; }
        public int? BatteryPercent { get; }
        public BluetoothDeviceType DeviceType { get; }

        public DeviceInfo(string name, int? batteryPercent, BluetoothDeviceType deviceType = BluetoothDeviceType.Unknown)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BatteryPercent = batteryPercent;
            DeviceType = deviceType;
        }
    }

    /// <summary>
    /// Single source of truth for all Bluetooth adapter and device operations.
    /// Uses Windows BluetoothAdapter & Radio APIs.
    /// </summary>
    public sealed class BluetoothManager : IDisposable
    {
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Connectify", "app_log.txt");

        public static void Log(string msg)
        {
            try 
            { 
                string dir = Path.GetDirectoryName(_logPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff}: {msg}\r\n"); 
            } catch { }
        }

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private Radio _bluetoothRadio;
        private DeviceWatcher _deviceWatcher;
        private volatile bool _watcherEnumerationComplete;
        private bool _disposed;

        /// <summary>
        /// Fired whenever the Bluetooth adapter state changes (internally or externally in Windows).
        /// </summary>
        public event EventHandler<AdapterStateChangedEventArgs> AdapterStateChanged;

        /// <summary>
        /// Fired whenever a Bluetooth device connects or disconnects while the app is running.
        /// Equivalent to a Flutter stream — raised on a background thread, marshal to UI before touching controls.
        /// </summary>
        public event EventHandler DeviceConnectionChanged;

        /// <summary>
        /// Finds and initializes the Bluetooth Radio object.
        /// </summary>
        private async Task<Radio> GetBluetoothRadioAsync(CancellationToken ct = default)
        {
            if (_bluetoothRadio != null)
            {
                return _bluetoothRadio;
            }

            try
            {
                // 1. Primary: Try via BluetoothAdapter.GetDefaultAsync() (direct adapter radio query)
                try
                {
                    Log("Trying BluetoothAdapter.GetDefaultAsync()...");
                    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
                    if (adapter != null)
                    {
                        Log($"BluetoothAdapter found. Calling GetRadioAsync()...");
                        var radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
                        if (radio != null)
                        {
                            _bluetoothRadio = radio;
                            _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                            Log($"Found Bluetooth Radio via Adapter: '{radio.Name}', State={radio.State}");
                            return _bluetoothRadio;
                        }
                        else
                        {
                            Log("adapter.GetRadioAsync() returned null");
                        }
                    }
                    else
                    {
                        Log("BluetoothAdapter.GetDefaultAsync() returned null");
                    }
                }
                catch (Exception ex)
                {
                    Log($"BluetoothAdapter path exception: {ex.Message}");
                }

                // 2. Secondary: Try Radio.GetRadiosAsync() with RequestAccessAsync()
                try
                {
                    // Ensure bthserv service is running
                    await Task.Run(() =>
                    {
                        try
                        {
                            using (var sc = new ServiceController("bthserv"))
                            {
                                if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.Paused)
                                {
                                    Log("Starting bthserv service...");
                                    sc.Start();
                                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                                    Log("bthserv service is Running");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"bthserv check note: {ex.Message}");
                        }
                    }).ConfigureAwait(false);

                    Log("Calling Radio.RequestAccessAsync()");
                    var access = await Radio.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
                    Log($"Radio.RequestAccessAsync returned: {access}");

                    var radios = await Radio.GetRadiosAsync().AsTask(ct).ConfigureAwait(false);
                    if (radios != null)
                    {
                        Log($"Radio.GetRadiosAsync returned {radios.Count} radios");
                        foreach (var radio in radios)
                        {
                            Log($"Radio: Name='{radio.Name}', Kind={radio.Kind}, State={radio.State}");
                            if (radio.Kind == RadioKind.Bluetooth)
                            {
                                _bluetoothRadio = radio;
                                _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                                Log($"Found Bluetooth Radio via GetRadiosAsync: '{radio.Name}', State={radio.State}");
                                return _bluetoothRadio;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Radio.GetRadiosAsync path exception: {ex.Message}");
                }

                Log("No Bluetooth Radio found by any method.");
                return null;
            }
            catch (Exception ex)
            {
                Log($"GetBluetoothRadioAsync outer exception: {ex.Message}");
                return null;
            }
        }

        private void BluetoothRadio_StateChanged(Radio sender, object args)
        {
            try
            {
                bool isEnabled = (sender.State == RadioState.On);
                Log($"BluetoothRadio_StateChanged event received. State={sender.State}, isEnabled={isEnabled}");
                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
            }
            catch (Exception ex)
            {
                Log($"BluetoothRadio_StateChanged exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if the Bluetooth adapter is on, false if off, null if no adapter.
        /// </summary>
        public async Task<bool?> GetAdapterStateAsync(CancellationToken ct = default)
        {
            Log("GetAdapterStateAsync called");
            try
            {
                var radio = await GetBluetoothRadioAsync(ct).ConfigureAwait(false);
                if (radio != null)
                {
                    bool isEnabled = (radio.State == RadioState.On);
                    Log($"GetAdapterStateAsync returning: {isEnabled} (State={radio.State})");
                    return isEnabled;
                }

                Log("GetAdapterStateAsync returning: null (no radio found)");
                return null;
            }
            catch (Exception ex)
            {
                Log($"GetAdapterStateAsync outer exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Turns the Bluetooth adapter on or off.
        /// Returns true if successful, false otherwise.
        /// </summary>
        public async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Log($"SetAdapterStateAsync(enable={enable}) called");
                var radio = await GetBluetoothRadioAsync(ct).ConfigureAwait(false);
                if (radio == null)
                {
                    Log("SetAdapterStateAsync: Radio is null, cannot toggle");
                    return false;
                }

                RadioState targetState = enable ? RadioState.On : RadioState.Off;
                Log($"Calling radio.SetStateAsync({targetState})...");

                var result = await radio.SetStateAsync(targetState).AsTask(ct).ConfigureAwait(false);
                bool success = (result == RadioAccessStatus.Allowed);
                Log($"radio.SetStateAsync returned: {result}, success={success}");

                return success;
            }
            catch (Exception ex)
            {
                Log($"SetAdapterStateAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns only currently CONNECTED Bluetooth (Classic) devices.
        /// Devices that are paired but not actively connected are excluded.
        /// Battery is not queried in this iteration — DeviceInfo.BatteryPercent will be null.
        /// Returns an empty list if the adapter is off, no devices are connected, or any error occurs.
        /// </summary>
        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            Log("GetPairedDevicesAsync called");
            try
            {
                // Guard: only query if adapter is on
                bool? state = await GetAdapterStateAsync(ct).ConfigureAwait(false);
                if (state != true)
                {
                    Log("GetPairedDevicesAsync: adapter is off or absent, returning empty list");
                    return new List<DeviceInfo>();
                }

                // BluetoothClient.PairedDevices is a synchronous, potentially blocking call
                // so we run it on the thread pool to avoid freezing the UI thread (Rule A-2).
                var results = await Task.Run(async () =>
                {
                    var list = new List<DeviceInfo>();
                    try
                    {
                        using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            Log($"GetPairedDevicesAsync: BluetoothClient.PairedDevices returned {paired?.Count() ?? 0} device(s)");
                            if (paired != null)
                            {
                                foreach (var device in paired)
                                {
                                    ct.ThrowIfCancellationRequested();

                                    // Skip devices that are paired but not currently connected
                                    if (!device.Connected)
                                    {
                                        Log($"  Skipping '{device.DeviceName}' — paired but not connected");
                                        continue;
                                    }

                                    string name = string.IsNullOrWhiteSpace(device.DeviceName)
                                        ? "Unknown Device"
                                        : device.DeviceName;
                                    var deviceType = ClassifyDevice(device);

                                    // Pass 1: PnP device property (fastest, no BLE connection needed)
                                    // Uses the Bluetooth address to locate the HFP PnP node
                                    string addrHex = device.DeviceAddress.ToString().Replace(":", "");
                                    int? battery = QueryBatteryViaPnp(addrHex);

                                    // Pass 2: BLE GATT Battery Service fallback (for BLE-only devices)
                                    if (battery == null)
                                    {
                                        battery = await QueryBatteryViaGattAsync(
                                            device.DeviceAddress.ToUInt64(), ct).ConfigureAwait(false);
                                    }

                                    Log($"  Connected device: '{name}', Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}");
                                    list.Add(new DeviceInfo(name, batteryPercent: battery, deviceType));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"GetPairedDevicesAsync Task.Run exception: {ex.Message}");
                        // Return whatever we collected before the error
                    }
                    return list;

                }, ct).ConfigureAwait(false);

                return results;
            }
            catch (OperationCanceledException)
            {
                Log("GetPairedDevicesAsync cancelled");
                return new List<DeviceInfo>();
            }
            catch (Exception ex)
            {
                Log($"GetPairedDevicesAsync outer exception: {ex.Message}");
                return new List<DeviceInfo>();
            }
        }

        // ── Battery Reading ───────────────────────────────────────────────────

        /// <summary>
        /// Pass 1: Reads battery from the Windows PnP device property store.
        ///
        /// Windows exposes Bluetooth battery on the HFP (Hands-Free Profile) PnP
        /// device node under the property key:
        ///   GUID = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, PID = 2, Type = Byte
        ///
        /// This is the same data source that Windows Settings > Bluetooth shows
        /// as the battery badge (e.g. "90%" next to the device name).
        ///
        /// Uses SetupDi P/Invoke to read the property directly — no external
        /// process spawning, no WMI overhead.
        /// </summary>
        private static int? QueryBatteryViaPnp(string bluetoothAddressHex)
        {
            try
            {
                string addrUpper = bluetoothAddressHex
                    .Replace(":", "").Replace("-", "").ToUpperInvariant();

                Log($"    Battery PnP: searching for address '{addrUpper}'");

                // The Bluetooth HFP battery property key
                var batteryPropertyKey = new NativeMethods.DEVPROPKEY
                {
                    fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
                    pid = 2
                };

                // Enumerate all BTHENUM devices (class GUID for Bluetooth)
                Guid bluetoothClassGuid = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
                IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevs(
                    ref bluetoothClassGuid, null, IntPtr.Zero,
                    NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);

                if (devInfoSet == (IntPtr)(-1))
                {
                    // Fallback: enumerate ALL present devices if class GUID doesn't match
                    devInfoSet = NativeMethods.SetupDiGetClassDevs(
                        IntPtr.Zero, "BTHENUM", IntPtr.Zero,
                        NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);
                }

                if (devInfoSet == (IntPtr)(-1))
                {
                    Log("    Battery PnP: SetupDiGetClassDevs failed");
                    return null;
                }

                try
                {
                    var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(devInfoData);

                    for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                    {
                        // Get the instance ID to check if it contains our BT address
                        char[] instanceIdBuf = new char[512];
                        uint requiredSize;
                        if (!NativeMethods.SetupDiGetDeviceInstanceId(
                                devInfoSet, ref devInfoData, instanceIdBuf, (uint)instanceIdBuf.Length, out requiredSize))
                            continue;

                        string instanceId = new string(instanceIdBuf, 0, (int)requiredSize - 1); // minus null terminator
                        if (instanceId.IndexOf(addrUpper, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        // This device matches our BT address — try reading the battery property
                        uint propertyType;
                        byte[] buffer = new byte[4];
                        uint bufferSize = (uint)buffer.Length;
                        bool success = NativeMethods.SetupDiGetDevicePropertyW(
                            devInfoSet, ref devInfoData,
                            ref batteryPropertyKey,
                            out propertyType,
                            buffer, bufferSize, out bufferSize, 0);

                        if (success && bufferSize >= 1)
                        {
                            byte level = buffer[0];
                            if (level >= 0 && level <= 100)
                            {
                                Log($"    Battery PnP: {level}% from '{instanceId}'");
                                return (int)level;
                            }
                        }
                    }
                }
                finally
                {
                    NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
                }

                Log($"    Battery PnP: no battery found for address '{addrUpper}'");
                return null;
            }
            catch (Exception ex)
            {
                Log($"    QueryBatteryViaPnp exception for '{bluetoothAddressHex}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// P/Invoke declarations for SetupDi device property APIs.
        /// </summary>
        private static class NativeMethods
        {
            public const uint DIGCF_PRESENT    = 0x00000002;
            public const uint DIGCF_ALLCLASSES = 0x00000004;

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct DEVPROPKEY
            {
                public Guid fmtid;
                public uint pid;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct SP_DEVINFO_DATA
            {
                public uint cbSize;
                public Guid ClassGuid;
                public uint DevInst;
                public IntPtr Reserved;
            }

            [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(
                ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(
                IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiEnumDeviceInfo(
                IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern bool SetupDiGetDeviceInstanceId(
                IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
                [System.Runtime.InteropServices.Out] char[] DeviceInstanceId,
                uint DeviceInstanceIdSize, out uint RequiredSize);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern bool SetupDiGetDevicePropertyW(
                IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
                ref DEVPROPKEY PropertyKey, out uint PropertyType,
                [System.Runtime.InteropServices.Out] byte[] PropertyBuffer,
                uint PropertyBufferSize, out uint RequiredSize, uint Flags);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
        }

        /// <summary>
        /// Pass 2: Reads battery via BLE GATT Battery Service (UUID 0x180F / characteristic 0x2A19).
        /// Used as a fallback when the Windows property approach returns null.
        /// Many BLE-only devices (fitness bands, some earbuds) expose this standard service.
        /// Returns null if the device is unreachable, the service is absent, or any error occurs.
        /// </summary>
        private static async Task<int?> QueryBatteryViaGattAsync(ulong bluetoothAddress, CancellationToken ct)
        {
            try
            {
                var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                    .AsTask(ct).ConfigureAwait(false);

                if (bleDevice == null)
                {
                    Log($"    Battery GATT: BLE device not found for address {bluetoothAddress:X12}");
                    return null;
                }

                using (bleDevice)
                {
                    var servicesResult = await bleDevice
                        .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                        .AsTask(ct).ConfigureAwait(false);

                    if (servicesResult.Status != GattCommunicationStatus.Success
                        || servicesResult.Services.Count == 0)
                    {
                        Log($"    Battery GATT: Battery service not found (status={servicesResult.Status})");
                        return null;
                    }

                    using (var service = servicesResult.Services[0])
                    {
                        var charsResult = await service
                            .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                            .AsTask(ct).ConfigureAwait(false);

                        if (charsResult.Status != GattCommunicationStatus.Success
                            || charsResult.Characteristics.Count == 0)
                        {
                            Log($"    Battery GATT: BatteryLevel characteristic not found");
                            return null;
                        }

                        var readResult = await charsResult.Characteristics[0]
                            .ReadValueAsync(BluetoothCacheMode.Uncached)
                            .AsTask(ct).ConfigureAwait(false);

                        if (readResult.Status != GattCommunicationStatus.Success)
                        {
                            Log($"    Battery GATT: ReadValueAsync failed (status={readResult.Status})");
                            return null;
                        }

                        // BatteryLevel is a single byte, 0-100
                        var reader = DataReader.FromBuffer(readResult.Value);
                        byte level = reader.ReadByte();
                        Log($"    Battery GATT: {level}% for address {bluetoothAddress:X12}");
                        return (int)level;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"    QueryBatteryViaGattAsync exception for {bluetoothAddress:X12}: {ex.Message}");
                return null;
            }
        }

        // ── Device Classification ─────────────────────────────────────────────

        /// <summary>
        /// Reads the Bluetooth Class of Device (CoD) bitmask to determine device type.
        /// Falls back to name-pattern matching for unclassified CoDs.
        /// </summary>
        private static BluetoothDeviceType ClassifyDevice(InTheHand.Net.Sockets.BluetoothDeviceInfo device)
        {
            try
            {
                // CoD is a 24-bit value packed as:
                //   bits 23-13 : Service class (ignored here)
                //   bits 12-8  : Major device class
                //   bits 7-2   : Minor device class
                //   bits 1-0   : Format (always 00)
                int cod = (int)device.ClassOfDevice.Value;
                int major = (cod >> 8) & 0x1F;
                int minor = (cod >> 2) & 0x3F;

                BluetoothDeviceType fromCod = ClassifyByCod(major, minor);
                if (fromCod != BluetoothDeviceType.Unknown)
                    return fromCod;
            }
            catch (Exception ex)
            {
                Log($"  ClassifyDevice CoD exception: {ex.Message}");
            }

            // CoD was Unknown or unavailable — try name-based heuristics
            string name = device.DeviceName ?? string.Empty;
            return ClassifyByName(name);
        }

        private static BluetoothDeviceType ClassifyByCod(int major, int minor)
        {
            switch (major)
            {
                case 0x04: // Audio / Video
                    switch (minor)
                    {
                        case 0x01: return BluetoothDeviceType.Earbuds;     // Wearable headset (earbuds / AirPod style)
                        case 0x02: return BluetoothDeviceType.Headset;     // Hands-free device
                        case 0x04: return BluetoothDeviceType.Microphone;  // Microphone
                        case 0x05: return BluetoothDeviceType.Speaker;     // Loudspeaker
                        case 0x06: return BluetoothDeviceType.Headphones;  // Headphones (over-ear)
                        case 0x07: return BluetoothDeviceType.AudioDevice; // Portable audio
                        case 0x08: return BluetoothDeviceType.AudioDevice; // Car audio
                        case 0x0A: return BluetoothDeviceType.AudioDevice; // HiFi audio
                        default:   return BluetoothDeviceType.AudioDevice;
                    }

                case 0x05: // Peripheral (mouse / keyboard / controller)
                    {
                        int inputKind = (minor >> 4) & 0x03; // bits 5-4
                        int subType   =  minor       & 0x0F; // bits 3-0
                        if (inputKind == 0x01) return BluetoothDeviceType.Keyboard;
                        if (inputKind == 0x02) return BluetoothDeviceType.Mouse;
                        if (subType == 0x01 || subType == 0x02)
                            return BluetoothDeviceType.Controller; // Joystick / Gamepad
                        return BluetoothDeviceType.Mouse; // generic peripheral
                    }

                case 0x02: return BluetoothDeviceType.Phone;
                case 0x01: return BluetoothDeviceType.Computer;
                case 0x07: return BluetoothDeviceType.Smartwatch; // Wearable
                case 0x08: return BluetoothDeviceType.Controller; // Toy (8BitDo etc. may use this)
                default:   return BluetoothDeviceType.Unknown;
            }
        }

        /// <summary>
        /// Name-pattern fallback for devices whose CoD is Uncategorized or 0.
        /// Matches common brand/model keywords case-insensitively.
        /// </summary>
        private static BluetoothDeviceType ClassifyByName(string name)
        {
            string n = name.ToLowerInvariant();

            // ── Earbuds ──────────────────────────────────────────────────────
            if (n.Contains("airpod")  || n.Contains("galaxy bud") || n.Contains(" buds") ||
                n.Contains("freebuds")|| n.Contains("nothing ear")|| n.Contains("liberty") ||
                n.Contains("soundcore")|| n.Contains("jabra")     || n.Contains("r50") ||
                n.Contains("r30")     || n.Contains("a3i")        || n.Contains("p3") ||
                n.Contains("wf-")     || n.Contains("earbud")     || n.Contains("tws"))
                return BluetoothDeviceType.Earbuds;

            // ── Over-ear headphones / headset ─────────────────────────────────
            if (n.Contains("wh-")    || n.Contains("xm4")   || n.Contains("xm5") ||
                n.Contains("qc45")   || n.Contains("qc35")  || n.Contains("qc700") ||
                n.Contains("momentum")|| n.Contains("arctis")|| n.Contains("nova") ||
                n.Contains("void")   || n.Contains("cloud") || n.Contains("stealth") ||
                n.Contains("studio") || n.Contains("headphone") || n.Contains("headset"))
                return BluetoothDeviceType.Headphones;

            // ── Speakers ─────────────────────────────────────────────────────
            if (n.Contains("jbl")    || n.Contains("charge")  || n.Contains("flip") ||
                n.Contains("pulse")  || n.Contains("xtreme")  || n.Contains("boombox") ||
                n.Contains("ue boom")|| n.Contains("wonderboom") || n.Contains("homepod") ||
                n.Contains("speaker")|| n.Contains("soundbar") || n.Contains("echo"))
                return BluetoothDeviceType.Speaker;

            // ── Mouse ─────────────────────────────────────────────────────────
            if (n.Contains("mouse")  || n.Contains("mx master") || n.Contains("mx any") ||
                n.Contains("pro click")|| n.Contains("basilisk") || n.Contains("deathadder") ||
                n.Contains("pebble") || n.Contains("anywhere"))
                return BluetoothDeviceType.Mouse;

            // ── Keyboard ─────────────────────────────────────────────────────
            if (n.Contains("keyboard") || n.Contains("keychron") || n.Contains("mx keys") ||
                n.Contains("k2")      || n.Contains("k4")        || n.Contains("k8") ||
                n.Contains("k10")     || n.Contains("anne")      || n.Contains("ducky"))
                return BluetoothDeviceType.Keyboard;

            // ── Controllers ───────────────────────────────────────────────────
            if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("xbox") ||
                n.Contains("8bitdo")    || n.Contains("controller") || n.Contains("gamepad") ||
                n.Contains("joycon")    || n.Contains("pro con"))
                return BluetoothDeviceType.Controller;

            // ── Phones ────────────────────────────────────────────────────────
            if (n.Contains("iphone")   || n.Contains("galaxy s") || n.Contains("galaxy a") ||
                n.Contains("pixel")    || n.Contains("oneplus")   || n.Contains("m31") ||
                n.Contains("redmi")    || n.Contains("poco")      || n.Contains("realme") ||
                n.Contains("huawei")   || n.Contains("honor")     || n.Contains("note "))
                return BluetoothDeviceType.Phone;

            // ── Smartwatch ────────────────────────────────────────────────────
            if (n.Contains("watch")    || n.Contains("galaxy watch") || n.Contains("gear") ||
                n.Contains("band")     || n.Contains("mi band")      || n.Contains("fitbit") ||
                n.Contains("garmin")   || n.Contains("vivosmart"))
                return BluetoothDeviceType.Smartwatch;

            return BluetoothDeviceType.Unknown;
        }

        // ── Device Watcher (real-time connect/disconnect) ─────────────────────

        /// <summary>
        /// Starts a DeviceWatcher that raises DeviceConnectionChanged whenever a Bluetooth
        /// device connects or disconnects. Safe to call multiple times — stops any existing watcher first.
        /// </summary>
        public void StartDeviceWatcher()
        {
            StopDeviceWatcher();
            Log("DeviceWatcher: Starting...");

            _watcherEnumerationComplete = false;

            // Watch all Bluetooth Classic devices; request the IsConnected property so
            // Updated events fire when connection status changes.
            string selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelector();
            _deviceWatcher = DeviceInformation.CreateWatcher(
                selector,
                new string[] { "System.Devices.Aep.IsConnected" },
                DeviceInformationKind.AssociationEndpoint);

            _deviceWatcher.Added             += DeviceWatcher_Added;
            _deviceWatcher.Removed           += DeviceWatcher_Removed;
            _deviceWatcher.Updated           += DeviceWatcher_Updated;
            _deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;

            _deviceWatcher.Start();
            Log("DeviceWatcher: Started (waiting for initial enumeration to complete)");
        }

        /// <summary>
        /// Stops and cleans up the DeviceWatcher. Safe to call even if no watcher is running.
        /// </summary>
        public void StopDeviceWatcher()
        {
            if (_deviceWatcher == null) return;
            Log("DeviceWatcher: Stopping...");
            try
            {
                _deviceWatcher.Added             -= DeviceWatcher_Added;
                _deviceWatcher.Removed           -= DeviceWatcher_Removed;
                _deviceWatcher.Updated           -= DeviceWatcher_Updated;
                _deviceWatcher.EnumerationCompleted -= DeviceWatcher_EnumerationCompleted;

                if (_deviceWatcher.Status == DeviceWatcherStatus.Started ||
                    _deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _deviceWatcher.Stop();
                }
            }
            catch (Exception ex)
            {
                Log($"DeviceWatcher: StopDeviceWatcher exception: {ex.Message}");
            }
            finally
            {
                _deviceWatcher = null;
                _watcherEnumerationComplete = false;
                Log("DeviceWatcher: Stopped");
            }
        }

        private void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            _watcherEnumerationComplete = true;
            Log("DeviceWatcher: Initial enumeration complete — now live-watching for changes");
        }

        // Fires when a device that wasn't previously visible appears (i.e. device connected)
        private void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            if (!_watcherEnumerationComplete) return; // ignore initial population
            Log($"DeviceWatcher: Device connected: '{args.Name}'");
            DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // Fires when a tracked device disappears (i.e. device disconnected/unpaired)
        private void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_watcherEnumerationComplete) return;
            Log($"DeviceWatcher: Device disconnected/removed: {args.Id}");
            DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // Fires when a device property changes — including IsConnected toggling
        private void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_watcherEnumerationComplete) return;
            if (!args.Properties.ContainsKey("System.Devices.Aep.IsConnected")) return;
            Log($"DeviceWatcher: IsConnected changed for {args.Id}");
            DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopDeviceWatcher();

            if (_bluetoothRadio != null)
            {
                try
                {
                    _bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged;
                }
                catch { }
                _bluetoothRadio = null;
            }

            try { _semaphore.Dispose(); } catch { }
        }
    }
}
