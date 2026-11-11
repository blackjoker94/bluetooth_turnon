using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Sockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace bluetooth_turnon.Services
{
    public sealed class ConnectionService : IConnectionService
    {
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _deviceSemaphores = new ConcurrentDictionary<ulong, SemaphoreSlim>();
        private readonly ConcurrentDictionary<ulong, BluetoothLEDevice> _connectedBleDevices = new ConcurrentDictionary<ulong, BluetoothLEDevice>();
        private readonly ConcurrentDictionary<ulong, GattSession> _connectedBleSessions = new ConcurrentDictionary<ulong, GattSession>();

        private readonly object _deviceOpCtsLock = new object();
        private CancellationTokenSource _deviceOpCts = new CancellationTokenSource();

        private readonly IRadioService _radioService;
        private readonly IBatteryService _batteryService;
        private IDeviceEnumerator _deviceEnumerator;

        private readonly BleConnectionStrategy _bleStrategy;
        private readonly ClassicAudioConnectionStrategy _classicAudioStrategy;
        private readonly ClassicHidConnectionStrategy _classicHidStrategy;

        public event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;

        public ConcurrentDictionary<ulong, BluetoothLEDevice> ConnectedBleDevices => _connectedBleDevices;
        public ConcurrentDictionary<ulong, GattSession> ConnectedBleSessions => _connectedBleSessions;

        public ConnectionService(IRadioService radioService, IBatteryService batteryService)
        {
            _radioService = radioService ?? throw new ArgumentNullException(nameof(radioService));
            _batteryService = batteryService;

            _bleStrategy = new BleConnectionStrategy(this, batteryService);
            _classicAudioStrategy = new ClassicAudioConnectionStrategy(
                radioService,
                batteryService,
                addr =>
                {
                    if (_deviceEnumerator != null && _deviceEnumerator.HasConnectedPeripherals(addr, out string pName, out BluetoothDeviceType pType))
                    {
                        return (true, pName, pType);
                    }
                    return (false, null, BluetoothDeviceType.Unknown);
                },
                (addr, msg) => OperationProgressChanged?.Invoke(this, new DeviceOperationProgressEventArgs(addr, msg)));
            _classicHidStrategy = new ClassicHidConnectionStrategy(batteryService);
        }

        public void SetDeviceEnumerator(IDeviceEnumerator enumerator)
        {
            _deviceEnumerator = enumerator;
        }

        private SemaphoreSlim GetDeviceSemaphore(ulong address)
        {
            return _deviceSemaphores.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        }

        private static void SafeRelease(SemaphoreSlim semaphore)
        {
            if (semaphore == null) return;
            try { semaphore.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        public void CancelDeviceOperations(bool userRequestedAdapterOff = false)
        {
            if (userRequestedAdapterOff && _radioService != null)
            {
                _radioService.UserRequestedAdapterOff = true;
            }
            lock (_deviceOpCtsLock)
            {
                try { _deviceOpCts.Cancel(); } catch { }
                _deviceOpCts = new CancellationTokenSource();
            }
        }

        public void ReleaseConnectedBleHandle(ulong address)
        {
            if (_connectedBleSessions.TryRemove(address, out var session))
            {
                try { session.MaintainConnection = false; session.Dispose(); } catch { }
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ReleaseConnectedBleHandle: released GattSession for {address:X12}");
            }

            if (_connectedBleDevices.TryRemove(address, out var dev))
            {
                try { dev?.Dispose(); } catch { }
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ReleaseConnectedBleHandle: released handle for {address:X12}");
            }
        }

        public void ReleaseAllConnectedBleHandles(string reason)
        {
            var sessionKeys = _connectedBleSessions.Keys.ToArray();
            foreach (var addr in sessionKeys)
            {
                if (_connectedBleSessions.TryRemove(addr, out var session))
                {
                    try { session.MaintainConnection = false; session.Dispose(); } catch { }
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ReleaseAllConnectedBleHandles ({reason}): released GattSession for {addr:X12}");
                }
            }

            var keys = _connectedBleDevices.Keys.ToArray();
            foreach (var addr in keys)
            {
                if (_connectedBleDevices.TryRemove(addr, out var dev))
                {
                    try { dev?.Dispose(); } catch { }
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ReleaseAllConnectedBleHandles ({reason}): released handle for {addr:X12}");
                }
            }
        }

        public async Task<bool> ConnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            var res = await ConnectDeviceWithResultAsync(address, ct).ConfigureAwait(false);
            return res.IsSuccess;
        }

        public async Task<DeviceOperationResult> ConnectDeviceWithResultAsync(ulong address, CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ConnectDeviceAsync called for {address:X12}");

            if (_radioService.BluetoothRadio == null || _radioService.BluetoothRadio.State != RadioState.On)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ConnectDeviceAsync: Bluetooth adapter is OFF or absent. Aborting connection for {address:X12}.");
                return DeviceOperationResult.RadioOff("Bluetooth adapter is OFF or absent");
            }

            CancellationTokenSource linkedCts;
            lock (_deviceOpCtsLock)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _deviceOpCts.Token);
            }

            using (linkedCts)
            {
                var token = linkedCts.Token;
                var sem = GetDeviceSemaphore(address);
                try
                {
                    await sem.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ConnectDeviceAsync: Cancelled while waiting for lock ({address:X12})");
                    return DeviceOperationResult.Cancelled("Cancelled while waiting for lock");
                }

                try
                {
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            string targetDeviceName;
                            bool isBlePaired = false;
                            using (var client = new BluetoothClient())
                            {
                                var paired = client.PairedDevices;
                                var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                                if (device == null)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectDeviceAsync: device {address:X12} not found in Classic PairedDevices. Routing to BLE...");
                                    return await _bleStrategy.ConnectAsync(address, null, token).ConfigureAwait(false);
                                }

                                targetDeviceName = device.DeviceName;
                                device.Refresh();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  device.Refresh() called for '{targetDeviceName}', initial Connected={device.Connected}");
                                if (device.Connected)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetDeviceName}' is already connected.");
                                    return DeviceOperationResult.AlreadyConnected($"'{targetDeviceName}' is already connected");
                                }

                                bool bleConnected = false;
                                if (_connectedBleDevices.TryGetValue(address, out var existingBleHandle))
                                {
                                    bleConnected = (existingBleHandle.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                }

                                isBlePaired = false;
                                BluetoothDeviceType bleType = BluetoothDeviceType.Unknown;
                                BluetoothLEDevice tempBle = null;
                                try
                                {
                                    using (var bleLookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                                    using (var bleLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, bleLookupCts.Token))
                                    {
                                        tempBle = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                            .AsTask(bleLinkedCts.Token).ConfigureAwait(false);
                                    }

                                    if (tempBle != null)
                                    {
                                        if (tempBle.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                            bleConnected = true;
                                        isBlePaired = (tempBle.DeviceInformation?.Pairing?.IsPaired == true);
                                        bleType = DeviceClassifier.ClassifyByAppearance(tempBle.Appearance.Category, tempBle.Appearance.SubCategory);
                                        if (bleType == BluetoothDeviceType.Unknown)
                                        {
                                            bleType = DeviceClassifier.ClassifyByName(tempBle.Name);
                                        }
                                    }
                                }
                                catch { }
                                finally
                                {
                                    if (tempBle != null && (!_connectedBleDevices.TryGetValue(address, out var tracked) || !ReferenceEquals(tracked, tempBle)))
                                    {
                                        try { tempBle.Dispose(); } catch { }
                                    }
                                }

                                int codMajor = 0;
                                int codMinor = 0;
                                bool codReadSuccess = false;
                                try
                                {
                                    int cod = (int)device.ClassOfDevice.Value;
                                    codMajor = (cod >> 8) & 0x1F;
                                    codMinor = (cod >> 2) & 0x3F;
                                    codReadSuccess = true;
                                }
                                catch { }

                                var classicType = DeviceClassifier.ClassifyByCod(codMajor, codMinor);
                                if (classicType == BluetoothDeviceType.Unknown)
                                {
                                    classicType = DeviceClassifier.ClassifyByName(device.DeviceName);
                                }

                                bool isClassicPeripheral = classicType == BluetoothDeviceType.Mouse ||
                                                           classicType == BluetoothDeviceType.Keyboard ||
                                                           classicType == BluetoothDeviceType.Controller;
                                bool isBlePeripheral = bleType == BluetoothDeviceType.Mouse ||
                                                       bleType == BluetoothDeviceType.Keyboard ||
                                                       bleType == BluetoothDeviceType.Controller;
                                bool isPeripheral = isClassicPeripheral || isBlePeripheral;

                                IReadOnlyCollection<Guid> installedServices = null;
                                bool installedReadSuccess = false;
                                int installedCount = 0;
                                string installedProfilesStr = "none";
                                try
                                {
                                    var installed = device.InstalledServices;
                                    installedReadSuccess = true;
                                    if (installed != null)
                                    {
                                        installedServices = installed.ToArray();
                                        installedCount = installed.Count;
                                        installedProfilesStr = installedCount > 0
                                            ? string.Join(", ", installedServices.Select(BluetoothManager.GetProfileName))
                                            : "empty";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    installedReadSuccess = false;
                                    installedProfilesStr = $"Read error: {ex.Message}";
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Reading InstalledServices for '{targetDeviceName}' note: {ex.Message}");
                                }

                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  [InstalledServices] Device '{targetDeviceName}' ({address:X12}, Connected={device.Connected}): count={installedCount}, profiles=[{installedProfilesStr}]");

                                var capability = ConnectRoutingPolicy.ClassifyTransportCapability(isBlePaired, installedReadSuccess, installedServices, codMajor, codReadSuccess);
                                var route = ConnectRoutingPolicy.DetermineConnectionRoute(capability, codMajor, isPeripheral);

                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  [Routing Decision] Device '{targetDeviceName}' ({address:X12}): Capability={capability}, Route={route}, isBlePaired={isBlePaired}, CoDMajor=0x{codMajor:X2}, isPeripheral={isPeripheral}");

                                if (route == ConnectionRoute.BleDirect)
                                {
                                    return await _bleStrategy.ConnectAsync(address, targetDeviceName, token).ConfigureAwait(false);
                                }
                                else if (route == ConnectionRoute.ClassicWithHid)
                                {
                                    return await _classicHidStrategy.ConnectAsync(address, targetDeviceName, token).ConfigureAwait(false);
                                }
                                else
                                {
                                    var audioRes = await _classicAudioStrategy.ConnectAsync(address, targetDeviceName, token).ConfigureAwait(false);
                                    if ((audioRes.Status == DeviceOperationStatus.Failed || audioRes.Status == DeviceOperationStatus.TimedOut)
                                        && codMajor != 0x04
                                        && isBlePaired
                                        && !token.IsCancellationRequested
                                        && !_radioService.UserRequestedAdapterOff)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Classic connect chain did not establish connection for dual-mode device '{targetDeviceName}'. Attempting BLE fallback...");
                                        var bleRes = await _bleStrategy.ConnectAsync(address, targetDeviceName, token).ConfigureAwait(false);
                                        if (bleRes.IsSuccess) return bleRes;
                                    }
                                    return audioRes;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return DeviceOperationResult.Cancelled();
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"ConnectDeviceAsync inner exception: {ex.Message}");
                            return DeviceOperationResult.Failed(ex.Message);
                        }
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ConnectDeviceAsync cancelled for {address:X12}");
                    return DeviceOperationResult.Cancelled();
                }
                finally
                {
                    SafeRelease(sem);
                }
            }
        }

        public async Task<bool> DisconnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            var res = await DisconnectDeviceWithResultAsync(address, ct).ConfigureAwait(false);
            return res.IsSuccess;
        }

        public async Task<DeviceOperationResult> DisconnectDeviceWithResultAsync(ulong address, CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"DisconnectDeviceAsync called for {address:X12}");

            CancellationTokenSource linkedCts;
            lock (_deviceOpCtsLock)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _deviceOpCts.Token);
            }

            using (linkedCts)
            {
                var token = linkedCts.Token;
                var sem = GetDeviceSemaphore(address);
                try
                {
                    await sem.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"DisconnectDeviceAsync: Cancelled while waiting for lock ({address:X12})");
                    return DeviceOperationResult.Cancelled("Cancelled while waiting for lock");
                }

                try
                {
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            string targetName = null;
                            using (var client = new BluetoothClient())
                            {
                                var paired = client.PairedDevices;
                                var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                                if (device == null)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectDeviceAsync: device {address:X12} not found in Classic PairedDevices. Attempting BLE disconnect...");
                                    return await _bleStrategy.DisconnectAsync(address, null, token).ConfigureAwait(false);
                                }

                                targetName = device.DeviceName;
                                device.Refresh();
                                if (!device.Connected)
                                {
                                    bool bleStillConnected = false;
                                    if (_connectedBleDevices.TryGetValue(address, out var bleHandle))
                                    {
                                        bleStillConnected = (bleHandle.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                    }
                                    if (!bleStillConnected)
                                    {
                                        BluetoothLEDevice checkDev = null;
                                        try
                                        {
                                            using (var bleLookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                                            using (var bleLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, bleLookupCts.Token))
                                            {
                                                checkDev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                                    .AsTask(bleLinkedCts.Token).ConfigureAwait(false);
                                            }

                                            if (checkDev != null)
                                                bleStillConnected = (checkDev.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                        }
                                        catch { }
                                        finally
                                        {
                                            if (checkDev != null && (!_connectedBleDevices.TryGetValue(address, out var tracked) || !ReferenceEquals(tracked, checkDev)))
                                            {
                                                try { checkDev.Dispose(); } catch { }
                                            }
                                        }
                                    }

                                    if (bleStillConnected)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetName}' is connected over BLE — routing to BLE disconnect.");
                                        return await _bleStrategy.DisconnectAsync(address, targetName, token).ConfigureAwait(false);
                                    }

                                    _batteryService?.HandleDeviceDisconnected(address);
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetName}' is already disconnected (Classic and BLE).");
                                    return DeviceOperationResult.AlreadyDisconnected($"'{targetName}' is already disconnected (Classic and BLE)");
                                }

                                bool isPeripheral = false;
                                int codMajor = 0;
                                int codMinor = 0;
                                try
                                {
                                    int cod = (int)device.ClassOfDevice.Value;
                                    codMajor = (cod >> 8) & 0x1F;
                                    codMinor = (cod >> 2) & 0x3F;
                                }
                                catch { }

                                var classifiedType = DeviceClassifier.ClassifyByCod(codMajor, codMinor);
                                if (classifiedType != BluetoothDeviceType.Unknown)
                                {
                                    isPeripheral = DeviceClassifier.IsPeripheralType(classifiedType);
                                }
                                if (!isPeripheral)
                                {
                                    var nameType = DeviceClassifier.ClassifyByName(targetName);
                                    if (nameType != BluetoothDeviceType.Unknown)
                                    {
                                        isPeripheral = DeviceClassifier.IsPeripheralType(nameType);
                                    }
                                }

                                DeviceOperationResult disconnectedRes;
                                if (isPeripheral)
                                {
                                    disconnectedRes = await _classicHidStrategy.DisconnectAsync(address, targetName, token).ConfigureAwait(false);
                                }
                                else
                                {
                                    disconnectedRes = await _classicAudioStrategy.DisconnectAsync(address, targetName, token).ConfigureAwait(false);
                                }

                                ReleaseConnectedBleHandle(address);
                                return disconnectedRes;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return DeviceOperationResult.Cancelled();
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"DisconnectDeviceAsync inner exception: {ex.Message}");
                            return DeviceOperationResult.Failed(ex.Message);
                        }
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"DisconnectDeviceAsync cancelled for {address:X12}");
                    return DeviceOperationResult.Cancelled();
                }
                finally
                {
                    SafeRelease(sem);
                }
            }
        }

        public void Dispose()
        {
            ReleaseAllConnectedBleHandles("disposing ConnectionService");
            try { _deviceOpCts.Dispose(); } catch { }
            foreach (var sem in _deviceSemaphores.Values)
            {
                try { sem.Dispose(); } catch { }
            }
            _deviceSemaphores.Clear();
        }
    }
}
