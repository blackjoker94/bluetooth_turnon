using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon
{
    /// <summary>
    /// Presenter managing device connection/disconnection intent,
    /// in-flight tracking, and cancellation for the UI.
    /// Decouples hardware state decisions and tracking from Form1.
    /// </summary>
    public sealed class DevicePresenter : IDisposable
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly Func<ulong, CancellationToken, Task<bool?>> _connectionStatusChecker;
        private readonly Func<ulong, CancellationToken, Task<bool>> _connectRunner;
        private readonly Func<ulong, CancellationToken, Task<bool>> _disconnectRunner;
        private readonly ConcurrentDictionary<ulong, (string Text, Color Color)> _inFlightOperations =
            new ConcurrentDictionary<ulong, (string Text, Color Color)>();
        private readonly object _ctsLock = new object();
        private CancellationTokenSource _allClicksCts = new CancellationTokenSource();
        private bool _disposed;

        public DevicePresenter(BluetoothManager bluetoothManager)
            : this(
                bluetoothManager,
                bluetoothManager != null ? bluetoothManager.IsDeviceConnectedAsync : (Func<ulong, CancellationToken, Task<bool?>>)null,
                bluetoothManager != null ? bluetoothManager.ConnectDeviceAsync : (Func<ulong, CancellationToken, Task<bool>>)null,
                bluetoothManager != null ? bluetoothManager.DisconnectDeviceAsync : (Func<ulong, CancellationToken, Task<bool>>)null)
        {
        }

        public DevicePresenter(
            BluetoothManager bluetoothManager,
            Func<ulong, CancellationToken, Task<bool?>> connectionStatusChecker,
            Func<ulong, CancellationToken, Task<bool>> connectRunner,
            Func<ulong, CancellationToken, Task<bool>> disconnectRunner)
        {
            _bluetoothManager = bluetoothManager;
            _connectionStatusChecker = connectionStatusChecker ?? (bluetoothManager != null ? bluetoothManager.IsDeviceConnectedAsync : null);
            _connectRunner = connectRunner ?? (bluetoothManager != null ? bluetoothManager.ConnectDeviceAsync : null);
            _disconnectRunner = disconnectRunner ?? (bluetoothManager != null ? bluetoothManager.DisconnectDeviceAsync : null);
        }

        /// <summary>
        /// Checks if a device operation is currently in flight and retrieves its status.
        /// </summary>
        public bool TryGetInFlightStatus(ulong address, out string text, out Color color)
        {
            if (_inFlightOperations.TryGetValue(address, out var val))
            {
                text = val.Text;
                color = val.Color;
                return true;
            }
            text = null;
            color = Color.Empty;
            return false;
        }

        /// <summary>
        /// Updates the status text and color for an in-flight operation.
        /// </summary>
        public void UpdateInFlightStatus(ulong address, string text, Color color)
        {
            _inFlightOperations[address] = (text, color);
        }

        /// <summary>
        /// Cancels all active in-flight device operations (e.g. when adapter turns off or form closes).
        /// </summary>
        public void CancelAllOperations()
        {
            lock (_ctsLock)
            {
                try
                {
                    _allClicksCts.Cancel();
                    _allClicksCts.Dispose();
                }
                catch { }
                _allClicksCts = new CancellationTokenSource();
            }
            _inFlightOperations.Clear();
        }

        /// <summary>
        /// Executes a toggle operation (connect if disconnected, disconnect if connected).
        /// Decides connection intent, avoids redundant calls, and tracks in-flight state.
        /// </summary>
        public async Task<DeviceOperationResult> ExecuteDeviceToggleAsync(
            DeviceInfo device,
            CancellationToken externalToken,
            Action<string, Color> onProgress = null)
        {
            if (device == null)
                return DeviceOperationResult.Failed("Device is null");

            ulong addr = device.Address;
            bool userIntentConnect = !device.IsConnected;
            string pendingText = userIntentConnect ? "Connecting..." : "Disconnecting...";
            Color pendingColor = Color.FromArgb(255, 190, 50);

            // Attempt to claim this address for in-flight operation
            if (!_inFlightOperations.TryAdd(addr, (pendingText, pendingColor)))
            {
                // Already in flight
                return DeviceOperationResult.Cancelled("Operation already in flight");
            }

            try
            {
                onProgress?.Invoke(pendingText, pendingColor);

                CancellationToken linkedToken;
                lock (_ctsLock)
                {
                    if (_disposed || _allClicksCts.IsCancellationRequested || externalToken.IsCancellationRequested)
                    {
                        return DeviceOperationResult.Cancelled("Cancelled before start");
                    }
                    linkedToken = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _allClicksCts.Token).Token;
                }

                BluetoothManager.Log($"[DevicePresenter] User requested {(userIntentConnect ? "Connect" : "Disconnect")} for '{device.Name}' ({addr:X12})");

                // Bound the fresh-state check (about 1s) so a slow BLE lookup can't hide feedback or stall
                bool? freshConnected = null;
                try
                {
                    using (var timeoutCts = new CancellationTokenSource())
                    using (var freshCheckCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken, timeoutCts.Token))
                    {
                        Task<bool?> checkTask;
                        if (_connectionStatusChecker != null)
                        {
                            checkTask = _connectionStatusChecker(addr, freshCheckCts.Token);
                        }
                        else if (_bluetoothManager != null)
                        {
                            checkTask = _bluetoothManager.IsDeviceConnectedAsync(addr, freshCheckCts.Token);
                        }
                        else
                        {
                            checkTask = Task.FromResult<bool?>(null);
                        }

                        var delayTask = Task.Delay(1000, linkedToken);
                        var completed = await Task.WhenAny(checkTask, delayTask).ConfigureAwait(false);
                        if (completed == checkTask)
                        {
                            freshConnected = await checkTask.ConfigureAwait(false);
                        }
                        else
                        {
                            // 1-second timeout reached! Cancel ongoing check task
                            try { timeoutCts.Cancel(); } catch { }
                            BluetoothManager.Log($"[DevicePresenter] Fresh state check timed out (1s) for '{device.Name}' ({addr:X12}). Proceeding with intended action.");
                            freshConnected = null;
                        }
                    }
                }
                catch (OperationCanceledException) when (!linkedToken.IsCancellationRequested)
                {
                    BluetoothManager.Log($"[DevicePresenter] Fresh state check timed out (1s) for '{device.Name}' ({addr:X12}). Proceeding with intended action.");
                    freshConnected = null;
                }
                catch (Exception ex)
                {
                    BluetoothManager.Log($"[DevicePresenter] Fresh state check exception for '{device.Name}' ({addr:X12}): {ex.Message}. Proceeding with intended action.");
                    freshConnected = null;
                }

                // If the fresh state already equals the intended end state (userIntentConnect),
                // treat it as success, skip the operation, and refresh. NEVER FLIP.
                if (freshConnected.HasValue && freshConnected.Value == userIntentConnect)
                {
                    BluetoothManager.Log($"[DevicePresenter] Device '{device.Name}' ({addr:X12}) is already in desired state (connected={userIntentConnect}). Skipping operation.");
                    return userIntentConnect
                        ? DeviceOperationResult.AlreadyConnected()
                        : DeviceOperationResult.AlreadyDisconnected();
                }

                // Fresh state null means unknown, so proceed with the intended action. Never flip.
                bool success = false;
                if (userIntentConnect)
                {
                    if (_connectRunner != null)
                    {
                        success = await _connectRunner(addr, linkedToken).ConfigureAwait(false);
                    }
                    else if (_bluetoothManager != null)
                    {
                        success = await _bluetoothManager.ConnectDeviceAsync(addr, linkedToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (_disconnectRunner != null)
                    {
                        success = await _disconnectRunner(addr, linkedToken).ConfigureAwait(false);
                    }
                    else if (_bluetoothManager != null)
                    {
                        success = await _bluetoothManager.DisconnectDeviceAsync(addr, linkedToken).ConfigureAwait(false);
                    }
                }

                if (success)
                {
                    return DeviceOperationResult.Succeeded(userIntentConnect ? "Connected" : "Disconnected");
                }
                else
                {
                    return DeviceOperationResult.Failed(userIntentConnect ? "Connection failed" : "Disconnect failed");
                }
            }
            catch (OperationCanceledException)
            {
                return DeviceOperationResult.Cancelled("Operation was cancelled");
            }
            catch (UnauthorizedAccessException ex)
            {
                BluetoothManager.Log($"[DevicePresenter] Access denied toggling {addr:X12}: {ex.Message}");
                return DeviceOperationResult.AccessDenied("Elevation required");
            }
            catch (Exception ex)
            {
                BluetoothManager.Log($"[DevicePresenter] Exception toggling {addr:X12}: {ex.Message}");
                return DeviceOperationResult.Failed(ex.Message);
            }
            finally
            {
                _inFlightOperations.TryRemove(addr, out _);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelAllOperations();
            lock (_ctsLock)
            {
                try { _allClicksCts.Dispose(); } catch { }
            }
        }
    }
}
