using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace bluetooth_turnon.Services
{
    public sealed class BleConnectionStrategy : IConnectionStrategy
    {
        private readonly IConnectionService _connectionService;
        private readonly IBatteryService _batteryService;

        public BleConnectionStrategy(IConnectionService connectionService, IBatteryService batteryService)
        {
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _batteryService = batteryService;
        }

        public bool CanHandle(ConnectionRoute route) => route == ConnectionRoute.BleDirect;

        public async Task<DeviceOperationResult> ConnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectBleDeviceAsync called for {address:X12}");
            BluetoothLEDevice bleDevice = null;
            bool success = false;
            try
            {
                using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                {
                    bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);
                }

                if (bleDevice == null)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ConnectBleDeviceAsync: BluetoothLEDevice {address:X12} not found.");
                    return DeviceOperationResult.DeviceNotFound($"BluetoothLEDevice {address:X12} not found.");
                }

                string bleName = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : (name ?? "BLE Device");
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectBleDeviceAsync: Found '{bleName}', initial ConnectionStatus={bleDevice.ConnectionStatus}");

                if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectBleDeviceAsync: '{bleName}' is already connected. Storing handle.");
                    _connectionService.ConnectedBleDevices.AddOrUpdate(address, bleDevice, (_, prev) =>
                    {
                        if (!ReferenceEquals(prev, bleDevice)) { try { prev.Dispose(); } catch { } }
                        return bleDevice;
                    });
                    success = true;
                    _batteryService?.ScheduleDeferredBatteryQuery(address, bleName, isClassicOnly: false, immediate: true);
                    return DeviceOperationResult.AlreadyConnected($"'{bleName}' is already connected.");
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ConnectBleDeviceAsync: Requesting uncached GATT services to trigger connection for '{bleName}'...");
                GattDeviceServicesResult gattResult = null;
                try
                {
                    using (var gattCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using (var linkedGattCts = CancellationTokenSource.CreateLinkedTokenSource(ct, gattCts.Token))
                    {
                        gattResult = await bleDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                            .AsTask(linkedGattCts.Token).ConfigureAwait(false);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ConnectBleDeviceAsync: GATT query completed with status {gattResult?.Status}.");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "  ConnectBleDeviceAsync: GATT service query timed out; checking connection status...");
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ConnectBleDeviceAsync: GATT service query note: {ex.Message}");
                }

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ConnectBleDeviceAsync: Verify attempt {attempt}/5: ConnectionStatus={bleDevice.ConnectionStatus}");
                    if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectBleDeviceAsync: '{bleName}' connected successfully on attempt {attempt}/5. Storing handle.");
                        _connectionService.ConnectedBleDevices.AddOrUpdate(address, bleDevice, (_, prev) =>
                        {
                            if (!ReferenceEquals(prev, bleDevice)) { try { prev.Dispose(); } catch { } }
                            return bleDevice;
                        });

                        try
                        {
                            var session = await GattSession.FromDeviceIdAsync(bleDevice.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
                            if (session != null)
                            {
                                session.MaintainConnection = true;
                                _connectionService.ConnectedBleSessions.AddOrUpdate(address, session, (_, prev) =>
                                {
                                    try { prev.MaintainConnection = false; prev.Dispose(); } catch { }
                                    return session;
                                });
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectBleDeviceAsync: GattSession MaintainConnection set for '{bleName}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ConnectBleDeviceAsync: GattSession initialization note: {ex.Message}");
                        }

                        success = true;
                        _batteryService?.ScheduleDeferredBatteryQuery(address, bleName, isClassicOnly: false, immediate: true);
                        return DeviceOperationResult.Succeeded($"'{bleName}' connected successfully.");
                    }
                    await Task.Delay(800, ct).ConfigureAwait(false);
                }

                if (gattResult?.Services != null)
                {
                    foreach (var svc in gattResult.Services) { try { svc.Dispose(); } catch { } }
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ConnectBleDeviceAsync failed for '{bleName}' ({address:X12}).");
                return DeviceOperationResult.Failed($"Connection failed for '{bleName}' ({address:X12}).");
            }
            catch (OperationCanceledException)
            {
                return DeviceOperationResult.Cancelled();
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  ConnectBleDeviceAsync exception: {ex.Message}");
                return DeviceOperationResult.Failed(ex.Message);
            }
            finally
            {
                if (!success)
                {
                    try { bleDevice?.Dispose(); } catch { }
                }
            }
        }

        public async Task<DeviceOperationResult> DisconnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectBleDeviceAsync called for {address:X12}");

            _connectionService.ReleaseConnectedBleHandle(address);

            BluetoothLEDevice bleDevice = null;
            try
            {
                using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                {
                    bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);
                }

                if (bleDevice == null)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  DisconnectBleDeviceAsync: BluetoothLEDevice {address:X12} not found.");
                    return DeviceOperationResult.DeviceNotFound($"BluetoothLEDevice {address:X12} not found.");
                }

                string bleName = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : (name ?? "BLE Device");
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectBleDeviceAsync: Found '{bleName}', initial ConnectionStatus={bleDevice.ConnectionStatus}");

                if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    _batteryService?.HandleDeviceDisconnected(address);
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectBleDeviceAsync: '{bleName}' is already disconnected.");
                    return DeviceOperationResult.AlreadyDisconnected($"'{bleName}' is already disconnected.");
                }

                try
                {
                    bool ioctlSuccess = NativeMethods.DisconnectDeviceViaIoctl(address);
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  DisconnectBleDeviceAsync: IOCTL disconnect returned {ioctlSuccess} for '{bleName}'");
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  DisconnectBleDeviceAsync: IOCTL disconnect note: {ex.Message}");
                }

                try
                {
                    bleDevice.Dispose();
                    bleDevice = null;
                }
                catch { }

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(800, ct).ConfigureAwait(false);

                    try
                    {
                        using (var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        using (var linkedCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct, checkCts.Token))
                        {
                            using (var checkDev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                .AsTask(linkedCheckCts.Token).ConfigureAwait(false))
                            {
                                if (checkDev == null || checkDev.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                                {
                                    _batteryService?.HandleDeviceDisconnected(address);
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  DisconnectBleDeviceAsync: '{bleName}' disconnected on attempt {attempt}/5.");
                                    return DeviceOperationResult.Succeeded($"'{bleName}' disconnected successfully.");
                                }
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  DisconnectBleDeviceAsync: Verify attempt {attempt}/5: ConnectionStatus={checkDev.ConnectionStatus}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  DisconnectBleDeviceAsync verify attempt {attempt} note: {ex.Message}");
                    }
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  DisconnectBleDeviceAsync failed for '{bleName}' ({address:X12}).");
                return DeviceOperationResult.Failed($"Disconnect failed for '{bleName}' ({address:X12}).");
            }
            catch (OperationCanceledException)
            {
                return DeviceOperationResult.Cancelled();
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  DisconnectBleDeviceAsync exception: {ex.Message}");
                return DeviceOperationResult.Failed(ex.Message);
            }
            finally
            {
                try { bleDevice?.Dispose(); } catch { }
            }
        }
    }
}
