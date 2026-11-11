using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Sockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace bluetooth_turnon.Services
{
    public sealed class DeviceEnumerator : IDeviceEnumerator
    {
        private readonly IRadioService _radioService;
        private readonly IBatteryService _batteryService;
        private readonly IConnectionService _connectionService;

        private DeviceWatcher _classicWatcher;
        private DeviceWatcher _bleWatcher;
        private volatile bool _classicEnumerationComplete;
        private volatile bool _bleEnumerationComplete;
        private readonly object _watcherDebounceLock = new object();
        private readonly ConcurrentDictionary<ulong, (string Name, BluetoothDeviceType Type, bool IsConnected)> _cachedDevices =
            new ConcurrentDictionary<ulong, (string Name, BluetoothDeviceType Type, bool IsConnected)>();
        private System.Threading.Timer _watcherDebounceTimer;
        private readonly object _snapshotLock = new object();
        private IReadOnlyList<DeviceInfo> _cachedPairedDevicesSnapshot;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private Task<IReadOnlyList<DeviceInfo>> _ongoingSnapshotTask;
        private bool _disposed;

        public event EventHandler DeviceConnectionChanged;

        public DeviceEnumerator(IRadioService radioService, IBatteryService batteryService, IConnectionService connectionService)
        {
            _radioService = radioService ?? throw new ArgumentNullException(nameof(radioService));
            _batteryService = batteryService;
            _connectionService = connectionService;
        }

        public void InvalidateDeviceSnapshotCache()
        {
            lock (_snapshotLock)
            {
                _lastSnapshotUtc = DateTime.MinValue;
                _cachedPairedDevicesSnapshot = null;
            }
        }

        private void TriggerDeviceConnectionChangedDebounced()
        {
            if (_disposed) return;
            lock (_watcherDebounceLock)
            {
                if (_watcherDebounceTimer == null)
                {
                    _watcherDebounceTimer = new System.Threading.Timer(_ =>
                    {
                        if (!_disposed)
                        {
                            InvalidateDeviceSnapshotCache();
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "DeviceWatcher: Debounce elapsed (500ms). Raising DeviceConnectionChanged.");
                            DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }, null, 500, Timeout.Infinite);
                }
                else
                {
                    _watcherDebounceTimer.Change(500, Timeout.Infinite);
                }
            }
        }

        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetPairedDevicesAsync called");
            if (_radioService.IsRadioBounceInProgress)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetPairedDevicesAsync: radio bounce in progress; suppressing device refresh");
                return null;
            }

            var radio = _radioService.BluetoothRadio;
            bool? state = (radio != null && radio.State != RadioState.Unknown)
                ? (radio.State == RadioState.On)
                : await _radioService.GetAdapterStateAsync(ct).ConfigureAwait(false);

            if (state != true)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetPairedDevicesAsync: adapter is off or absent, returning empty list");
                InvalidateDeviceSnapshotCache();
                return new List<DeviceInfo>();
            }

            Task<IReadOnlyList<DeviceInfo>> taskToAwait = null;
            lock (_snapshotLock)
            {
                if (_cachedPairedDevicesSnapshot != null && (DateTime.UtcNow - _lastSnapshotUtc).TotalMilliseconds < 750)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetPairedDevicesAsync: Returning cached 750ms snapshot");
                    return _cachedPairedDevicesSnapshot;
                }

                if (_ongoingSnapshotTask != null && !_ongoingSnapshotTask.IsCompleted)
                {
                    taskToAwait = _ongoingSnapshotTask;
                }
                else
                {
                    _ongoingSnapshotTask = EnumeratePairedDevicesCoreAsync(ct);
                    taskToAwait = _ongoingSnapshotTask;
                }
            }

            try
            {
                var snapshot = await taskToAwait.ConfigureAwait(false);
                lock (_snapshotLock)
                {
                    if (snapshot != null)
                    {
                        _cachedPairedDevicesSnapshot = snapshot;
                        _lastSnapshotUtc = DateTime.UtcNow;
                    }
                }
                return snapshot;
            }
            catch (Exception)
            {
                lock (_snapshotLock)
                {
                    if (_ongoingSnapshotTask == taskToAwait)
                    {
                        _ongoingSnapshotTask = null;
                    }
                }
                throw;
            }
        }

        private async Task<IReadOnlyList<DeviceInfo>> EnumeratePairedDevicesCoreAsync(CancellationToken ct)
        {

            try
            {
                var results = await Task.Run(async () =>
                {
                    var list = new List<DeviceInfo>();
                    var disconnectedClassicAddresses = new HashSet<ulong>();

                    var pairedBleAddresses = new HashSet<ulong>();
                    var pairedBleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        string preBleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                        var preBleCollection = await DeviceInformation.FindAllAsync(
                            preBleSelector,
                            new string[] { "System.Devices.Aep.IsConnected" }
                        ).AsTask(ct).ConfigureAwait(false);

                        if (preBleCollection != null)
                        {
                            foreach (var devInfo in preBleCollection)
                            {
                                if (!string.IsNullOrEmpty(devInfo.Name))
                                    pairedBleNames.Add(devInfo.Name);
                                ulong parsedAddr = BatteryPolicy.TryExtractAddressFromBleDeviceId(devInfo.Id);
                                if (parsedAddr != 0)
                                    pairedBleAddresses.Add(parsedAddr);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: Pre-scan BLE collection note: {ex.Message}");
                    }

                    // 1. Enumerate Classic Bluetooth paired devices
                    try
                    {
                        using (var client = new BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: BluetoothClient.PairedDevices returned {paired?.Count() ?? 0} Classic device(s)");
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
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: device.Refresh() failed for '{device.DeviceName}': {ex.Message}");
                                    }

                                    bool isConnected = device.Connected;
                                    ulong address = 0;
                                    try { address = device.DeviceAddress.ToUInt64(); } catch { }

                                    string name = string.IsNullOrWhiteSpace(device.DeviceName)
                                        ? "Unknown Device"
                                        : device.DeviceName;

                                    int codMajor = 0;
                                    int codMinor = 0;
                                    try
                                    {
                                        int cod = (int)device.ClassOfDevice.Value;
                                        codMajor = (cod >> 8) & 0x1F;
                                        codMinor = (cod >> 2) & 0x3F;
                                    }
                                    catch { }

                                    var deviceType = DeviceClassifier.ClassifyByCod(codMajor, codMinor);
                                    if (deviceType == BluetoothDeviceType.Unknown)
                                    {
                                        deviceType = DeviceClassifier.ClassifyByName(name);
                                    }

                                    int? battery = null;

                                    if (!isConnected)
                                    {
                                        disconnectedClassicAddresses.Add(address);
                                    }
                                    else
                                    {
                                        bool isBlePaired = (address != 0 && pairedBleAddresses.Contains(address)) ||
                                                           (!string.IsNullOrEmpty(name) && pairedBleNames.Contains(name));

                                        string addrHex = address.ToString("X12");
                                        battery = _batteryService?.QueryBatteryViaPnp(addrHex);

                                        if (_batteryService != null)
                                        {
                                            var cacheEntry = _batteryService.BatteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });

                                            if (battery != null)
                                            {
                                                cacheEntry.RecordSuccess(battery.Value, name);
                                            }
                                            else
                                            {
                                                battery = cacheEntry.GetEffectiveBattery();

                                                if (!isBlePaired)
                                                {
                                                    if (cacheEntry.Outcome != BatteryQueryOutcome.Unsupported)
                                                    {
                                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Battery GATT: Skipped for '{name}' ({address:X12}) — device is Classic-only (no BLE pairing). Caching as Unsupported.");
                                                        cacheEntry.RecordUnsupported(name, isClassicOnly: true);
                                                    }
                                                }
                                                else
                                                {
                                                    if (battery == null || cacheEntry.ShouldRetry())
                                                    {
                                                        _batteryService.ScheduleDeferredBatteryQuery(address, name, isClassicOnly: false, immediate: true);
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    int installedCount = 0;
                                    string installedProfilesStr = "none";
                                    try
                                    {
                                        var installed = device.InstalledServices;
                                        if (installed != null)
                                        {
                                            installedCount = installed.Count;
                                            installedProfilesStr = installedCount > 0
                                                ? string.Join(", ", installed.Select(BluetoothManager.GetProfileName))
                                                : "empty";
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        installedProfilesStr = $"Error: {ex.Message}";
                                    }

                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Paired Classic device: '{name}' ({address:X12}), Connected={isConnected}, Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}, InstalledServices count={installedCount} [{installedProfilesStr}]");
                                    list.Add(new DeviceInfo(name, battery, deviceType, isConnected, address));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetPairedDevicesAsync Classic enumeration exception: {ex.Message}");
                    }

                    // 2. Enumerate Bluetooth LE paired devices
                    try
                    {
                        string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                        var bleCollection = await DeviceInformation.FindAllAsync(
                            bleSelector,
                            new string[] { "System.Devices.Aep.IsConnected" }
                        ).AsTask(ct).ConfigureAwait(false);

                        if (bleCollection != null)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: Found {bleCollection.Count} paired BLE device(s)");
                            foreach (var devInfo in bleCollection)
                            {
                                ct.ThrowIfCancellationRequested();

                                BluetoothLEDevice bleDevice = null;
                                bool isCachedHandle = false;
                                ulong extractedAddr = BatteryPolicy.TryExtractAddressFromBleDeviceId(devInfo.Id);
                                ulong address = extractedAddr;
                                try
                                {
                                    if (_connectionService != null && extractedAddr != 0 &&
                                        _connectionService.ConnectedBleDevices.TryGetValue(extractedAddr, out var existingTracked) &&
                                        existingTracked != null)
                                    {
                                        bleDevice = existingTracked;
                                        isCachedHandle = true;
                                    }
                                    else
                                    {
                                        using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                                        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                                        {
                                            bleDevice = await BluetoothLEDevice.FromIdAsync(devInfo.Id)
                                                .AsTask(linkedCts.Token).ConfigureAwait(false);
                                        }
                                    }

                                    if (bleDevice == null)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: FromIdAsync returned null for '{devInfo.Name}' ({devInfo.Id})");
                                        continue;
                                    }

                                    address = bleDevice.BluetoothAddress;
                                    if (address == 0)
                                    {
                                        address = extractedAddr;
                                    }
                                    if (address == 0)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetPairedDevicesAsync: BLE device '{bleDevice.Name}' address was 0; skipping");
                                        continue;
                                    }

                                    if (!isCachedHandle && _connectionService != null && _connectionService.ConnectedBleDevices.TryGetValue(address, out var cachedHandle) && cachedHandle != null)
                                    {
                                        if (!ReferenceEquals(bleDevice, cachedHandle))
                                        {
                                            try { bleDevice.Dispose(); } catch { }
                                        }
                                        bleDevice = cachedHandle;
                                        isCachedHandle = true;
                                    }

                                    string name = !string.IsNullOrWhiteSpace(bleDevice.Name)
                                        ? bleDevice.Name
                                        : (!string.IsNullOrWhiteSpace(devInfo.Name) ? devInfo.Name : "Unknown BLE Device");

                                    bool isConnected = (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                    var deviceType = DeviceClassifier.ClassifyByAppearance(bleDevice.Appearance.Category, bleDevice.Appearance.SubCategory);
                                    if (deviceType == BluetoothDeviceType.Unknown)
                                    {
                                        deviceType = DeviceClassifier.ClassifyByName(name);
                                    }

                                    int existingIndex = list.FindIndex(d => d.Address == address);
                                    if (existingIndex >= 0)
                                    {
                                        var existing = list[existingIndex];
                                        bool mergedConnected = existing.IsConnected || isConnected;
                                        string mergedName = (existing.Name == "Unknown Device" && name != "Unknown BLE Device") ? name : existing.Name;
                                        var mergedType = (existing.DeviceType == BluetoothDeviceType.Unknown && deviceType != BluetoothDeviceType.Unknown) ? deviceType : existing.DeviceType;
                                        int? mergedBattery = existing.BatteryPercent;

                                        if (mergedConnected && mergedBattery == null && _batteryService != null)
                                        {
                                            string addrHex = address.ToString("X12");
                                            mergedBattery = _batteryService.QueryBatteryViaPnp(addrHex);

                                            var cacheEntry = _batteryService.BatteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });
                                            if (mergedBattery != null)
                                            {
                                                cacheEntry.RecordSuccess(mergedBattery.Value, mergedName);
                                            }
                                            else
                                            {
                                                mergedBattery = cacheEntry.GetEffectiveBattery();
                                                if (mergedBattery == null || cacheEntry.ShouldRetry())
                                                {
                                                    _batteryService.ScheduleDeferredBatteryQuery(address, mergedName, isClassicOnly: false, immediate: true);
                                                }
                                            }
                                        }

                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Paired BLE device: '{mergedName}' ({address:X12}) merged with Classic entry. Connected={mergedConnected}, Type={mergedType}, Battery={mergedBattery?.ToString() ?? "N/A"}");
                                        list[existingIndex] = new DeviceInfo(mergedName, mergedBattery, mergedType, mergedConnected, address);
                                    }
                                    else
                                    {
                                        int? battery = null;
                                        if (!isConnected)
                                        {
                                            _batteryService?.HandleDeviceDisconnected(address);
                                        }
                                        else if (_batteryService != null)
                                        {
                                            string addrHex = address.ToString("X12");
                                            battery = _batteryService.QueryBatteryViaPnp(addrHex);

                                            var cacheEntry = _batteryService.BatteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });
                                            if (battery != null)
                                            {
                                                cacheEntry.RecordSuccess(battery.Value, name);
                                            }
                                            else
                                            {
                                                battery = cacheEntry.GetEffectiveBattery();
                                                if (battery == null || cacheEntry.ShouldRetry())
                                                {
                                                    _batteryService.ScheduleDeferredBatteryQuery(address, name, isClassicOnly: false, immediate: true);
                                                }
                                            }
                                        }

                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Paired BLE device (Pure): '{name}' ({address:X12}), Connected={isConnected}, Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}");
                                        list.Add(new DeviceInfo(name, battery, deviceType, isConnected, address));
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetPairedDevicesAsync: BLE entry processing exception for '{devInfo.Name}': {ex.Message}");
                                }
                                finally
                                {
                                    if (!isCachedHandle && (_connectionService == null || !_connectionService.ConnectedBleDevices.ContainsKey(address)))
                                    {
                                        try { bleDevice?.Dispose(); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetPairedDevicesAsync BLE enumeration exception: {ex.Message}");
                    }

                    foreach (var addr in disconnectedClassicAddresses)
                    {
                        var dev = list.FirstOrDefault(d => d.Address == addr);
                        if (dev != null && !dev.IsConnected)
                        {
                            _batteryService?.HandleDeviceDisconnected(addr);
                        }
                    }

                    foreach (var d in list)
                    {
                        _cachedDevices[d.Address] = (d.Name, d.DeviceType, d.IsConnected);
                    }

                    return list.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();

                }, ct).ConfigureAwait(false);

                return results;
            }
            catch (OperationCanceledException)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "GetPairedDevicesAsync cancelled");
                return new List<DeviceInfo>();
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetPairedDevicesAsync outer exception: {ex.Message}");
                return new List<DeviceInfo>();
            }
        }

        public async Task<bool?> IsDeviceConnectedAsync(ulong address, CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"IsDeviceConnectedAsync called for {address:X12}");
            try
            {
                var radio = _radioService.BluetoothRadio;
                bool? adapterState = (radio != null && radio.State != RadioState.Unknown)
                    ? (radio.State == RadioState.On)
                    : await _radioService.GetAdapterStateAsync(ct).ConfigureAwait(false);

                if (adapterState != true) return false;

                if (_connectionService != null && _connectionService.ConnectedBleDevices.TryGetValue(address, out var bleDev))
                {
                    if (bleDev != null && bleDev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        return true;
                    }
                }

                return await Task.Run(async () =>
                {
                    try
                    {
                        bool isClassicPaired = false;
                        using (var client = new BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                            if (device != null)
                            {
                                isClassicPaired = true;
                                device.Refresh();
                                if (device.Connected) return true;
                            }
                        }

                        BluetoothLEDevice ble = null;
                        bool isBlePaired = false;
                        try
                        {
                            using (var lookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
                            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                            {
                                ble = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                    .AsTask(linkedCts.Token).ConfigureAwait(false);
                            }

                            if (ble != null)
                            {
                                isBlePaired = (ble.DeviceInformation?.Pairing?.IsPaired == true);
                                if (ble.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                {
                                    return true;
                                }
                            }
                        }
                        finally
                        {
                            if (ble != null && (_connectionService == null || !_connectionService.ConnectedBleDevices.TryGetValue(address, out var tracked) || !ReferenceEquals(tracked, ble)))
                            {
                                try { ble.Dispose(); } catch { }
                            }
                        }

                        return (isClassicPaired || isBlePaired) ? false : (bool?)null;
                    }
                    catch { return null; }
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"IsDeviceConnectedAsync exception: {ex.Message}");
                return null;
            }
        }

        public bool HasConnectedPeripherals(ulong currentTargetAddress, out string peripheralName, out BluetoothDeviceType peripheralType)
        {
            peripheralName = null;
            peripheralType = BluetoothDeviceType.Unknown;

            if (BluetoothManager.ConnectedPeripheralsProbe != null)
            {
                var probeResult = BluetoothManager.ConnectedPeripheralsProbe(currentTargetAddress);
                if (probeResult.hasPeripherals)
                {
                    peripheralName = probeResult.name;
                    peripheralType = probeResult.type;
                    return true;
                }
            }

            try
            {
                if (_connectionService != null)
                {
                    foreach (var kvp in _connectionService.ConnectedBleDevices)
                    {
                        if (kvp.Key == currentTargetAddress) continue;
                        var dev = kvp.Value;
                        if (dev != null && dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        {
                            var type = DeviceClassifier.ClassifyByAppearance(dev.Appearance.Category, dev.Appearance.SubCategory);
                            if (type == BluetoothDeviceType.Unknown)
                            {
                                type = DeviceClassifier.ClassifyByName(dev.Name);
                            }
                            if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                            {
                                peripheralName = dev.Name;
                                peripheralType = type;
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"HasConnectedPeripherals: Active BLE peripheral found: '{dev.Name}' ({type})");
                                return true;
                            }
                        }
                    }
                }

                foreach (var kvp in _cachedDevices)
                {
                    if (kvp.Key == currentTargetAddress) continue;
                    if (kvp.Value.IsConnected && (kvp.Value.Type == BluetoothDeviceType.Mouse || kvp.Value.Type == BluetoothDeviceType.Keyboard || kvp.Value.Type == BluetoothDeviceType.Controller))
                    {
                        peripheralName = kvp.Value.Name;
                        peripheralType = kvp.Value.Type;
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"HasConnectedPeripherals: Active cached peripheral found: '{peripheralName}' ({peripheralType})");
                        return true;
                    }
                }

                using (var client = new BluetoothClient())
                {
                    var paired = client.PairedDevices;
                    if (paired != null)
                    {
                        foreach (var d in paired)
                        {
                            if (d.DeviceAddress.ToUInt64() == currentTargetAddress) continue;
                            if (d.Connected)
                            {
                                int cod = (int)d.ClassOfDevice.Value;
                                int major = (cod >> 8) & 0x1F;
                                int minor = (cod >> 2) & 0x3F;
                                var type = DeviceClassifier.ClassifyByCod(major, minor);
                                if (type == BluetoothDeviceType.Unknown)
                                {
                                    type = DeviceClassifier.ClassifyByName(d.DeviceName);
                                }

                                if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                                {
                                    peripheralName = d.DeviceName;
                                    peripheralType = type;
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"HasConnectedPeripherals: Active Classic peripheral found: '{d.DeviceName}' ({type})");
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"HasConnectedPeripherals check exception: {ex.Message}");
            }
            return false;
        }

        public async Task<(bool hasPeripherals, string name, BluetoothDeviceType type)> CheckConnectedPeripheralsAsync(ulong currentTargetAddress, CancellationToken ct = default)
        {
            if (BluetoothManager.ConnectedPeripheralsProbe != null)
            {
                var probeResult = BluetoothManager.ConnectedPeripheralsProbe(currentTargetAddress);
                if (probeResult.hasPeripherals)
                {
                    return probeResult;
                }
            }

            try
            {
                // 1. ConnectionService connected BLE devices
                if (_connectionService != null)
                {
                    foreach (var kvp in _connectionService.ConnectedBleDevices)
                    {
                        if (kvp.Key == currentTargetAddress) continue;
                        var dev = kvp.Value;
                        if (dev != null && dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        {
                            var type = DeviceClassifier.ClassifyByAppearance(dev.Appearance.Category, dev.Appearance.SubCategory);
                            if (type == BluetoothDeviceType.Unknown)
                            {
                                type = DeviceClassifier.ClassifyByName(dev.Name);
                            }
                            if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"CheckConnectedPeripheralsAsync: Active BLE peripheral found: '{dev.Name}' ({type})");
                                return (true, dev.Name, type);
                            }
                        }
                    }
                }

                // 2. Cached devices
                foreach (var kvp in _cachedDevices)
                {
                    if (kvp.Key == currentTargetAddress) continue;
                    if (kvp.Value.IsConnected && (kvp.Value.Type == BluetoothDeviceType.Mouse || kvp.Value.Type == BluetoothDeviceType.Keyboard || kvp.Value.Type == BluetoothDeviceType.Controller))
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"CheckConnectedPeripheralsAsync: Active cached peripheral found: '{kvp.Value.Name}' ({kvp.Value.Type})");
                        return (true, kvp.Value.Name, kvp.Value.Type);
                    }
                }

                // 3. WinRT DeviceInformation paired BLE devices (includes Windows boot-connected BLE HID devices)
                try
                {
                    string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                    var bleCollection = await DeviceInformation.FindAllAsync(
                        bleSelector,
                        new string[] { "System.Devices.Aep.IsConnected" }
                    ).AsTask(ct).ConfigureAwait(false);

                    if (bleCollection != null)
                    {
                        foreach (var devInfo in bleCollection)
                        {
                            ct.ThrowIfCancellationRequested();
                            ulong parsedAddr = BatteryPolicy.TryExtractAddressFromBleDeviceId(devInfo.Id);
                            if (parsedAddr != 0 && parsedAddr == currentTargetAddress) continue;

                            bool isConnectedProp = false;
                            if (devInfo.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connVal) && connVal is bool bConn)
                            {
                                isConnectedProp = bConn;
                            }

                            if (isConnectedProp)
                            {
                                var type = DeviceClassifier.ClassifyByName(devInfo.Name);
                                if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"CheckConnectedPeripheralsAsync: Connected paired BLE HID found via AEP property: '{devInfo.Name}' ({type})");
                                    return (true, devInfo.Name, type);
                                }

                                BluetoothLEDevice bleDev = null;
                                try
                                {
                                    using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                                    {
                                        bleDev = await BluetoothLEDevice.FromIdAsync(devInfo.Id).AsTask(linkedCts.Token).ConfigureAwait(false);
                                    }
                                    if (bleDev != null)
                                    {
                                        var appType = DeviceClassifier.ClassifyByAppearance(bleDev.Appearance.Category, bleDev.Appearance.SubCategory);
                                        if (appType == BluetoothDeviceType.Mouse || appType == BluetoothDeviceType.Keyboard || appType == BluetoothDeviceType.Controller)
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"CheckConnectedPeripheralsAsync: Connected paired BLE HID found via appearance: '{bleDev.Name}' ({appType})");
                                            return (true, bleDev.Name, appType);
                                        }
                                    }
                                }
                                catch { }
                                finally
                                {
                                    try { bleDev?.Dispose(); } catch { }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"CheckConnectedPeripheralsAsync: BLE AEP check note: {ex.Message}");
                }

                // 4. Classic Bluetooth paired devices on background thread
                var classicResult = await Task.Run(() =>
                {
                    try
                    {
                        using (var client = new BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            if (paired != null)
                            {
                                foreach (var d in paired)
                                {
                                    if (d.DeviceAddress.ToUInt64() == currentTargetAddress) continue;
                                    if (d.Connected)
                                    {
                                        int cod = (int)d.ClassOfDevice.Value;
                                        int major = (cod >> 8) & 0x1F;
                                        int minor = (cod >> 2) & 0x3F;
                                        var type = DeviceClassifier.ClassifyByCod(major, minor);
                                        if (type == BluetoothDeviceType.Unknown)
                                        {
                                            type = DeviceClassifier.ClassifyByName(d.DeviceName);
                                        }

                                        if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"CheckConnectedPeripheralsAsync: Active Classic peripheral found: '{d.DeviceName}' ({type})");
                                            return (true, d.DeviceName, type);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"CheckConnectedPeripheralsAsync Classic check exception: {ex.Message}");
                    }
                    return (false, (string)null, BluetoothDeviceType.Unknown);
                }, ct).ConfigureAwait(false);

                if (classicResult.Item1)
                {
                    return classicResult;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"CheckConnectedPeripheralsAsync exception: {ex.Message}");
            }

            return (false, null, BluetoothDeviceType.Unknown);
        }

        public void StartDeviceWatcher()
        {
            bool classicRunning = _classicWatcher != null &&
                (_classicWatcher.Status == DeviceWatcherStatus.Started ||
                 _classicWatcher.Status == DeviceWatcherStatus.EnumerationCompleted);
            bool bleRunning = _bleWatcher != null &&
                (_bleWatcher.Status == DeviceWatcherStatus.Started ||
                 _bleWatcher.Status == DeviceWatcherStatus.EnumerationCompleted);

            if (classicRunning && bleRunning)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "DeviceWatcher: Both Classic and BLE watchers already running, ignoring start request");
                return;
            }

            StopDeviceWatcher();
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "DeviceWatcher: Starting Classic and BLE watchers...");

            _classicEnumerationComplete = false;
            _bleEnumerationComplete = false;

            try
            {
                string classicSelector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                _classicWatcher = DeviceInformation.CreateWatcher(
                    classicSelector,
                    new string[] { "System.Devices.Aep.IsConnected" },
                    DeviceInformationKind.AssociationEndpoint);

                _classicWatcher.Added += ClassicWatcher_Added;
                _classicWatcher.Removed += ClassicWatcher_Removed;
                _classicWatcher.Updated += ClassicWatcher_Updated;
                _classicWatcher.EnumerationCompleted += ClassicWatcher_EnumerationCompleted;

                _classicWatcher.Start();
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "DeviceWatcher: Classic watcher started");
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"DeviceWatcher: Failed to start Classic watcher: {ex.Message}");
            }

            try
            {
                string bleSelector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                _bleWatcher = DeviceInformation.CreateWatcher(
                    bleSelector,
                    new string[] { "System.Devices.Aep.IsConnected" },
                    DeviceInformationKind.AssociationEndpoint);

                _bleWatcher.Added += BleWatcher_Added;
                _bleWatcher.Removed += BleWatcher_Removed;
                _bleWatcher.Updated += BleWatcher_Updated;
                _bleWatcher.EnumerationCompleted += BleWatcher_EnumerationCompleted;

                _bleWatcher.Start();
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "DeviceWatcher: BLE watcher started");
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"DeviceWatcher: Failed to start BLE watcher: {ex.Message}");
            }
        }

        public void StopDeviceWatcher()
        {
            if (_classicWatcher == null && _bleWatcher == null) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "DeviceWatcher: Stopping watchers...");

            if (_classicWatcher != null)
            {
                try
                {
                    _classicWatcher.Added -= ClassicWatcher_Added;
                    _classicWatcher.Removed -= ClassicWatcher_Removed;
                    _classicWatcher.Updated -= ClassicWatcher_Updated;
                    _classicWatcher.EnumerationCompleted -= ClassicWatcher_EnumerationCompleted;

                    if (_classicWatcher.Status == DeviceWatcherStatus.Started ||
                        _classicWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    {
                        _classicWatcher.Stop();
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"DeviceWatcher: Stop Classic watcher exception: {ex.Message}");
                }
                finally
                {
                    _classicWatcher = null;
                    _classicEnumerationComplete = false;
                }
            }

            if (_bleWatcher != null)
            {
                try
                {
                    _bleWatcher.Added -= BleWatcher_Added;
                    _bleWatcher.Removed -= BleWatcher_Removed;
                    _bleWatcher.Updated -= BleWatcher_Updated;
                    _bleWatcher.EnumerationCompleted -= BleWatcher_EnumerationCompleted;

                    if (_bleWatcher.Status == DeviceWatcherStatus.Started ||
                        _bleWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    {
                        _bleWatcher.Stop();
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"DeviceWatcher: Stop BLE watcher exception: {ex.Message}");
                }
                finally
                {
                    _bleWatcher = null;
                    _bleEnumerationComplete = false;
                }
            }

            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "DeviceWatcher: Stopped all watchers");
        }

        private void ClassicWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            _classicEnumerationComplete = true;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "DeviceWatcher (Classic): Initial enumeration complete — now live-watching for changes");
        }

        private void ClassicWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            if (!_classicEnumerationComplete) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (Classic): Device connected/paired: '{args.Name}'");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void ClassicWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_classicEnumerationComplete) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (Classic): Device disconnected/unpaired: {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void ClassicWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_classicEnumerationComplete) return;
            if (!args.Properties.ContainsKey("System.Devices.Aep.IsConnected")) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (Classic): IsConnected changed for {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            _bleEnumerationComplete = true;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "DeviceWatcher (BLE): Initial enumeration complete — now live-watching for changes");
        }

        private void BleWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            if (!_bleEnumerationComplete) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (BLE): Device connected/paired: '{args.Name}'");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_bleEnumerationComplete) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (BLE): Device disconnected/unpaired: {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_bleEnumerationComplete) return;
            if (!args.Properties.ContainsKey("System.Devices.Aep.IsConnected")) return;
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"DeviceWatcher (BLE): IsConnected changed for {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopDeviceWatcher();
            lock (_watcherDebounceLock)
            {
                try { _watcherDebounceTimer?.Dispose(); } catch { }
                _watcherDebounceTimer = null;
            }
        }
    }
}
