using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace bluetooth_turnon.Services
{
    public sealed class BatteryService : IBatteryService
    {
        private static readonly ConcurrentDictionary<string, string> _pnpInstanceIdCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<ulong, BatteryCacheEntry> _batteryCache =
            new ConcurrentDictionary<ulong, BatteryCacheEntry>();
        private readonly HashSet<ulong> _inFlightBatteryQueries = new HashSet<ulong>();
        private readonly object _batteryRetryLock = new object();

        private Func<ulong, CancellationToken, Task<bool?>> _isDeviceConnectedChecker;
        private Func<ulong, BluetoothLEDevice> _getCachedBleDevice;

        public event Action<ulong, int?> BatteryUpdated;

        public ConcurrentDictionary<ulong, BatteryCacheEntry> BatteryCache => _batteryCache;
        public HashSet<ulong> InFlightBatteryQueries => _inFlightBatteryQueries;

        public void SetDeviceConnectionChecker(Func<ulong, CancellationToken, Task<bool?>> checker)
        {
            _isDeviceConnectedChecker = checker;
        }

        public void SetCachedBleDeviceGetter(Func<ulong, BluetoothLEDevice> getter)
        {
            _getCachedBleDevice = getter;
        }

        public int? GetEffectiveBattery(ulong address)
        {
            return _batteryCache.TryGetValue(address, out var entry) ? entry.GetEffectiveBattery() : null;
        }

        public void HandleDeviceDisconnected(ulong address)
        {
            if (address == 0) return;
            lock (_batteryRetryLock)
            {
                _inFlightBatteryQueries.Remove(address);
            }
            if (_batteryCache.TryGetValue(address, out var entry))
            {
                entry.FailureCount = 0;
            }
        }

        public void HandleAdapterTurnedOff()
        {
            lock (_batteryRetryLock)
            {
                _inFlightBatteryQueries.Clear();
            }
            foreach (var kvp in _batteryCache)
            {
                kvp.Value.FailureCount = 0;
            }
        }

        public void ClearAll()
        {
            _batteryCache.Clear();
            lock (_batteryRetryLock)
            {
                _inFlightBatteryQueries.Clear();
            }
        }

        private static int? ReadPnpBatteryDirect(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            IntPtr devInfoSet = IntPtr.Zero;
            try
            {
                devInfoSet = NativeMethods.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                if (devInfoSet == (IntPtr)(-1) || devInfoSet == IntPtr.Zero) return null;

                var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                devInfoData.cbSize = (uint)Marshal.SizeOf(devInfoData);
                if (!NativeMethods.SetupDiOpenDeviceInfo(devInfoSet, instanceId, IntPtr.Zero, 0, ref devInfoData))
                    return null;

                var batteryPropertyKey = new NativeMethods.DEVPROPKEY
                {
                    fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
                    pid = 2
                };

                uint propertyType;
                byte[] buffer = new byte[4];
                uint bufferSize = (uint)buffer.Length;
                if (NativeMethods.SetupDiGetDevicePropertyW(
                        devInfoSet, ref devInfoData, ref batteryPropertyKey,
                        out propertyType, buffer, bufferSize, out bufferSize, 0))
                {
                    if (propertyType == NativeMethods.DEVPROP_TYPE_BYTE && bufferSize >= 1)
                    {
                        byte level = buffer[0];
                        if (level <= 100)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery PnP (cached instance): {level}% from '{instanceId}'");
                            return (int)level;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (devInfoSet != IntPtr.Zero && devInfoSet != (IntPtr)(-1))
                {
                    NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
                }
            }
            return null;
        }

        public int? QueryBatteryViaPnp(string bluetoothAddressHex)
        {
            try
            {
                string addrUpper = bluetoothAddressHex
                    .Replace(":", "").Replace("-", "").ToUpperInvariant();

                if (addrUpper.Length > 0 && addrUpper.Length < 12)
                {
                    addrUpper = addrUpper.PadLeft(12, '0');
                }

                if (_pnpInstanceIdCache.TryGetValue(addrUpper, out string cachedInstanceId) && !string.IsNullOrEmpty(cachedInstanceId))
                {
                    int? directBattery = ReadPnpBatteryDirect(cachedInstanceId);
                    if (directBattery != null)
                    {
                        return directBattery;
                    }
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery PnP: searching for address '{addrUpper}'");

                var batteryPropertyKey = new NativeMethods.DEVPROPKEY
                {
                    fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
                    pid = 2
                };

                string[] enumerators = new[] { "BTHENUM", "BTHHFENUM", "BTHLEDEVICE" };
                foreach (var enumerator in enumerators)
                {
                    IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevs(
                        IntPtr.Zero, enumerator, IntPtr.Zero,
                        NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);

                    if (devInfoSet == (IntPtr)(-1)) continue;

                    try
                    {
                        var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                        devInfoData.cbSize = (uint)Marshal.SizeOf(devInfoData);

                        for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                        {
                            char[] instanceIdBuf = new char[512];
                            uint requiredSize;
                            if (!NativeMethods.SetupDiGetDeviceInstanceId(
                                    devInfoSet, ref devInfoData, instanceIdBuf, (uint)instanceIdBuf.Length, out requiredSize))
                            {
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

                            string instanceId = new string(instanceIdBuf, 0, (int)requiredSize - 1);
                            if (instanceId.IndexOf(addrUpper, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            _pnpInstanceIdCache[addrUpper] = instanceId;

                            uint propertyType;
                            byte[] buffer = new byte[4];
                            uint bufferSize = (uint)buffer.Length;
                            bool success = NativeMethods.SetupDiGetDevicePropertyW(
                                devInfoSet, ref devInfoData,
                                ref batteryPropertyKey,
                                out propertyType,
                                buffer, bufferSize, out bufferSize, 0);

                            if (success && propertyType == NativeMethods.DEVPROP_TYPE_BYTE && bufferSize >= 1)
                            {
                                byte level = buffer[0];
                                if (level <= 100)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery PnP: {level}% from '{instanceId}' ({enumerator})");
                                    return (int)level;
                                }
                            }
                        }
                    }
                    finally
                    {
                        NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
                    }
                }

                Guid bluetoothClassGuid = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
                IntPtr fallbackDevInfoSet = NativeMethods.SetupDiGetClassDevs(
                    ref bluetoothClassGuid, null, IntPtr.Zero,
                    NativeMethods.DIGCF_PRESENT);

                if (fallbackDevInfoSet != (IntPtr)(-1))
                {
                    try
                    {
                        var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                        devInfoData.cbSize = (uint)Marshal.SizeOf(devInfoData);

                        for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(fallbackDevInfoSet, i, ref devInfoData); i++)
                        {
                            char[] instanceIdBuf = new char[512];
                            uint requiredSize;
                            if (!NativeMethods.SetupDiGetDeviceInstanceId(
                                    fallbackDevInfoSet, ref devInfoData, instanceIdBuf, (uint)instanceIdBuf.Length, out requiredSize))
                            {
                                if (requiredSize > instanceIdBuf.Length)
                                {
                                    instanceIdBuf = new char[requiredSize];
                                    if (!NativeMethods.SetupDiGetDeviceInstanceId(
                                            fallbackDevInfoSet, ref devInfoData, instanceIdBuf, (uint)instanceIdBuf.Length, out requiredSize))
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

                            string instanceId = new string(instanceIdBuf, 0, (int)requiredSize - 1);
                            if (instanceId.IndexOf(addrUpper, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            _pnpInstanceIdCache[addrUpper] = instanceId;

                            uint propertyType;
                            byte[] buffer = new byte[4];
                            uint bufferSize = (uint)buffer.Length;
                            bool success = NativeMethods.SetupDiGetDevicePropertyW(
                                fallbackDevInfoSet, ref devInfoData,
                                ref batteryPropertyKey,
                                out propertyType,
                                buffer, bufferSize, out bufferSize, 0);

                            if (success && propertyType == NativeMethods.DEVPROP_TYPE_BYTE && bufferSize >= 1)
                            {
                                byte level = buffer[0];
                                if (level <= 100)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery PnP: {level}% from fallback GUID '{instanceId}'");
                                    return (int)level;
                                }
                            }
                        }
                    }
                    finally
                    {
                        NativeMethods.SetupDiDestroyDeviceInfoList(fallbackDevInfoSet);
                    }
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery PnP: no battery found for address '{addrUpper}'");
                return null;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"    QueryBatteryViaPnp exception for '{bluetoothAddressHex}': {ex.Message}");
                return null;
            }
        }

        public void ScheduleDeferredBatteryQuery(ulong address, string deviceName, bool isClassicOnly = false, bool immediate = false)
        {
            if (address == 0) return;

            if (_batteryCache.TryGetValue(address, out var existingEntry))
            {
                if (existingEntry.Outcome == BatteryQueryOutcome.Unsupported && DateTime.UtcNow < existingEntry.NextAllowedRetryUtc)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Battery GATT: '{deviceName}' ({address:X12}) confirmed Unsupported. Leaving alone.");
                    return;
                }
                if (existingEntry.Outcome == BatteryQueryOutcome.TransientFailure && DateTime.UtcNow < existingEntry.NextAllowedRetryUtc)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Battery GATT: '{deviceName}' ({address:X12}) in transient backoff until {existingEntry.NextAllowedRetryUtc:HH:mm:ss} UTC (failure #{existingEntry.FailureCount}). Skipping.");
                    return;
                }
            }

            lock (_batteryRetryLock)
            {
                if (!_inFlightBatteryQueries.Add(address))
                {
                    return;
                }
            }

            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Scheduling {(immediate ? "immediate" : "deferred (3s)")} battery query for '{deviceName}' ({address:X12})...");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!immediate)
                    {
                        await Task.Delay(3000).ConfigureAwait(false);
                    }

                    lock (_batteryRetryLock)
                    {
                        if (!_inFlightBatteryQueries.Contains(address))
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query aborted: '{deviceName}' ({address:X12}) was cancelled or disconnected.");
                            return;
                        }
                    }

                    if (_isDeviceConnectedChecker != null)
                    {
                        bool? isConnected = await _isDeviceConnectedChecker(address, CancellationToken.None).ConfigureAwait(false);
                        if (isConnected != true)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query aborted: '{deviceName}' ({address:X12}) is not connected.");
                            HandleDeviceDisconnected(address);
                            return;
                        }
                    }

                    string addrHex = address.ToString("X12");
                    int? retriedBattery = QueryBatteryViaPnp(addrHex);

                    var cacheEntry = _batteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });

                    if (retriedBattery != null)
                    {
                        cacheEntry.RecordSuccess(retriedBattery.Value, deviceName);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Battery query SUCCESS via PnP for '{deviceName}': {retriedBattery}%. Raising BatteryUpdated.");
                        BatteryUpdated?.Invoke(address, retriedBattery.Value);
                        return;
                    }

                    if (isClassicOnly)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Battery: '{deviceName}' ({address:X12}) is Classic-only and PnP returned null. Skipping GATT and caching as Unsupported.");
                        cacheEntry.RecordUnsupported(deviceName, isClassicOnly: true);
                        return;
                    }

                    if (_isDeviceConnectedChecker != null)
                    {
                        bool? isConnected = await _isDeviceConnectedChecker(address, CancellationToken.None).ConfigureAwait(false);
                        if (isConnected != true)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query aborted before GATT: '{deviceName}' ({address:X12}) is not connected.");
                            HandleDeviceDisconnected(address);
                            return;
                        }
                    }

                    var gattResult = await QueryBatteryViaGattAsync(address, deviceName, CancellationToken.None).ConfigureAwait(false);

                    lock (_batteryRetryLock)
                    {
                        if (!_inFlightBatteryQueries.Contains(address))
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query result discarded: '{deviceName}' ({address:X12}) was disconnected during query.");
                            return;
                        }
                    }

                    if (gattResult.Outcome == BatteryQueryOutcome.Success && gattResult.BatteryLevel.HasValue)
                    {
                        cacheEntry.RecordSuccess(gattResult.BatteryLevel.Value, deviceName);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Deferred battery query SUCCESS via GATT for '{deviceName}': {gattResult.BatteryLevel.Value}%. Raising BatteryUpdated.");
                        BatteryUpdated?.Invoke(address, gattResult.BatteryLevel.Value);
                    }
                    else if (gattResult.Outcome == BatteryQueryOutcome.Unsupported)
                    {
                        cacheEntry.RecordUnsupported(deviceName, isClassicOnly: false);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Deferred battery query: '{deviceName}' confirmed Unsupported (cached for {BatteryPolicy.BatteryUnsupportedCacheDuration.TotalHours}h). Leaving alone.");
                    }
                    else
                    {
                        cacheEntry.RecordTransientFailure(deviceName);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query: Transient failure #{cacheEntry.FailureCount} for '{deviceName}'. Preserving last good value ({cacheEntry.GetEffectiveBattery()?.ToString() ?? "N/A"}). Next retry at {cacheEntry.NextAllowedRetryUtc:HH:mm:ss} UTC.");

                        if (cacheEntry.FailureCount <= BatteryPolicy.MaxBatteryRetries)
                        {
                            ScheduleBoundedRetryAfterBackoff(address, deviceName, cacheEntry.FailureCount);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Deferred battery query cancelled for '{deviceName}' ({address:X12}).");
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  Deferred battery query exception for '{deviceName}': {ex.Message}");
                }
                finally
                {
                    lock (_batteryRetryLock)
                    {
                        _inFlightBatteryQueries.Remove(address);
                    }
                }
            });
        }

        private void ScheduleBoundedRetryAfterBackoff(ulong address, string deviceName, int failureCount)
        {
            var backoff = BatteryPolicy.GetTransientBackoff(failureCount);
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Battery GATT: Scheduling bounded retry #{failureCount + 1} for '{deviceName}' in {backoff.TotalSeconds}s...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(backoff).ConfigureAwait(false);
                    if (_isDeviceConnectedChecker != null)
                    {
                        bool? stillConnected = await _isDeviceConnectedChecker(address, CancellationToken.None).ConfigureAwait(false);
                        if (stillConnected == true)
                        {
                            ScheduleDeferredBatteryQuery(address, deviceName, isClassicOnly: false, immediate: true);
                        }
                    }
                }
                catch { }
            });
        }

        public async Task<BatteryQueryResult> QueryBatteryViaGattAsync(ulong bluetoothAddress, string deviceName, CancellationToken ct)
        {
            var totalSw = Stopwatch.StartNew();
            BluetoothLEDevice bleDevice = null;
            bool isCachedHandle = false;

            try
            {
                if (bluetoothAddress != 0 && _getCachedBleDevice != null)
                {
                    var cachedDev = _getCachedBleDevice(bluetoothAddress);
                    if (cachedDev != null)
                    {
                        bleDevice = cachedDev;
                        isCachedHandle = true;
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT: Reusing tracked handle for {bluetoothAddress:X12} ('{bleDevice.Name}')");
                    }
                }

                if (bleDevice == null && bluetoothAddress != 0)
                {
                    try
                    {
                        using (var lookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
                        using (var linkedLookup = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                        {
                            bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                                .AsTask(linkedLookup.Token).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }

                if (bleDevice == null && !string.IsNullOrWhiteSpace(deviceName))
                {
                    try
                    {
                        using (var findCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
                        using (var linkedFind = CancellationTokenSource.CreateLinkedTokenSource(ct, findCts.Token))
                        {
                            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                            var pairedBleDevices = await DeviceInformation.FindAllAsync(selector).AsTask(linkedFind.Token).ConfigureAwait(false);
                            if (pairedBleDevices != null)
                            {
                                var match = pairedBleDevices.FirstOrDefault(d =>
                                    !string.IsNullOrEmpty(d.Name) &&
                                    string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

                                if (match == null && deviceName.Length >= 4)
                                {
                                    match = pairedBleDevices.FirstOrDefault(d =>
                                        !string.IsNullOrEmpty(d.Name) &&
                                        d.Name.Length >= 4 &&
                                        (d.Name.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         deviceName.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0));
                                }

                                if (match != null)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT: Matched paired BLE device '{match.Name}' (Id={match.Id}) for '{deviceName}'");
                                    bleDevice = await BluetoothLEDevice.FromIdAsync(match.Id).AsTask(linkedFind.Token).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT: DeviceInformation.FindAllAsync lookup note: {ex.Message}");
                    }
                }

                if (bleDevice == null)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT: BLE device not found for '{deviceName}' (address {bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
                    return new BatteryQueryResult
                    {
                        Outcome = BatteryQueryOutcome.TransientFailure,
                        ElapsedMs = totalSw.ElapsedMilliseconds,
                        Details = "BLE device not found"
                    };
                }

                // Phase 1: Fast Cached Read
                bool fastReadSucceeded = false;
                int fastBatteryLevel = -1;

                try
                {
                    using (var fastCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(BatteryPolicy.BatteryCachedQueryBudgetMs)))
                    using (var linkedFastCts = CancellationTokenSource.CreateLinkedTokenSource(ct, fastCts.Token))
                    {
                        var fastToken = linkedFastCts.Token;
                        var swCachedServices = Stopwatch.StartNew();
                        var cachedServicesResult = await bleDevice
                            .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
                            .AsTask(fastToken).ConfigureAwait(false);
                        long cachedServicesMs = swCachedServices.ElapsedMilliseconds;

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Fast Cached]: GetGattServices took {cachedServicesMs}ms, status={cachedServicesResult?.Status}, count={cachedServicesResult?.Services?.Count ?? 0}");

                        if (cachedServicesResult != null &&
                            cachedServicesResult.Status == GattCommunicationStatus.Success &&
                            cachedServicesResult.Services != null &&
                            cachedServicesResult.Services.Count > 0)
                        {
                            try
                            {
                                var service = cachedServicesResult.Services[0];
                                var swCachedChars = Stopwatch.StartNew();
                                var cachedCharsResult = await service
                                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached)
                                    .AsTask(fastToken).ConfigureAwait(false);
                                long cachedCharsMs = swCachedChars.ElapsedMilliseconds;

                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Fast Cached]: GetCharacteristics took {cachedCharsMs}ms, status={cachedCharsResult?.Status}, count={cachedCharsResult?.Characteristics?.Count ?? 0}");

                                if (cachedCharsResult != null &&
                                    cachedCharsResult.Status == GattCommunicationStatus.Success &&
                                    cachedCharsResult.Characteristics != null &&
                                    cachedCharsResult.Characteristics.Count > 0)
                                {
                                    var charac = cachedCharsResult.Characteristics[0];
                                    var swCachedRead = Stopwatch.StartNew();
                                    var readResult = await charac.ReadValueAsync(BluetoothCacheMode.Uncached)
                                        .AsTask(fastToken).ConfigureAwait(false);
                                    long cachedReadMs = swCachedRead.ElapsedMilliseconds;

                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Fast Cached]: ReadValueAsync took {cachedReadMs}ms, status={readResult?.Status}");

                                    if (readResult != null && readResult.Status == GattCommunicationStatus.Success && readResult.Value != null)
                                    {
                                        using (var reader = DataReader.FromBuffer(readResult.Value))
                                        {
                                            byte level = reader.ReadByte();
                                            fastBatteryLevel = (int)level;
                                            fastReadSucceeded = true;
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"    Battery GATT [Fast Cached SUCCESS]: {level}% for '{bleDevice.Name}' ({bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (cachedServicesResult?.Services != null)
                                {
                                    foreach (var svc in cachedServicesResult.Services) { try { svc.Dispose(); } catch { } }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Fast Cached]: Cache lookup did not complete: {ex.Message} ({totalSw.ElapsedMilliseconds}ms)");
                }

                if (fastReadSucceeded && fastBatteryLevel >= 0)
                {
                    return new BatteryQueryResult
                    {
                        Outcome = BatteryQueryOutcome.Success,
                        BatteryLevel = fastBatteryLevel,
                        ElapsedMs = totalSw.ElapsedMilliseconds,
                        Details = "Fast cached read"
                    };
                }

                // Phase 2: Slow Background Discovery
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT: Transitioning to Slow Background Discovery for '{bleDevice.Name}' ({BatteryPolicy.BatteryUncachedQueryBudgetMs}ms budget)...");

                using (var slowCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(BatteryPolicy.BatteryUncachedQueryBudgetMs)))
                using (var linkedSlowCts = CancellationTokenSource.CreateLinkedTokenSource(ct, slowCts.Token))
                {
                    var slowToken = linkedSlowCts.Token;
                    var swUncachedServices = Stopwatch.StartNew();
                    var uncachedServicesResult = await bleDevice
                        .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                        .AsTask(slowToken).ConfigureAwait(false);
                    long uncachedServicesMs = swUncachedServices.ElapsedMilliseconds;

                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Slow Discovery]: GetGattServices (Uncached) took {uncachedServicesMs}ms, status={uncachedServicesResult?.Status}, count={uncachedServicesResult?.Services?.Count ?? 0}");

                    if (uncachedServicesResult != null &&
                        uncachedServicesResult.Status == GattCommunicationStatus.Success &&
                        (uncachedServicesResult.Services == null || uncachedServicesResult.Services.Count == 0))
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"    Battery GATT [Unsupported]: Device '{bleDevice.Name}' confirmed absent Battery Service in {totalSw.ElapsedMilliseconds}ms.");
                        return new BatteryQueryResult
                        {
                            Outcome = BatteryQueryOutcome.Unsupported,
                            ElapsedMs = totalSw.ElapsedMilliseconds,
                            Details = "Battery Service absent"
                        };
                    }

                    if (uncachedServicesResult == null || uncachedServicesResult.Status != GattCommunicationStatus.Success)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Transient Failure]: Services discovery status={uncachedServicesResult?.Status} in {totalSw.ElapsedMilliseconds}ms.");
                        return new BatteryQueryResult
                        {
                            Outcome = BatteryQueryOutcome.TransientFailure,
                            ElapsedMs = totalSw.ElapsedMilliseconds,
                            Details = $"Services status: {uncachedServicesResult?.Status}"
                        };
                    }

                    try
                    {
                        var service = uncachedServicesResult.Services[0];
                        var swUncachedChars = Stopwatch.StartNew();
                        var uncachedCharsResult = await service
                            .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                            .AsTask(slowToken).ConfigureAwait(false);
                        long uncachedCharsMs = swUncachedChars.ElapsedMilliseconds;

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Slow Discovery]: GetCharacteristics (Uncached) took {uncachedCharsMs}ms, status={uncachedCharsResult?.Status}, count={uncachedCharsResult?.Characteristics?.Count ?? 0}");

                        if (uncachedCharsResult != null &&
                            uncachedCharsResult.Status == GattCommunicationStatus.Success &&
                            (uncachedCharsResult.Characteristics == null || uncachedCharsResult.Characteristics.Count == 0))
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"    Battery GATT [Unsupported]: BatteryLevel characteristic absent on '{bleDevice.Name}' in {totalSw.ElapsedMilliseconds}ms.");
                            return new BatteryQueryResult
                            {
                                Outcome = BatteryQueryOutcome.Unsupported,
                                ElapsedMs = totalSw.ElapsedMilliseconds,
                                Details = "BatteryLevel characteristic absent"
                            };
                        }

                        if (uncachedCharsResult == null || uncachedCharsResult.Status != GattCommunicationStatus.Success)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Transient Failure]: Characteristics query status={uncachedCharsResult?.Status} in {totalSw.ElapsedMilliseconds}ms.");
                            return new BatteryQueryResult
                            {
                                Outcome = BatteryQueryOutcome.TransientFailure,
                                ElapsedMs = totalSw.ElapsedMilliseconds,
                                Details = $"Chars status: {uncachedCharsResult?.Status}"
                            };
                        }

                        var charac = uncachedCharsResult.Characteristics[0];
                        var swUncachedRead = Stopwatch.StartNew();
                        var readResult = await charac.ReadValueAsync(BluetoothCacheMode.Uncached)
                            .AsTask(slowToken).ConfigureAwait(false);
                        long uncachedReadMs = swUncachedRead.ElapsedMilliseconds;

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Slow Discovery]: ReadValueAsync took {uncachedReadMs}ms, status={readResult?.Status}");

                        if (readResult != null && readResult.Status == GattCommunicationStatus.Success && readResult.Value != null)
                        {
                            using (var reader = DataReader.FromBuffer(readResult.Value))
                            {
                                byte level = reader.ReadByte();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"    Battery GATT [Slow Discovery SUCCESS]: {level}% for '{bleDevice.Name}' ({bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
                                return new BatteryQueryResult
                                {
                                    Outcome = BatteryQueryOutcome.Success,
                                    BatteryLevel = (int)level,
                                    ElapsedMs = totalSw.ElapsedMilliseconds,
                                    Details = "Slow uncached discovery"
                                };
                            }
                        }

                        return new BatteryQueryResult
                        {
                            Outcome = BatteryQueryOutcome.TransientFailure,
                            ElapsedMs = totalSw.ElapsedMilliseconds,
                            Details = $"Read status: {readResult?.Status}"
                        };
                    }
                    finally
                    {
                        if (uncachedServicesResult?.Services != null)
                        {
                            foreach (var svc in uncachedServicesResult.Services) { try { svc.Dispose(); } catch { } }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"    Battery GATT [Timeout/Cancelled]: Operation timed out or cancelled in {totalSw.ElapsedMilliseconds}ms for '{deviceName}' ({bluetoothAddress:X12})");
                return new BatteryQueryResult
                {
                    Outcome = BatteryQueryOutcome.TransientFailure,
                    ElapsedMs = totalSw.ElapsedMilliseconds,
                    Details = "Timed out or cancelled"
                };
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"    Battery GATT [Exception]: Error querying '{deviceName}' ({bluetoothAddress:X12}): {ex.Message} ({totalSw.ElapsedMilliseconds}ms)");
                return new BatteryQueryResult
                {
                    Outcome = BatteryQueryOutcome.TransientFailure,
                    ElapsedMs = totalSw.ElapsedMilliseconds,
                    Details = ex.Message
                };
            }
            finally
            {
                if (!isCachedHandle)
                {
                    try { bleDevice?.Dispose(); } catch { }
                }
            }
        }
    }
}
