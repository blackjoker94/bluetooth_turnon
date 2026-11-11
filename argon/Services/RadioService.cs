using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace bluetooth_turnon.Services
{
    public sealed class RadioService : IRadioService
    {
        private const string ConnectifyRegistrySubKey = @"SOFTWARE\Connectify";
        private const string BounceInProgressValueName = "BounceInProgress";
        private const string SettingsRegistryPath = @"SOFTWARE\Connectify\Settings";
        private const string AutoBounceSettingValueName = "AllowAutomaticRadioBounce";

        private static bool _allowAutomaticRadioBounce = LoadAllowAutomaticRadioBounce();

        public static Func<Task<bool>> IncompleteBounceRadioRestorerHook { get; set; }

        public static bool AllowAutomaticRadioBounce
        {
            get => _allowAutomaticRadioBounce;
            set
            {
                _allowAutomaticRadioBounce = value;
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
                    {
                        key?.SetValue(AutoBounceSettingValueName, value ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"Save AllowAutomaticRadioBounce exception: {ex.Message}");
                }
            }
        }

        public static bool LoadAllowAutomaticRadioBounce()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(AutoBounceSettingValueName);
                        if (val is int intVal) return intVal == 1;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void SetBounceInProgressMarker(bool inProgress)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(ConnectifyRegistrySubKey))
                {
                    if (key != null)
                    {
                        if (inProgress)
                        {
                            key.SetValue(BounceInProgressValueName, 1, RegistryValueKind.DWord);
                        }
                        else
                        {
                            key.DeleteValue(BounceInProgressValueName, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"SetBounceInProgressMarker({inProgress}) exception: {ex.Message}");
            }
        }

        public static bool GetBounceInProgressMarker()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ConnectifyRegistrySubKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(BounceInProgressValueName);
                        if (val is int intVal) return intVal == 1;
                    }
                }
            }
            catch { }
            return false;
        }

        public static async Task<bool> CheckAndRecoverIncompleteBounceAsync(IRadioService radioService = null)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ConnectifyRegistrySubKey, true))
                {
                    if (key == null) return false;
                    object val = key.GetValue(BounceInProgressValueName);
                    if (val is int intVal && intVal == 1)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "Startup: Detected incomplete radio bounce marker from a prior session/crash.");
                        bool restored = false;
                        try
                        {
                            if (IncompleteBounceRadioRestorerHook != null)
                            {
                                restored = await IncompleteBounceRadioRestorerHook().ConfigureAwait(false);
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Startup: Incomplete bounce radio restored via hook: {restored}");
                            }
                            else
                            {
                                Radio radio = null;
                                if (radioService != null)
                                {
                                    radio = await radioService.GetBluetoothRadioAsync(CancellationToken.None, forceRefresh: true).ConfigureAwait(false);
                                }
                                else
                                {
                                    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().ConfigureAwait(false);
                                    if (adapter != null)
                                    {
                                        radio = await adapter.GetRadioAsync().AsTask().ConfigureAwait(false);
                                    }
                                }

                                if (radio != null && radio.State != RadioState.On)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "Startup: Bluetooth radio is OFF after incomplete bounce. Restoring radio power to ON...");
                                    var status = await radio.SetStateAsync(RadioState.On).AsTask(CancellationToken.None).ConfigureAwait(false);
                                    restored = (status == RadioAccessStatus.Allowed);
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Startup: Restored radio to ON, status={status}, success={restored}");
                                }
                                else
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "Startup: Radio is already ON or not present.");
                                    restored = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"Startup: Exception while restoring radio: {ex.Message}");
                        }
                        finally
                        {
                            try
                            {
                                key.DeleteValue(BounceInProgressValueName, false);
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "Startup: Cleared BounceInProgress registry marker.");
                            }
                            catch { }
                        }
                        return restored;
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"CheckAndRecoverIncompleteBounceAsync exception: {ex.Message}");
            }
            return false;
        }

        private readonly SemaphoreSlim _radioInitSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _adapterToggleSemaphore = new SemaphoreSlim(1, 1);

        private volatile Radio _bluetoothRadio;
        private volatile bool _radioDiscoveryAttempted;
        private volatile bool _userRequestedAdapterOff;
        private bool _disposed;

        public event EventHandler<AdapterStateChangedEventArgs> AdapterStateChanged;
        public event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;
        public event Action<string> RadioTurnedOff;

        private volatile bool _isRadioBounceInProgress;
        public bool IsRadioBounceInProgress
        {
            get => _isRadioBounceInProgress;
            internal set => _isRadioBounceInProgress = value;
        }

        public bool UserRequestedAdapterOff
        {
            get => _userRequestedAdapterOff;
            set => _userRequestedAdapterOff = value;
        }

        public Radio BluetoothRadio => _bluetoothRadio;

        public static async Task EnsureBthServRunningAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    using (var sc = new ServiceController("bthserv"))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.Paused)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "Starting bthserv service...");
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "bthserv service is Running");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.InnerException is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 5)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, "Elevation required: Win32 error 5 (Access Denied) when starting bthserv. Run as Administrator if service operations are needed.");
                    }
                    else
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"bthserv check note: {ex.Message}");
                    }
                }
            }).ConfigureAwait(false);
        }

        public async Task<Radio> GetBluetoothRadioAsync(CancellationToken ct = default, bool forceRefresh = false)
        {
            if (_disposed) return null;

            var existingRadio = _bluetoothRadio;
            if (!forceRefresh && existingRadio != null && existingRadio.State != RadioState.Unknown)
            {
                return existingRadio;
            }

            if (_radioDiscoveryAttempted && !forceRefresh && (existingRadio == null || existingRadio.State == RadioState.Unknown))
            {
                return null;
            }

            await _radioInitSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                existingRadio = _bluetoothRadio;
                if (!forceRefresh && existingRadio != null && existingRadio.State != RadioState.Unknown)
                {
                    return existingRadio;
                }

                if (_radioDiscoveryAttempted && !forceRefresh && (existingRadio == null || existingRadio.State == RadioState.Unknown))
                {
                    return null;
                }

                bool wasAbsent = (existingRadio == null || existingRadio.State == RadioState.Unknown);

                if (existingRadio != null)
                {
                    try { existingRadio.StateChanged -= BluetoothRadio_StateChanged; } catch { }
                    _bluetoothRadio = null;
                }

                _radioDiscoveryAttempted = true;

                await EnsureBthServRunningAsync().ConfigureAwait(false);

                // 1. Primary: Try via BluetoothAdapter.GetDefaultAsync()
                try
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "Trying BluetoothAdapter.GetDefaultAsync()...");
                    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
                    if (adapter != null)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "BluetoothAdapter found. Calling GetRadioAsync()...");
                        var radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
                        if (radio != null && radio.State != RadioState.Unknown)
                        {
                            _bluetoothRadio = radio;
                            _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Found Bluetooth Radio via Adapter: '{radio.Name}', State={radio.State}");
                            if (wasAbsent)
                            {
                                bool isEnabled = (radio.State == RadioState.On);
                                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
                            }
                            return _bluetoothRadio;
                        }
                        else
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, radio == null ? "adapter.GetRadioAsync() returned null" : $"adapter.GetRadioAsync() returned radio with State={radio.State}");
                        }
                    }
                    else
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "BluetoothAdapter.GetDefaultAsync() returned null");
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"BluetoothAdapter path exception: {ex.Message}");
                }

                // 2. Secondary: Try Radio.GetRadiosAsync()
                try
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "Calling Radio.RequestAccessAsync()");
                    var access = await Radio.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"Radio.RequestAccessAsync returned: {access}");

                    var radios = await Radio.GetRadiosAsync().AsTask(ct).ConfigureAwait(false);
                    if (radios != null)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"Radio.GetRadiosAsync returned {radios.Count} radios");
                        foreach (var radio in radios)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"Radio: Name='{radio.Name}', Kind={radio.Kind}, State={radio.State}");
                            if (radio.Kind == RadioKind.Bluetooth && radio.State != RadioState.Unknown)
                            {
                                _bluetoothRadio = radio;
                                _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Found Bluetooth Radio via GetRadiosAsync: '{radio.Name}', State={radio.State}");
                                if (wasAbsent)
                                {
                                    bool isEnabled = (radio.State == RadioState.On);
                                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
                                }
                                return _bluetoothRadio;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"Radio.GetRadiosAsync path exception: {ex.Message}");
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "No Bluetooth Radio found by any method.");
                return null;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetBluetoothRadioAsync outer exception: {ex.Message}");
                return null;
            }
            finally
            {
                SafeRelease(_radioInitSemaphore);
            }
        }

        private void BluetoothRadio_StateChanged(Radio sender, object args)
        {
            try
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"BluetoothRadio_StateChanged event received. State={sender.State}");
                if (sender.State == RadioState.Unknown)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "BluetoothRadio_StateChanged: Radio state is Unknown (adapter unplugged or reset). Invalidating cached radio.");
                    try { sender.StateChanged -= BluetoothRadio_StateChanged; } catch { }
                    _bluetoothRadio = null;
                    _radioDiscoveryAttempted = false;

                    RadioTurnedOff?.Invoke("adapter removed");
                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(null, "No Adapter"));
                    return;
                }

                bool isEnabled = (sender.State == RadioState.On);
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"BluetoothRadio_StateChanged: isEnabled={isEnabled}");

                if (isEnabled)
                {
                    _userRequestedAdapterOff = false;
                }
                else
                {
                    if (!IsRadioBounceInProgress || _userRequestedAdapterOff)
                    {
                        RadioTurnedOff?.Invoke("external radio off");
                    }
                }

                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"BluetoothRadio_StateChanged exception: {ex.Message}");
            }
        }

        public async Task<bool?> GetAdapterStateAsync(CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetAdapterStateAsync called");
            try
            {
                var radio = _bluetoothRadio;
                if (radio == null || radio.State == RadioState.Unknown)
                {
                    radio = await GetBluetoothRadioAsync(ct, forceRefresh: true).ConfigureAwait(false);
                }

                if (radio != null && radio.State != RadioState.Unknown)
                {
                    bool isEnabled = (radio.State == RadioState.On);
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"GetAdapterStateAsync: {isEnabled} (State={radio.State})");
                    return isEnabled;
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "GetAdapterStateAsync: null (no radio found)");
                return null;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"GetAdapterStateAsync exception: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default, bool isUserInitiated = true)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"SetAdapterStateAsync(enable={enable}, isUserInitiated={isUserInitiated}) called");
            if (_disposed) return false;

            if (enable)
            {
                if (isUserInitiated)
                {
                    _userRequestedAdapterOff = false;
                }
            }
            else
            {
                if (isUserInitiated)
                {
                    _userRequestedAdapterOff = true;

                    // If a radio bounce is currently in progress, wait for it to unwind before toggling hardware
                    if (IsRadioBounceInProgress)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "SetAdapterStateAsync(OFF): Radio bounce is in progress. Cancelling bounce and awaiting unwind...");
                        var swUnwind = System.Diagnostics.Stopwatch.StartNew();
                        while (IsRadioBounceInProgress && swUnwind.ElapsedMilliseconds < 5000)
                        {
                            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                        }
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"SetAdapterStateAsync(OFF): Bounce unwind wait finished after {swUnwind.ElapsedMilliseconds}ms (IsRadioBounceInProgress={IsRadioBounceInProgress}).");
                    }
                }
            }

            var radio = _bluetoothRadio;
            if (radio == null || radio.State == RadioState.Unknown)
            {
                radio = await GetBluetoothRadioAsync(ct, forceRefresh: true).ConfigureAwait(false);
            }

            if (radio == null || radio.State == RadioState.Unknown)
            {
                if (!enable && isUserInitiated)
                {
                    _userRequestedAdapterOff = false;
                }
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, "SetAdapterStateAsync: Radio is null or Unknown, cannot toggle");
                return false;
            }

            if (!enable && radio.State == RadioState.Off)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "SetAdapterStateAsync(OFF): Radio is already in Off state. Syncing UI event.");
                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(false));
                return true;
            }

            await _adapterToggleSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RadioState targetState = enable ? RadioState.On : RadioState.Off;
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Calling radio.SetStateAsync({targetState})...");

                var result = await radio.SetStateAsync(targetState).AsTask(ct).ConfigureAwait(false);
                bool success = (result == RadioAccessStatus.Allowed);
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"radio.SetStateAsync returned: {result}, success={success}");

                if (success)
                {
                    if (!enable && isUserInitiated)
                    {
                        RadioTurnedOff?.Invoke("adapter off");
                    }
                }
                else
                {
                    if (!enable && isUserInitiated)
                    {
                        _userRequestedAdapterOff = false;
                    }
                }

                return success;
            }
            catch (Exception ex)
            {
                if (!enable && isUserInitiated)
                {
                    _userRequestedAdapterOff = false;
                }
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"SetAdapterStateAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                SafeRelease(_adapterToggleSemaphore);
            }
        }

        public async Task<bool> ManualResetBluetoothAsync(CancellationToken ct = default)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync: User initiated manual Bluetooth reset.");
            if (_disposed) return false;
            if (ct.IsCancellationRequested) return false;

            IsRadioBounceInProgress = true;
            SetBounceInProgressMarker(true);
            bool radioTurnedOffByBounce = false;
            bool onSuccess = false;
            OperationProgressChanged?.Invoke(this, new DeviceOperationProgressEventArgs(0, "Resetting Bluetooth adapter..."));

            try
            {
                if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync: Adapter is OFF or absent. Turning adapter ON...");
                    onSuccess = await SetAdapterStateAsync(true, ct, isUserInitiated: true).ConfigureAwait(false);
                    return onSuccess;
                }

                bool offSuccess = await SetAdapterStateAsync(false, ct, isUserInitiated: false).ConfigureAwait(false);
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ManualResetBluetoothAsync: adapter turned off (success={offSuccess})");
                if (!offSuccess)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, "ManualResetBluetoothAsync: SetAdapterStateAsync(false) failed, aborting reset.");
                    return false;
                }
                radioTurnedOffByBounce = true;

                await Task.Delay(1000, ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (_userRequestedAdapterOff)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync: aborted because user requested adapter OFF.");
                        break;
                    }

                    onSuccess = await SetAdapterStateAsync(true, CancellationToken.None, isUserInitiated: false).ConfigureAwait(false);
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ManualResetBluetoothAsync: adapter turned on attempt {attempt}/3 (success={onSuccess})");
                    if (onSuccess) break;
                    await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
                }

                return onSuccess;
            }
            catch (OperationCanceledException)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"ManualResetBluetoothAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (_userRequestedAdapterOff)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync finally: user requested adapter OFF; ensuring radio is powered OFF in hardware...");
                        try
                        {
                            var radio = _bluetoothRadio;
                            if (radio == null || radio.State == RadioState.Unknown)
                            {
                                radio = await GetBluetoothRadioAsync(CancellationToken.None, forceRefresh: true).ConfigureAwait(false);
                            }

                            if (radio != null && radio.State != RadioState.Off)
                            {
                                var res = await radio.SetStateAsync(RadioState.Off).AsTask(CancellationToken.None).ConfigureAwait(false);
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"ManualResetBluetoothAsync finally: SetStateAsync(Off) returned {res}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"ManualResetBluetoothAsync finally: power off exception: {ex.Message}");
                        }
                    }
                    else if (radioTurnedOffByBounce &&
                        !onSuccess &&
                        (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On) &&
                        !_disposed)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "ManualResetBluetoothAsync finally: radio was left OFF; restoring power...");
                        try { await SetAdapterStateAsync(true, CancellationToken.None, isUserInitiated: false).ConfigureAwait(false); } catch { }
                    }
                }
                catch { }

                SetBounceInProgressMarker(false);
                IsRadioBounceInProgress = false;

                try
                {
                    bool? finalState = _userRequestedAdapterOff
                        ? false
                        : (onSuccess ? true : ((_bluetoothRadio != null && _bluetoothRadio.State != RadioState.Unknown) ? (_bluetoothRadio.State == RadioState.On) : (bool?)null));
                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(finalState));
                }
                catch { }
            }
        }

        private static void SafeRelease(SemaphoreSlim semaphore)
        {
            if (semaphore == null) return;
            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_bluetoothRadio != null)
            {
                try { _bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged; } catch { }
                _bluetoothRadio = null;
            }

            try { _radioInitSemaphore.Dispose(); } catch { }
            try { _adapterToggleSemaphore.Dispose(); } catch { }
        }
    }
}
