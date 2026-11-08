using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public bool IsConnected { get; }
        public ulong Address { get; }

        public DeviceInfo(string name, int? batteryPercent, BluetoothDeviceType deviceType = BluetoothDeviceType.Unknown, bool isConnected = false, ulong address = 0)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BatteryPercent = batteryPercent;
            DeviceType = deviceType;
            IsConnected = isConnected;
            Address = address;
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
        /// Ensures the Windows Bluetooth Support Service (bthserv) is running.
        /// </summary>
        private static async Task EnsureBthServRunningAsync()
        {
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
        }

        /// <summary>
        /// Internal implementation to find and initialize the Bluetooth Radio object.
        /// Assumes caller handles serialization where appropriate.
        /// </summary>
        private async Task<Radio> GetBluetoothRadioCoreAsync(CancellationToken ct = default)
        {
            if (_bluetoothRadio != null)
            {
                return _bluetoothRadio;
            }

            try
            {
                // Ensure bthserv service is running before attempting adapter / radio queries
                await EnsureBthServRunningAsync().ConfigureAwait(false);

                // 1. Primary: Try via BluetoothAdapter.GetDefaultAsync() (direct adapter radio query)
                try
                {
                    Log("Trying BluetoothAdapter.GetDefaultAsync()...");
                    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
                    if (adapter != null)
                    {
                        Log("BluetoothAdapter found. Calling GetRadioAsync()...");
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
                Log($"GetBluetoothRadioCoreAsync outer exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds and initializes the Bluetooth Radio object, guarded by _semaphore.
        /// </summary>
        private async Task<Radio> GetBluetoothRadioAsync(CancellationToken ct = default)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetBluetoothRadioCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
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
        /// Reads adapter state directly without acquiring _semaphore (assumes lock is already held).
        /// </summary>
        private async Task<bool?> GetAdapterStateCoreAsync(CancellationToken ct = default)
        {
            try
            {
                var radio = await GetBluetoothRadioCoreAsync(ct).ConfigureAwait(false);
                if (radio != null)
                {
                    bool isEnabled = (radio.State == RadioState.On);
                    Log($"GetAdapterStateCoreAsync: {isEnabled} (State={radio.State})");
                    return isEnabled;
                }

                Log("GetAdapterStateCoreAsync: null (no radio found)");
                return null;
            }
            catch (Exception ex)
            {
                Log($"GetAdapterStateCoreAsync exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if the Bluetooth adapter is on, false if off, null if no adapter.
        /// Guarded by _semaphore.
        /// </summary>
        public async Task<bool?> GetAdapterStateAsync(CancellationToken ct = default)
        {
            Log("GetAdapterStateAsync called");
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetAdapterStateCoreAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"GetAdapterStateAsync outer exception: {ex.Message}");
                return null;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Turns the Bluetooth adapter on or off.
        /// Returns true if successful, false otherwise.
        /// Guarded by _semaphore.
        /// </summary>
        public async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Log($"SetAdapterStateAsync(enable={enable}) called");
                var radio = await GetBluetoothRadioCoreAsync(ct).ConfigureAwait(false);
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
        /// Guarded by _semaphore so background scans never collide with connection operations.
        /// </summary>
        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            Log("GetPairedDevicesAsync called");
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Guard: only query if adapter is on
                bool? state = await GetAdapterStateCoreAsync(ct).ConfigureAwait(false);
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

                                    try
                                    {
                                        device.Refresh();
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"GetPairedDevicesAsync: device.Refresh() failed for '{device.DeviceName}': {ex.Message}");
                                    }

                                    bool isConnected = device.Connected;
                                    ulong address = 0;
                                    try { address = device.DeviceAddress.ToUInt64(); } catch { }

                                    string name = string.IsNullOrWhiteSpace(device.DeviceName)
                                        ? "Unknown Device"
                                        : device.DeviceName;
                                    var deviceType = ClassifyDevice(device);
                                    
                                    int? battery = null;

                                    if (isConnected)
                                    {
                                        // Pass 1: PnP device property (fastest, no BLE connection needed)
                                        // Uses the Bluetooth address to locate the HFP PnP node
                                        string addrHex = device.DeviceAddress.ToString().Replace(":", "");
                                        battery = QueryBatteryViaPnp(addrHex);

                                        // Pass 2: BLE GATT Battery Service fallback (for BLE-only devices)
                                        if (battery == null)
                                        {
                                            battery = await QueryBatteryViaGattAsync(address, ct).ConfigureAwait(false);
                                        }
                                    }

                                    Log($"  Paired device: '{name}', Connected={isConnected}, Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}");
                                    list.Add(new DeviceInfo(name, battery, deviceType, isConnected, address));
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
                    // Sort: connected devices first, then alphabetically by name
                    return list.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();

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
            finally
            {
                _semaphore.Release();
            }
        }

        // ── Device Connection & Core Audio Reconnect ─────────────────────────

        private static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        private const uint KSPROPERTY_TYPE_GET = 0x00000001;
        private const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
        private const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
        private const int EDataFlow_eAll = 2;
        private const uint DEVICE_STATEMASK_ALL = 0x0000000F;
        private const uint STGM_READ = 0x00000000;
        private const uint CLSCTX_ALL = 23;

        private static readonly Guid[] TargetConnectionProfiles = new[]
        {
            InTheHand.Net.Bluetooth.BluetoothService.AudioSink,
            InTheHand.Net.Bluetooth.BluetoothService.Handsfree,
            InTheHand.Net.Bluetooth.BluetoothService.Headset,
            InTheHand.Net.Bluetooth.BluetoothService.HumanInterfaceDevice
        };

        /// <summary>
        /// Requests uncached RFCOMM services for the device with a 4-second timeout.
        /// Forces Windows to open an active ACL baseband radio link to the paired device.
        /// </summary>
        private static async Task WakeBasebandLinkAsync(ulong address, CancellationToken ct)
        {
            try
            {
                Log($"  Waking baseband link for {address:X12} via WinRT uncached RFCOMM discovery...");
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    var winRtDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);

                    if (winRtDevice != null)
                    {
                        using (winRtDevice)
                        {
                            var rfcommResult = await winRtDevice.GetRfcommServicesAsync(BluetoothCacheMode.Uncached)
                                .AsTask(linkedCts.Token).ConfigureAwait(false);
                            Log($"  WinRT RFCOMM wake completed. Found {rfcommResult?.Services?.Count ?? 0} service(s), Error={rfcommResult?.Error}");
                        }
                    }
                    else
                    {
                        Log("  WinRT BluetoothDevice was null for address.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("  WinRT baseband link wake timed out (4s) or was cancelled; proceeding with connection.");
            }
            catch (Exception ex)
            {
                Log($"  WinRT baseband link wake exception: {ex.Message}; proceeding with connection.");
            }
        }

        /// <summary>
        /// Attempts instant reconnection or disconnection for audio endpoints via Windows Core Audio (IKsControl)
        /// using KSPROPSETID_BtAudio and KSPROPERTY_ONESHOT_RECONNECT / KSPROPERTY_ONESHOT_DISCONNECT.
        /// </summary>
        private static bool TrySendCoreAudioBtCommand(ulong address, string deviceName, bool connect)
        {
            try
            {
                Log($"  CoreAudio: Attempting {(connect ? "reconnect" : "disconnect")} via IKsControl for {address:X12} ('{deviceName}')...");
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator == null) return false;

                IMMDeviceCollection collection = null;
                int hr = enumerator.EnumAudioEndpoints(EDataFlow_eAll, DEVICE_STATEMASK_ALL, out collection);
                if (hr != 0 || collection == null)
                {
                    Marshal.ReleaseComObject(enumerator);
                    return false;
                }

                uint count = 0;
                collection.GetCount(out count);
                Log($"  CoreAudio: Found {count} audio endpoint(s)");

                string addrHex12 = address.ToString("X12").ToUpperInvariant();
                string addrHexClean = address.ToString("X").ToUpperInvariant();
                bool anyTriggered = false;

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice endpoint = null;
                    try
                    {
                        if (collection.Item(i, out endpoint) != 0 || endpoint == null) continue;

                        bool isMatch = false;

                        // 1. Check ID string
                        string endpointId;
                        if (endpoint.GetId(out endpointId) == 0 && !string.IsNullOrEmpty(endpointId))
                        {
                            string idUpper = endpointId.ToUpperInvariant();
                            if (idUpper.Contains(addrHex12) || (!string.IsNullOrEmpty(addrHexClean) && idUpper.Contains(addrHexClean)))
                            {
                                isMatch = true;
                            }
                        }

                        // 2. Check property store (FriendlyName and Bluetooth Address)
                        IPropertyStore propStore = null;
                        try
                        {
                            if (endpoint.OpenPropertyStore(STGM_READ, out propStore) == 0 && propStore != null)
                            {
                                var keyBtAddr = new PROPERTYKEY(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1);
                                PROPVARIANT pv;
                                if (propStore.GetValue(ref keyBtAddr, out pv) == 0)
                                {
                                    if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                                    {
                                        string btAddrProp = Marshal.PtrToStringUni(pv.pwszVal);
                                        if (!string.IsNullOrEmpty(btAddrProp))
                                        {
                                            string clean = btAddrProp.Replace(":", "").Replace("-", "").ToUpperInvariant();
                                            if (clean.Contains(addrHex12) || clean.Contains(addrHexClean))
                                            {
                                                isMatch = true;
                                            }
                                        }
                                    }
                                    NativeMethods.PropVariantClear(ref pv);
                                }

                                if (!isMatch && !string.IsNullOrEmpty(deviceName))
                                {
                                    var keyFriendlyName = new PROPERTYKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
                                    if (propStore.GetValue(ref keyFriendlyName, out pv) == 0)
                                    {
                                        if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                                        {
                                            string fn = Marshal.PtrToStringUni(pv.pwszVal);
                                            if (!string.IsNullOrEmpty(fn) && (fn.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0 || deviceName.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0))
                                            {
                                                isMatch = true;
                                            }
                                        }
                                        NativeMethods.PropVariantClear(ref pv);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (propStore != null) Marshal.ReleaseComObject(propStore);
                        }

                        if (!isMatch) continue;

                        Log($"  CoreAudio: Endpoint matched: '{endpointId}'");

                        // Get IDeviceTopology
                        Guid iidTopology = typeof(IDeviceTopology).GUID;
                        object topologyObj = null;
                        if (endpoint.Activate(ref iidTopology, CLSCTX_ALL, IntPtr.Zero, out topologyObj) != 0 || topologyObj == null)
                            continue;

                        IDeviceTopology endpointTopology = (IDeviceTopology)topologyObj;
                        try
                        {
                            IConnector endpointConnector = null;
                            if (endpointTopology.GetConnector(0, out endpointConnector) != 0 || endpointConnector == null)
                                continue;

                            try
                            {
                                IConnector adapterConnector = null;
                                if (endpointConnector.GetConnectedTo(out adapterConnector) != 0 || adapterConnector == null)
                                    continue;

                                try
                                {
                                    IPart adapterPart = adapterConnector as IPart;
                                    if (adapterPart == null) continue;

                                    IDeviceTopology adapterTopology = null;
                                    if (adapterPart.GetTopologyObject(out adapterTopology) != 0 || adapterTopology == null)
                                        continue;

                                    try
                                    {
                                        string adapterDeviceId = null;
                                        if (adapterTopology.GetDeviceId(out adapterDeviceId) != 0 || string.IsNullOrEmpty(adapterDeviceId))
                                            continue;

                                        IMMDevice adapterDevice = null;
                                        if (enumerator.GetDevice(adapterDeviceId, out adapterDevice) != 0 || adapterDevice == null)
                                            continue;

                                        try
                                        {
                                            Guid iidKsControl = typeof(IKsControl).GUID;
                                            object ksControlObj = null;
                                            IKsControl ksControl = null;

                                            if (adapterDevice.Activate(ref iidKsControl, CLSCTX_ALL, IntPtr.Zero, out ksControlObj) == 0 && ksControlObj != null)
                                            {
                                                ksControl = ksControlObj as IKsControl;
                                            }

                                            if (ksControl == null && adapterPart.Activate(CLSCTX_ALL, ref iidKsControl, out ksControlObj) == 0 && ksControlObj != null)
                                            {
                                                ksControl = ksControlObj as IKsControl;
                                            }

                                            if (ksControl != null)
                                            {
                                                try
                                                {
                                                    var ksProp = new KSIDENTIFIER
                                                    {
                                                        Set = KSPROPSETID_BtAudio,
                                                        Id = connect ? KSPROPERTY_ONESHOT_RECONNECT : KSPROPERTY_ONESHOT_DISCONNECT,
                                                        Flags = KSPROPERTY_TYPE_GET
                                                    };
                                                    uint bytesRet = 0;
                                                    int ksHr = ksControl.KsProperty(ref ksProp, (uint)Marshal.SizeOf(ksProp), IntPtr.Zero, 0, out bytesRet);
                                                    Log($"  CoreAudio: KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}' returned hr=0x{ksHr:X8}");
                                                    if (ksHr >= 0)
                                                    {
                                                        anyTriggered = true;
                                                    }
                                                }
                                                finally
                                                {
                                                    Marshal.ReleaseComObject(ksControl);
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            if (adapterDevice != null) Marshal.ReleaseComObject(adapterDevice);
                                        }
                                    }
                                    finally
                                    {
                                        if (adapterTopology != null) Marshal.ReleaseComObject(adapterTopology);
                                    }
                                }
                                finally
                                {
                                    if (adapterConnector != null) Marshal.ReleaseComObject(adapterConnector);
                                }
                            }
                            finally
                            {
                                if (endpointConnector != null) Marshal.ReleaseComObject(endpointConnector);
                            }
                        }
                        finally
                        {
                            if (endpointTopology != null) Marshal.ReleaseComObject(endpointTopology);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  CoreAudio endpoint loop exception: {ex.Message}");
                    }
                    finally
                    {
                        if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                    }
                }

                if (collection != null) Marshal.ReleaseComObject(collection);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                Log($"  CoreAudio: Completed. anyTriggered={anyTriggered}");
                return anyTriggered;
            }
            catch (Exception ex)
            {
                Log($"  CoreAudio outer exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to connect to a paired Bluetooth device using WinRT baseband link wake,
        /// Core Audio IKsControl reconnect, and profile cycling across supported profiles.
        /// Verifies actual connection status before returning. Guarded by _semaphore.
        /// </summary>
        public async Task<bool> ConnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            Log($"ConnectDeviceAsync called for {address:X12}");
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(async () =>
                {
                    try
                    {
                        using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                            if (device == null)
                            {
                                Log($"  ConnectDeviceAsync: device {address:X12} not found in PairedDevices");
                                return false;
                            }

                            // Step 1: Refresh device state before acting
                            device.Refresh();
                            Log($"  device.Refresh() called for '{device.DeviceName}', initial Connected={device.Connected}");
                            if (device.Connected)
                            {
                                Log($"  '{device.DeviceName}' is already connected.");
                                return true;
                            }

                            // Step 2: Wake baseband link via WinRT uncached discovery (with 4s timeout)
                            await WakeBasebandLinkAsync(address, ct).ConfigureAwait(false);

                            // Step 4: Add Optional Windows Core Audio (IKsControl) Reconnect for Audio Endpoints
                            bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, device.DeviceName, connect: true);
                            Log($"  CoreAudio reconnect triggered: {coreAudioTriggered}");

                            // If CoreAudio succeeded, check if connected quickly
                            if (coreAudioTriggered)
                            {
                                await Task.Delay(200, ct).ConfigureAwait(false);
                                device.Refresh();
                                if (device.Connected)
                                {
                                    Log($"  CoreAudio reconnected '{device.DeviceName}' immediately.");
                                    return true;
                                }
                            }

                            // Step 3: Cycle Service States (false then true) Across Supported Profiles
                            // (AudioSink, Handsfree, Headset, HumanInterfaceDevice)
                            foreach (var profile in TargetConnectionProfiles)
                            {
                                ct.ThrowIfCancellationRequested();
                                try
                                {
                                    Log($"  Disabling service {profile} on '{device.DeviceName}'...");
                                    device.SetServiceState(profile, false);
                                }
                                catch (Exception ex)
                                {
                                    Log($"  SetServiceState({profile}, false) note: {ex.Message}");
                                }

                                try
                                {
                                    await Task.Delay(150, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }

                                try
                                {
                                    Log($"  Enabling service {profile} on '{device.DeviceName}'...");
                                    device.SetServiceState(profile, true);
                                }
                                catch (Exception ex)
                                {
                                    Log($"  SetServiceState({profile}, true) note: {ex.Message}");
                                }
                            }

                            // Step 5: Verify Actual Connection Status Before Returning (poll up to 3 times, every 400ms)
                            bool connected = false;
                            for (int attempt = 1; attempt <= 3; attempt++)
                            {
                                try
                                {
                                    await Task.Delay(400, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }

                                try
                                {
                                    device.Refresh();
                                    Log($"  Connection verify attempt {attempt}/3: Connected={device.Connected}");
                                    if (device.Connected)
                                    {
                                        connected = true;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"  Connection verify attempt {attempt} exception: {ex.Message}");
                                }
                            }

                            return connected;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"ConnectDeviceAsync exception: {ex.Message}");
                        return false;
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log($"ConnectDeviceAsync cancelled for {address:X12}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Attempts to disconnect a paired Bluetooth device using Core Audio IKsControl disconnect
        /// and clearing service states across supported profiles. Guarded by _semaphore.
        /// </summary>
        public async Task<bool> DisconnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            Log($"DisconnectDeviceAsync called for {address:X12}");
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(async () =>
                {
                    try
                    {
                        using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                            if (device == null)
                            {
                                Log($"  DisconnectDeviceAsync: device {address:X12} not found in PairedDevices");
                                return false;
                            }

                            // Step 1: Refresh device state before acting
                            device.Refresh();
                            Log($"  device.Refresh() called for '{device.DeviceName}', Connected={device.Connected}");
                            if (!device.Connected)
                            {
                                Log($"  '{device.DeviceName}' is already disconnected.");
                                return true;
                            }

                            // Core Audio disconnect
                            bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, device.DeviceName, connect: false);
                            Log($"  CoreAudio disconnect triggered: {coreAudioTriggered}");

                            // Step 2: Disable services across target profiles (AudioSink, Handsfree, Headset, HumanInterfaceDevice)
                            foreach (var profile in TargetConnectionProfiles)
                            {
                                try
                                {
                                    Log($"  Disabling service {profile} on '{device.DeviceName}'...");
                                    device.SetServiceState(profile, false);
                                }
                                catch (Exception ex)
                                {
                                    Log($"  Disconnect SetServiceState({profile}, false) note: {ex.Message}");
                                }
                            }

                            // Poll up to 3 times (every 400 ms) to verify disconnection
                            bool disconnected = false;
                            for (int attempt = 1; attempt <= 3; attempt++)
                            {
                                try
                                {
                                    await Task.Delay(400, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }

                                try
                                {
                                    device.Refresh();
                                    Log($"  Disconnection verify attempt {attempt}/3: Connected={device.Connected}");
                                    if (!device.Connected)
                                    {
                                        disconnected = true;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"  Disconnection verify attempt {attempt} exception: {ex.Message}");
                                }
                            }

                            return disconnected || !device.Connected;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"DisconnectDeviceAsync exception: {ex.Message}");
                        return false;
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log($"DisconnectDeviceAsync cancelled for {address:X12}");
                return false;
            }
            finally
            {
                _semaphore.Release();
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

                // Primary: Enumerate all present devices on the BTHENUM bus (scopes strictly to Bluetooth PnP nodes)
                IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevs(
                    IntPtr.Zero, "BTHENUM", IntPtr.Zero,
                    NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);

                if (devInfoSet == (IntPtr)(-1))
                {
                    // Fallback: enumerate by Bluetooth device setup class GUID (without DIGCF_ALLCLASSES so the GUID filter is respected)
                    Guid bluetoothClassGuid = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
                    devInfoSet = NativeMethods.SetupDiGetClassDevs(
                        ref bluetoothClassGuid, null, IntPtr.Zero,
                        NativeMethods.DIGCF_PRESENT);
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
                        {
                            // If buffer was too small, dynamically resize to requiredSize and retry
                            if (requiredSize > instanceIdBuf.Length)
                            {
                                instanceIdBuf = new char[requiredSize];
                                if (!NativeMethods.SetupDiGetDeviceInstanceId(
                                        devInfoSet, ref devInfoData, instanceIdBuf, (uint)instanceIdBuf.Length, out requiredSize))
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }
                        }

                        if (requiredSize <= 1 || requiredSize > instanceIdBuf.Length)
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

            [System.Runtime.InteropServices.DllImport("ole32.dll")]
            public static extern int PropVariantClear(ref PROPVARIANT pvar);
        }

        #region Core Audio & Kernel Streaming Interop

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(
                int dataFlow,
                uint dwStateMask,
                out IMMDeviceCollection ppDevices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(
                int dataFlow,
                int role,
                out IMMDevice ppEndpoint);

            [PreserveSig]
            int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
                out IMMDevice ppDevice);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr pClient);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint pcDevices);

            [PreserveSig]
            int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                [In] ref Guid iid,
                uint dwClsCtx,
                IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

            [PreserveSig]
            int OpenPropertyStore(
                uint stgmAccess,
                out IPropertyStore ppProperties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

            [PreserveSig]
            int GetState(out uint pdwState);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint cProps);

            [PreserveSig]
            int GetAt(uint iProp, out PROPERTYKEY pkey);

            [PreserveSig]
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

            [PreserveSig]
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

            [PreserveSig]
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;

            public PROPERTYKEY(Guid guid, uint id)
            {
                fmtid = guid;
                pid = id;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public IntPtr pwszVal;
            [FieldOffset(8)] public uint uintVal;
            [FieldOffset(8)] public byte byteVal;
            [FieldOffset(8)] public long longVal;
        }

        [ComImport]
        [Guid("2A042143-8270-404A-814C-9715B87D0079")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDeviceTopology
        {
            [PreserveSig]
            int GetConnectorCount(out uint pCount);

            [PreserveSig]
            int GetConnector(uint nIndex, out IConnector ppConnector);

            [PreserveSig]
            int GetSubunitCount(out uint pCount);

            [PreserveSig]
            int GetSubunit(uint nIndex, out IntPtr ppSubunit);

            [PreserveSig]
            int GetPartById(uint nId, out IPart ppPart);

            [PreserveSig]
            int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string pDeviceId);

            [PreserveSig]
            int GetSignalPath(IPart pIPartFrom, IPart pIPartTo, bool bRejectProcesses, out IntPtr ppParts);
        }

        [ComImport]
        [Guid("9c2c4058-23f5-41de-877a-df4af236a2e1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IConnector
        {
            [PreserveSig]
            int GetType(out int pType);

            [PreserveSig]
            int GetDataFlow(out int pFlow);

            [PreserveSig]
            int ConnectTo(IConnector pConnectTo);

            [PreserveSig]
            int Disconnect();

            [PreserveSig]
            int IsConnected(out bool pbConnected);

            [PreserveSig]
            int GetConnectedTo(out IConnector ppConTo);

            [PreserveSig]
            int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrConnectorId);

            [PreserveSig]
            int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
        }

        [ComImport]
        [Guid("AE2DE0E4-5BCA-478D-AA87-4FA972203306")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPart
        {
            [PreserveSig]
            int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrName);

            [PreserveSig]
            int GetLocalId(out uint pnId);

            [PreserveSig]
            int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrGlobalId);

            [PreserveSig]
            int GetPartType(out int pPartType);

            [PreserveSig]
            int GetSubType(out Guid pSubType);

            [PreserveSig]
            int GetControlInterfaceCount(out uint pCount);

            [PreserveSig]
            int GetControlInterface(uint nIndex, out IntPtr ppInterfaceDesc);

            [PreserveSig]
            int EnumPartsIncoming(out IntPtr ppParts);

            [PreserveSig]
            int EnumPartsOutgoing(out IntPtr ppParts);

            [PreserveSig]
            int GetTopologyObject(out IDeviceTopology ppTopology);

            [PreserveSig]
            int Activate(uint dwClsCtx, [In] ref Guid refiid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        [ComImport]
        [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IKsControl
        {
            [PreserveSig]
            int KsProperty(
                [In] ref KSIDENTIFIER Property,
                uint PropertyLength,
                IntPtr PropertyData,
                uint DataLength,
                out uint BytesReturned);

            [PreserveSig]
            int KsMethod(
                [In] ref KSIDENTIFIER Method,
                uint MethodLength,
                IntPtr MethodData,
                uint DataLength,
                out uint BytesReturned);

            [PreserveSig]
            int KsEvent(
                [In] ref KSIDENTIFIER Event,
                uint EventLength,
                IntPtr EventData,
                uint DataLength,
                out uint BytesReturned);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KSIDENTIFIER
        {
            public Guid Set;
            public uint Id;
            public uint Flags;
        }

        #endregion

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
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    var token = linkedCts.Token;

                    var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                        .AsTask(token).ConfigureAwait(false);

                    if (bleDevice == null)
                    {
                        Log($"    Battery GATT: BLE device not found for address {bluetoothAddress:X12}");
                        return null;
                    }

                    using (bleDevice)
                    {
                        var servicesResult = await bleDevice
                            .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                            .AsTask(token).ConfigureAwait(false);

                        try
                        {
                            if (servicesResult.Status != GattCommunicationStatus.Success
                                || servicesResult.Services == null
                                || servicesResult.Services.Count == 0)
                            {
                                Log($"    Battery GATT: Battery service not found (status={servicesResult.Status})");
                                return null;
                            }

                            var service = servicesResult.Services[0];
                            var charsResult = await service
                                .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                                .AsTask(token).ConfigureAwait(false);

                            if (charsResult.Status != GattCommunicationStatus.Success
                                || charsResult.Characteristics == null
                                || charsResult.Characteristics.Count == 0)
                            {
                                Log("    Battery GATT: BatteryLevel characteristic not found");
                                return null;
                            }

                            var readResult = await charsResult.Characteristics[0]
                                .ReadValueAsync(BluetoothCacheMode.Uncached)
                                .AsTask(token).ConfigureAwait(false);

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
                        finally
                        {
                            if (servicesResult?.Services != null)
                            {
                                foreach (var svc in servicesResult.Services)
                                {
                                    try { svc.Dispose(); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log($"    Battery GATT: Read timed out (3s) or cancelled for {bluetoothAddress:X12}");
                return null;
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
