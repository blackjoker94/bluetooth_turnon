using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon
{
    public sealed class DeviceOperationRecord
    {
        public long OperationId { get; }
        public ulong Address { get; }
        public bool UserIntentConnect { get; }
        public string StatusText { get; set; }
        public Color StatusColor { get; set; }
        public DateTime StartTimeUtc { get; }
        public CancellationTokenSource Cts { get; }

        public DeviceOperationRecord(long operationId, ulong address, bool userIntentConnect, string statusText, Color statusColor, CancellationTokenSource cts)
        {
            OperationId = operationId;
            Address = address;
            UserIntentConnect = userIntentConnect;
            StatusText = statusText;
            StatusColor = statusColor;
            StartTimeUtc = DateTime.UtcNow;
            Cts = cts;
        }
    }

    /// <summary>
    /// Presenter managing device connection/disconnection intent,
    /// in-flight tracking, and cancellation for the UI.
    /// Decouples hardware state decisions and tracking from Form1.
    /// </summary>
    public sealed class DevicePresenter : IDisposable
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly Func<ulong, CancellationToken, Task<bool?>> _connectionStatusChecker;
        private readonly Func<ulong, CancellationToken, Task<DeviceOperationResult>> _connectRunner;
        private readonly Func<ulong, CancellationToken, Task<DeviceOperationResult>> _disconnectRunner;
        private static long _nextOperationId = 0;
        private readonly ConcurrentDictionary<ulong, DeviceOperationRecord> _activeOperations =
            new ConcurrentDictionary<ulong, DeviceOperationRecord>();
        private readonly object _ctsLock = new object();
        private CancellationTokenSource _allClicksCts = new CancellationTokenSource();
        private volatile bool _disposed;

        public DevicePresenter(BluetoothManager bluetoothManager)
            : this(
                bluetoothManager,
                bluetoothManager != null ? bluetoothManager.IsDeviceConnectedAsync : (Func<ulong, CancellationToken, Task<bool?>>)null,
                bluetoothManager != null ? (Func<ulong, CancellationToken, Task<DeviceOperationResult>>)bluetoothManager.ConnectDeviceWithResultAsync : null,
                bluetoothManager != null ? (Func<ulong, CancellationToken, Task<DeviceOperationResult>>)bluetoothManager.DisconnectDeviceWithResultAsync : null)
        {
        }

        public DevicePresenter(
            BluetoothManager bluetoothManager,
            Func<ulong, CancellationToken, Task<bool?>> connectionStatusChecker,
            Func<ulong, CancellationToken, Task<DeviceOperationResult>> connectRunner,
            Func<ulong, CancellationToken, Task<DeviceOperationResult>> disconnectRunner)
        {
            _bluetoothManager = bluetoothManager;
            _connectionStatusChecker = connectionStatusChecker ?? (bluetoothManager != null ? bluetoothManager.IsDeviceConnectedAsync : null);
            _connectRunner = connectRunner ?? (bluetoothManager != null ? bluetoothManager.ConnectDeviceWithResultAsync : null);
            _disconnectRunner = disconnectRunner ?? (bluetoothManager != null ? bluetoothManager.DisconnectDeviceWithResultAsync : null);
        }

        public DevicePresenter(
            BluetoothManager bluetoothManager,
            Func<ulong, CancellationToken, Task<bool?>> connectionStatusChecker,
            Func<ulong, CancellationToken, Task<bool>> connectRunner,
            Func<ulong, CancellationToken, Task<bool>> disconnectRunner)
            : this(
                bluetoothManager,
                connectionStatusChecker,
                connectRunner != null
                    ? (Func<ulong, CancellationToken, Task<DeviceOperationResult>>)(async (addr, ct) =>
                    {
                        bool ok = await connectRunner(addr, ct).ConfigureAwait(false);
                        return ok ? DeviceOperationResult.Succeeded("Connected") : DeviceOperationResult.Failed("Connection failed");
                    })
                    : null,
                disconnectRunner != null
                    ? (Func<ulong, CancellationToken, Task<DeviceOperationResult>>)(async (addr, ct) =>
                    {
                        bool ok = await disconnectRunner(addr, ct).ConfigureAwait(false);
                        return ok ? DeviceOperationResult.Succeeded("Disconnected") : DeviceOperationResult.Failed("Disconnect failed");
                    })
                    : null)
        {
        }

        /// <summary>
        /// Checks if a device operation is currently in flight and retrieves its status.
        /// </summary>
        public bool TryGetInFlightStatus(ulong address, out string text, out Color color)
        {
            if (_activeOperations.TryGetValue(address, out var val))
            {
                text = val.StatusText;
                color = val.StatusColor;
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
            _activeOperations.AddOrUpdate(
                address,
                a => new DeviceOperationRecord(0, a, false, text, color, null),
                (a, existing) =>
                {
                    existing.StatusText = text;
                    existing.StatusColor = color;
                    return existing;
                });
        }

        /// <summary>
        /// Cancels all active in-flight device operations (e.g. when adapter turns off or form closes).
        /// </summary>
        public void CancelAllOperations() => CancelAllOperations(false);

        /// <summary>
        /// Cancels all active in-flight device operations (e.g. when adapter turns off or form closes).
        /// </summary>
        public void CancelAllOperations(bool userRequestedAdapterOff)
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

            foreach (var kvp in _activeOperations.ToArray())
            {
                try { kvp.Value.Cts?.Cancel(); } catch { }
            }
            _activeOperations.Clear();
            _bluetoothManager?.CancelDeviceOperations(userRequestedAdapterOff);
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

            long opId = Interlocked.Increment(ref _nextOperationId);
            CancellationTokenSource opCts = null;
            CancellationToken linkedToken;
            lock (_ctsLock)
            {
                if (_disposed || _allClicksCts.IsCancellationRequested || externalToken.IsCancellationRequested)
                {
                    return DeviceOperationResult.Cancelled("Cancelled before start");
                }
                opCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _allClicksCts.Token);
                linkedToken = opCts.Token;
            }

            var record = new DeviceOperationRecord(opId, addr, userIntentConnect, pendingText, pendingColor, opCts);

            // Attempt to claim this address for in-flight operation
            if (!_activeOperations.TryAdd(addr, record))
            {
                try { opCts.Dispose(); } catch { }
                // Already in flight
                return DeviceOperationResult.Cancelled("Operation already in flight");
            }

            try
            {
                onProgress?.Invoke(pendingText, pendingColor);

                BluetoothManager.Log($"[DevicePresenter] User requested {(userIntentConnect ? "Connect" : "Disconnect")} for '{device.Name}' ({addr:X12})");

                // Bound the fresh-state check (about 1s) so a slow BLE lookup can't hide feedback or stall
                bool? freshConnected = null;
                try
                {
                    using (var timeoutCts = new CancellationTokenSource())
                    using (var freshCheckCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken, timeoutCts.Token))
                    using (var delayCts = new CancellationTokenSource())
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

                        var delayTask = Task.Delay(1000, delayCts.Token);
                        try
                        {
                            var completed = await Task.WhenAny(checkTask, delayTask).ConfigureAwait(false);
                            if (completed == checkTask)
                            {
                                try { delayCts.Cancel(); } catch { }
                                freshConnected = await checkTask.ConfigureAwait(false);
                            }
                            else
                            {
                                // 1-second timeout reached! Cancel ongoing check task
                                try { timeoutCts.Cancel(); } catch { }
                                _ = checkTask.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                BluetoothManager.Log($"[DevicePresenter] Fresh state check timed out (1s) for '{device.Name}' ({addr:X12}). Proceeding with intended action.");
                                freshConnected = null;
                            }
                        }
                        finally
                        {
                            try { delayCts.Cancel(); } catch { }
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
                DeviceOperationResult opResult;
                if (userIntentConnect)
                {
                    if (_connectRunner != null)
                    {
                        opResult = await _connectRunner(addr, linkedToken).ConfigureAwait(false);
                    }
                    else if (_bluetoothManager != null)
                    {
                        opResult = await _bluetoothManager.ConnectDeviceWithResultAsync(addr, linkedToken).ConfigureAwait(false);
                    }
                    else
                    {
                        opResult = DeviceOperationResult.Failed("No connect runner available");
                    }
                }
                else
                {
                    if (_disconnectRunner != null)
                    {
                        opResult = await _disconnectRunner(addr, linkedToken).ConfigureAwait(false);
                    }
                    else if (_bluetoothManager != null)
                    {
                        opResult = await _bluetoothManager.DisconnectDeviceWithResultAsync(addr, linkedToken).ConfigureAwait(false);
                    }
                    else
                    {
                        opResult = DeviceOperationResult.Failed("No disconnect runner available");
                    }
                }

                return opResult;
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
                if (_activeOperations.TryGetValue(addr, out var current) && current.OperationId == opId)
                {
                    ((ICollection<KeyValuePair<ulong, DeviceOperationRecord>>)_activeOperations).Remove(
                        new KeyValuePair<ulong, DeviceOperationRecord>(addr, current));
                }
                try { opCts?.Dispose(); } catch { }
            }
        }

        public bool IsRadioBounceInProgress => _bluetoothManager?.IsRadioBounceInProgress ?? false;

        public bool AllowAutomaticRadioBounce
        {
            get => BluetoothManager.AllowAutomaticRadioBounce;
            set => BluetoothManager.AllowAutomaticRadioBounce = value;
        }

        public bool HasConnectedPeripherals(ulong ignoreAddress, out string hidName, out BluetoothDeviceType hidType)
        {
            if (_bluetoothManager != null)
            {
                return _bluetoothManager.HasConnectedPeripherals(ignoreAddress, out hidName, out hidType);
            }
            hidName = null;
            hidType = BluetoothDeviceType.Unknown;
            return false;
        }

        public async Task<(bool hasPeripherals, string name, BluetoothDeviceType type)> CheckConnectedPeripheralsAsync(ulong ignoreAddress, CancellationToken ct = default)
        {
            if (_bluetoothManager != null)
            {
                return await _bluetoothManager.CheckConnectedPeripheralsAsync(ignoreAddress, ct).ConfigureAwait(false);
            }
            return (false, null, BluetoothDeviceType.Unknown);
        }

        public async Task<bool> ManualResetBluetoothAsync(CancellationToken ct = default)
        {
            if (_bluetoothManager != null)
            {
                return await _bluetoothManager.ManualResetBluetoothAsync(ct).ConfigureAwait(false);
            }
            return false;
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
