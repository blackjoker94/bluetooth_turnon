using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace bluetooth_turnon.Services
{
    public sealed class ClassicHidConnectionStrategy : IConnectionStrategy
    {
        private readonly IBatteryService _batteryService;

        public ClassicHidConnectionStrategy(IBatteryService batteryService)
        {
            _batteryService = batteryService;
        }

        public bool CanHandle(ConnectionRoute route) => route == ConnectionRoute.ClassicWithHid;

        public async Task<bool> ConnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHidConnectionStrategy.ConnectAsync called for {address:X12}");

            return await Task.Run(async () =>
            {
                try
                {
                    using (var client = new BluetoothClient())
                    {
                        var paired = client.PairedDevices;
                        var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                        if (device == null)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicHid: device {address:X12} not found in PairedDevices.");
                            return false;
                        }

                        string targetName = device.DeviceName ?? (name ?? "HID Device");
                        device.Refresh();
                        if (device.Connected)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHid: '{targetName}' is already connected.");
                            return true;
                        }

                        ct.ThrowIfCancellationRequested();

                        var hidProfile = BluetoothService.HumanInterfaceDevice;
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHid: Setting service state HumanInterfaceDevice to true for '{targetName}'...");
                        try
                        {
                            device.SetServiceState(hidProfile, true);
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicHid: SetServiceState exception: {ex.Message}");
                        }

                        await Task.Delay(500, ct).ConfigureAwait(false);

                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (attempt > 1) await Task.Delay(500, ct).ConfigureAwait(false);

                            try
                            {
                                device.Refresh();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ClassicHid verify attempt {attempt}/5: Connected={device.Connected}");
                                if (device.Connected)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHid: '{targetName}' connected successfully on attempt {attempt}/5.");
                                    _batteryService?.ScheduleDeferredBatteryQuery(address, targetName, isClassicOnly: true, immediate: true);
                                    return true;
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ClassicHid verify attempt {attempt} exception: {ex.Message}");
                            }
                        }

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicHid: Connect failed for '{targetName}'.");
                        return false;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  ClassicHid.ConnectAsync exception: {ex.Message}");
                    return false;
                }
            }, ct).ConfigureAwait(false);
        }

        public async Task<bool> DisconnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHidConnectionStrategy.DisconnectAsync called for {address:X12}");

            return await Task.Run(async () =>
            {
                try
                {
                    using (var client = new BluetoothClient())
                    {
                        var paired = client.PairedDevices;
                        var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                        if (device == null)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicHid: device {address:X12} not found in PairedDevices.");
                            return false;
                        }

                        string targetName = device.DeviceName ?? (name ?? "HID Device");
                        device.Refresh();
                        if (!device.Connected)
                        {
                            _batteryService?.HandleDeviceDisconnected(address);
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHid: '{targetName}' is already disconnected.");
                            return true;
                        }

                        bool ioctlOk = NativeMethods.DisconnectDeviceViaIoctl(address);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ClassicHid: IOCTL disconnect result={ioctlOk} for '{targetName}'");

                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(800, ct).ConfigureAwait(false);

                            try
                            {
                                device.Refresh();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ClassicHid disconnect verify attempt {attempt}/5: Connected={device.Connected}");
                                if (!device.Connected)
                                {
                                    _batteryService?.HandleDeviceDisconnected(address);
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicHid: '{targetName}' disconnected successfully.");
                                    return true;
                                }
                            }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  ClassicHid disconnect verify attempt {attempt} exception: {ex.Message}");
                            }
                        }

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicHid: Disconnect failed for '{targetName}'.");
                        return false;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  ClassicHid.DisconnectAsync exception: {ex.Message}");
                    return false;
                }
            }, ct).ConfigureAwait(false);
        }
    }
}
