using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;
using Windows.Foundation;

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
    /// Represents a paired Bluetooth device with optional battery information.
    /// Battery is nullable — many BLE devices do not expose the battery service.
    /// </summary>
    public sealed class DeviceInfo
    {
        public string Name { get; }
        public int? BatteryPercent { get; }

        public DeviceInfo(string name, int? batteryPercent)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            BatteryPercent = batteryPercent;
        }
    }

    /// <summary>
    /// Single source of truth for all Bluetooth adapter and device operations.
    /// Uses Windows BluetoothAdapter & Radio APIs.
    /// </summary>
    public sealed class BluetoothManager : IDisposable
    {
        public static void Log(string msg)
        {
            try { File.AppendAllText(@"app_log.txt", $"{DateTime.Now:HH:mm:ss.fff}: {msg}\r\n"); } catch { }
        }

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private Radio _bluetoothRadio;
        private bool _disposed;

        /// <summary>
        /// Fired whenever the Bluetooth adapter state changes (internally or externally in Windows).
        /// </summary>
        public event EventHandler<AdapterStateChangedEventArgs> AdapterStateChanged;

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

                // Delay briefly for OS state change to propagate
                await Task.Delay(300, ct).ConfigureAwait(false);

                bool? currentState = (radio.State == RadioState.On);
                Log($"Post-toggle radio.State={radio.State}");
                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(currentState));

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

        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            var results = new List<DeviceInfo>();
            try
            {
                bool? state = await GetAdapterStateAsync(ct).ConfigureAwait(false);
                if (state != true) return results;
                // Paired devices query can be implemented here
            }
            catch
            {
                // Return empty list on failure
            }
            return results;
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
