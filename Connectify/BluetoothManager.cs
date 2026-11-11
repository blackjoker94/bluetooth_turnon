using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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
            {
                ctr = ct.Register(() =>
                {
                    try { asyncOp.Cancel(); } catch { }
                    // Allow WinRT 500ms to invoke Completed gracefully; fallback to ensure caller is unblocked if WinRT hangs
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try { ctr.Dispose(); } catch { }
                        tcs.TrySetCanceled();
                    });
                });
            }

            tcs.Task.ContinueWith(_ =>
            {
                try { ctr.Dispose(); } catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);

            asyncOp.Completed = (info, status) =>
            {
                try { ctr.Dispose(); } catch { }
                switch (status)
                {
                    case AsyncStatus.Completed:
                        T res = default;
                        try { res = info.GetResults(); } catch { }
                        if (ct.IsCancellationRequested)
                        {
                            // Cancellation was requested while the operation completed.
                            // Immediately dispose the result so it doesn't leak or hold the ACL link open,
                            // then signal cancellation on tcs.
                            DisposeWinRtObject(res);
                            tcs.TrySetCanceled();
                        }
                        else if (!tcs.TrySetResult(res))
                        {
                            DisposeWinRtObject(res);
                        }
                        break;
                    case AsyncStatus.Canceled:
                        tcs.TrySetCanceled();
                        break;
                    case AsyncStatus.Error:
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error."));
                        }
                        break;
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
            {
                ctr = ct.Register(() =>
                {
                    try { asyncAction.Cancel(); } catch { }
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try { ctr.Dispose(); } catch { }
                        tcs.TrySetCanceled();
                    });
                });
            }

            tcs.Task.ContinueWith(_ =>
            {
                try { ctr.Dispose(); } catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);

            asyncAction.Completed = (info, status) =>
            {
                try { ctr.Dispose(); } catch { }
                switch (status)
                {
                    case AsyncStatus.Completed:
                        try { info.GetResults(); } catch { }
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetResult(true);
                        }
                        break;
                    case AsyncStatus.Canceled:
                        tcs.TrySetCanceled();
                        break;
                    case AsyncStatus.Error:
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error."));
                        }
                        break;
                }
            };
            return tcs.Task;
        }

        private static void DisposeWinRtObject(object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult gattResult)
                {
                    if (gattResult.Services != null)
                    {
                        foreach (var s in gattResult.Services)
                        {
                            try { s.Dispose(); } catch { }
                        }
                    }
                }
                else if (obj is Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceServicesResult rfcommResult)
                {
                    if (rfcommResult.Services != null)
                    {
                        foreach (var s in rfcommResult.Services)
                        {
                            try { s.Dispose(); } catch { }
                        }
                    }
                }

                if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch { }
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
    /// Event arguments for operation status updates (e.g., "Reconnecting Bluetooth...").
    /// </summary>
    public sealed class DeviceOperationProgressEventArgs : EventArgs
    {
        public ulong Address { get; }
        public string StatusMessage { get; }

        public DeviceOperationProgressEventArgs(ulong address, string statusMessage)
        {
            Address = address;
            StatusMessage = statusMessage;
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

    public enum AdapterUiState
    {
        NoAdapter,
        Disabled,
        Enabled,
        TurningOn,
        TurningOff,
        Reconnecting
    }

    public enum DeviceTransportCapability
    {
        Unknown,
        ClassicCapable,
        BleOnly
    }

    public enum ConnectionRoute
    {
        ClassicWithAudio,
        ClassicWithHid,
        BleDirect
    }

    public enum DeviceOperationStatus
    {
        Success,
        Failed,
        Cancelled,
        AccessDenied,
        AlreadyInDesiredState,
        RadioOff
    }

    public sealed class DeviceOperationResult
    {
        public DeviceOperationStatus Status { get; }
        public string Message { get; }
        public bool IsSuccess => Status == DeviceOperationStatus.Success || Status == DeviceOperationStatus.AlreadyInDesiredState;

        public DeviceOperationResult(DeviceOperationStatus status, string message = null)
        {
            Status = status;
            Message = message ?? status.ToString();
        }

        public static DeviceOperationResult Succeeded(string msg = "Success") => new DeviceOperationResult(DeviceOperationStatus.Success, msg);
        public static DeviceOperationResult AlreadyConnected(string msg = "Already connected") => new DeviceOperationResult(DeviceOperationStatus.AlreadyInDesiredState, msg);
        public static DeviceOperationResult AlreadyDisconnected(string msg = "Already disconnected") => new DeviceOperationResult(DeviceOperationStatus.AlreadyInDesiredState, msg);
        public static DeviceOperationResult Cancelled(string msg = "Cancelled") => new DeviceOperationResult(DeviceOperationStatus.Cancelled, msg);
        public static DeviceOperationResult Failed(string reason) => new DeviceOperationResult(DeviceOperationStatus.Failed, reason);
        public static DeviceOperationResult AccessDenied(string reason = "Elevation required") => new DeviceOperationResult(DeviceOperationStatus.AccessDenied, reason);
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
    /// Outcome of a battery query attempt.
    /// </summary>
    public enum BatteryQueryOutcome
    {
        Success,
        Unsupported,
        TransientFailure
    }

    /// <summary>
    /// Thread-safe cache entry tracking battery level, last-good value, query outcome,
    /// and backoff retry timings.
    /// </summary>
    public sealed class BatteryCacheEntry
    {
        public ulong Address { get; set; }
        public string DeviceName { get; set; }
        public int? BatteryPercent { get; set; }
        public int? LastGoodBattery { get; set; }
        public DateTime? LastGoodUtc { get; set; }
        public BatteryQueryOutcome Outcome { get; set; }
        public DateTime LastCheckedUtc { get; set; }
        public int FailureCount { get; set; }
        public DateTime NextAllowedRetryUtc { get; set; }
        public bool IsClassicOnly { get; set; }

        public int? GetEffectiveBattery()
        {
            if (BatteryPercent.HasValue) return BatteryPercent;
            if (LastGoodBattery.HasValue && LastGoodUtc.HasValue &&
                (DateTime.UtcNow - LastGoodUtc.Value) < BluetoothManager.BatteryLastGoodRetentionDuration)
            {
                return LastGoodBattery;
            }
            return null;
        }

        public bool ShouldRetry()
        {
            if (Outcome == BatteryQueryOutcome.Unsupported)
            {
                return DateTime.UtcNow >= NextAllowedRetryUtc;
            }
            if (Outcome == BatteryQueryOutcome.TransientFailure)
            {
                return DateTime.UtcNow >= NextAllowedRetryUtc;
            }
            return false;
        }

        public void RecordSuccess(int level, string deviceName)
        {
            DeviceName = deviceName;
            BatteryPercent = level;
            LastGoodBattery = level;
            LastGoodUtc = DateTime.UtcNow;
            Outcome = BatteryQueryOutcome.Success;
            LastCheckedUtc = DateTime.UtcNow;
            FailureCount = 0;
            NextAllowedRetryUtc = DateTime.UtcNow.AddMinutes(5);
        }

        public void RecordUnsupported(string deviceName, bool isClassicOnly = false)
        {
            DeviceName = deviceName;
            BatteryPercent = null;
            Outcome = BatteryQueryOutcome.Unsupported;
            LastCheckedUtc = DateTime.UtcNow;
            IsClassicOnly = isClassicOnly;
            NextAllowedRetryUtc = DateTime.UtcNow.Add(BluetoothManager.BatteryUnsupportedCacheDuration);
        }

        public void RecordTransientFailure(string deviceName)
        {
            DeviceName = deviceName;
            Outcome = BatteryQueryOutcome.TransientFailure;
            LastCheckedUtc = DateTime.UtcNow;
            FailureCount++;
            NextAllowedRetryUtc = DateTime.UtcNow.Add(BluetoothManager.GetTransientBackoff(FailureCount));

            if (LastGoodBattery.HasValue && LastGoodUtc.HasValue &&
                (DateTime.UtcNow - LastGoodUtc.Value) < BluetoothManager.BatteryLastGoodRetentionDuration)
            {
                BatteryPercent = LastGoodBattery;
            }
            else
            {
                BatteryPercent = null;
            }
        }
    }

    public sealed class BatteryQueryResult
    {
        public BatteryQueryOutcome Outcome { get; set; }
        public int? BatteryLevel { get; set; }
        public long ElapsedMs { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Single source of truth for all Bluetooth adapter and device operations.
    /// Uses Windows BluetoothAdapter & Radio APIs.
    /// </summary>
    public sealed class BluetoothManager : IDisposable
    {
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error
        }

        private const long MaxLogFileSizeBytes = 1024 * 1024; // 1 MB
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Connectify", "app_log.txt");
        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), 2000);
        private static readonly Thread _logWorkerThread;
        private static volatile bool _logWriterRunning = true;
        private static readonly Regex _macRegex = new Regex(@"\b([0-9A-Fa-f]{2}[:-]?){5}([0-9A-Fa-f]{2})\b|\b[0-9A-Fa-f]{12}\b", RegexOptions.Compiled);

        #region Diagnostic Switches (Connect & Disconnect Chain Step Toggles)
        public static bool EnableStepWake = true;
        public static bool EnableStepCoreAudio = true;
        public static bool EnableStepServiceCycle = true;
        public static bool EnableStepBounce = true;
        public static bool EnableDisconnectCoreAudio = true;
        public static bool EnableDisconnectIoctl = true;
        #endregion

        #region Core Audio Watchdog & Circuit Breaker Settings
        internal static int CoreAudioWatchdogTimeoutMs = 5000;
        private static int _coreAudioConsecutiveTimeouts = 0;
        private static bool _coreAudioCircuitBreakerOpen = false;
        private const int CoreAudioMaxConsecutiveTimeouts = 3;

        internal static bool CoreAudioCircuitBreakerOpen
        {
            get => _coreAudioCircuitBreakerOpen;
            set
            {
                _coreAudioCircuitBreakerOpen = value;
                if (!value) _coreAudioConsecutiveTimeouts = 0;
            }
        }
        internal static int CoreAudioConsecutiveTimeouts => _coreAudioConsecutiveTimeouts;
        internal static Func<IKsControl, KSIDENTIFIER, int> KsPropertyInvoker { get; set; }
        #endregion

        static BluetoothManager()
        {
            _logWorkerThread = new Thread(ProcessLogQueue)
            {
                IsBackground = true,
                Name = "ConnectifyLogWriter",
                Priority = ThreadPriority.BelowNormal
            };
            _logWorkerThread.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ShutdownLogger();
        }

        public static string MaskAddress(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return _macRegex.Replace(input, m =>
            {
                string val = m.Value;
                if (val.Length == 12)
                {
                    // Mask first 8 hex characters: ********ABCD
                    return "********" + val.Substring(8);
                }
                else if (val.Length == 17)
                {
                    // Form XX:XX:XX:XX:XX:XX -> **:**:**:**:XX:XX
                    return "**:**:**:**:" + val.Substring(12);
                }
                return val;
            });
        }

        public static void Log(string msg) => Log(LogLevel.Info, msg);

        public static void Log(LogLevel level, string msg)
        {
            if (string.IsNullOrEmpty(msg) || !_logWriterRunning) return;
            string masked = MaskAddress(msg);
            string entry = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {masked}";
            _logQueue.TryAdd(entry);
        }

        private static void ProcessLogQueue()
        {
            var batch = new List<string>(64);
            while (_logWriterRunning || _logQueue.Count > 0)
            {
                try
                {
                    string first;
                    if (_logQueue.TryTake(out first, 500))
                    {
                        batch.Add(first);
                        while (batch.Count < 100 && _logQueue.TryTake(out string next))
                        {
                            batch.Add(next);
                        }

                        WriteLogBatch(batch);
                        batch.Clear();
                    }
                }
                catch { }
            }
        }

        private static void WriteLogBatch(List<string> entries)
        {
            if (entries == null || entries.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_logPath))
                {
                    var fileInfo = new FileInfo(_logPath);
                    if (fileInfo.Length >= MaxLogFileSizeBytes)
                    {
                        try
                        {
                            string backupPath = Path.Combine(dir, "app_log.old.txt");
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            File.Move(_logPath, backupPath);
                        }
                        catch
                        {
                            File.WriteAllText(_logPath, string.Empty);
                        }
                    }
                }

                File.AppendAllLines(_logPath, entries);
            }
            catch { }
        }

        private static void ShutdownLogger()
        {
            _logWriterRunning = false;
            try
            {
                _logQueue.CompleteAdding();
                _logWorkerThread?.Join(500);
            }
            catch { }
        }

        private readonly SemaphoreSlim _radioInitSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _adapterToggleSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _deviceSemaphores = new ConcurrentDictionary<ulong, SemaphoreSlim>();

        private SemaphoreSlim GetDeviceSemaphore(ulong address)
        {
            return _deviceSemaphores.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        }

        private const string PendingProfilesRegistryPath = @"SOFTWARE\Connectify\PendingProfileRestores";
        private const string ConnectifyRegistrySubKey = @"SOFTWARE\Connectify";
        private const string BounceInProgressValueName = "BounceInProgress";
        private const string SettingsRegistryPath = @"SOFTWARE\Connectify\Settings";
        private const string AutoBounceSettingValueName = "AllowAutomaticRadioBounce";

        private static bool _allowAutomaticRadioBounce = LoadAllowAutomaticRadioBounce();

        internal static Func<Task<bool>> IncompleteBounceRadioRestorerHook { get; set; }

        /// <summary>
        /// Gets or sets whether the connection chain is allowed to automatically bounce (power cycle)
        /// the Bluetooth radio adapter when pre-bounce steps fail. Off (false) by default.
        /// Setting is persisted in the registry (HKCU\SOFTWARE\Connectify\Settings) and survives restarts.
        /// </summary>
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
                    Log($"Save AllowAutomaticRadioBounce exception: {ex.Message}");
                }
            }
        }

        internal static bool LoadAllowAutomaticRadioBounce()
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
            return false; // Default: OFF (never bounce automatically)
        }

        internal static void SetBounceInProgressMarker(bool inProgress)
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
                Log($"SetBounceInProgressMarker({inProgress}) exception: {ex.Message}");
            }
        }

        internal static bool GetBounceInProgressMarker()
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

        internal static async Task<bool> CheckAndRecoverIncompleteBounceAsync(BluetoothManager manager = null)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ConnectifyRegistrySubKey, true))
                {
                    if (key == null) return false;
                    object val = key.GetValue(BounceInProgressValueName);
                    if (val is int intVal && intVal == 1)
                    {
                        Log("Startup: Detected incomplete radio bounce marker from a prior session/crash.");
                        bool restored = false;
                        try
                        {
                            if (IncompleteBounceRadioRestorerHook != null)
                            {
                                restored = await IncompleteBounceRadioRestorerHook().ConfigureAwait(false);
                                Log($"Startup: Incomplete bounce radio restored via hook: {restored}");
                            }
                            else
                            {
                                Radio radio = null;
                                if (manager != null)
                                {
                                    radio = await manager.GetBluetoothRadioAsync(CancellationToken.None, forceRefresh: true).ConfigureAwait(false);
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
                                    Log("Startup: Bluetooth radio is OFF after incomplete bounce. Restoring radio power to ON...");
                                    var status = await radio.SetStateAsync(RadioState.On).AsTask(CancellationToken.None).ConfigureAwait(false);
                                    restored = (status == RadioAccessStatus.Allowed);
                                    Log($"Startup: Restored radio to ON, status={status}, success={restored}");
                                }
                                else
                                {
                                    Log("Startup: Radio is already ON or not present.");
                                    restored = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Startup: Exception while restoring radio: {ex.Message}");
                        }
                        finally
                        {
                            try
                            {
                                key.DeleteValue(BounceInProgressValueName, false);
                                Log("Startup: Cleared BounceInProgress registry marker.");
                            }
                            catch { }
                        }
                        return restored;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CheckAndRecoverIncompleteBounceAsync exception: {ex.Message}");
            }
            return false;
        }

        public BluetoothManager()
        {
            // Recover any profile services that were left disabled by an abnormal termination (Fix Issue #11)
            // and recover radio power if an incomplete radio bounce occurred before abnormal termination (Fix Issue #5)
            Task.Run(async () =>
            {
                RecoverPendingProfileRestores();
                await CheckAndRecoverIncompleteBounceAsync(this).ConfigureAwait(false);
            });
        }

        private static void RecordPendingProfileRestore(ulong address, Guid profile)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(PendingProfilesRegistryPath))
                {
                    key?.SetValue($"{address:X12}_{profile}", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static void ClearPendingProfileRestore(ulong address, Guid profile)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PendingProfilesRegistryPath, true))
                {
                    key?.DeleteValue($"{address:X12}_{profile}", false);
                }
            }
            catch { }
        }

        private static void RecoverPendingProfileRestores()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PendingProfilesRegistryPath, true))
                {
                    if (key == null) return;
                    string[] names = key.GetValueNames();
                    if (names == null || names.Length == 0) return;

                    Log($"Startup: Found {names.Length} pending profile restore marker(s) from previous abnormal shutdown.");
                    using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                    {
                        var paired = client.PairedDevices;
                        foreach (string name in names)
                        {
                            try
                            {
                                var parts = name.Split('_');
                                if (parts.Length == 2 &&
                                    ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong addr) &&
                                    Guid.TryParse(parts[1], out Guid profile))
                                {
                                    var dev = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == addr);
                                    if (dev != null)
                                    {
                                        dev.SetServiceState(profile, true);
                                        Log($"  Startup recovery: Restored service {profile} on device '{dev.DeviceName}' ({addr:X12})");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"  Startup recovery exception for {name}: {ex.Message}");
                            }
                            finally
                            {
                                try { key.DeleteValue(name, false); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"RecoverPendingProfileRestores exception: {ex.Message}");
            }
        }

        #region Battery GATT Configuration & Budgets

        /// <summary>
        /// Timeout budget for fast cached GATT queries (services/characteristics from local OS cache).
        /// Default: 2000ms (2 seconds).
        /// </summary>
        public static int BatteryCachedQueryBudgetMs { get; set; } = 2000;

        /// <summary>
        /// Timeout budget for slow uncached background GATT discovery (over-the-air with peripheral).
        /// Default: 10000ms (10 seconds) chosen from the slowest measured over-the-air discovery results.
        /// </summary>
        public static int BatteryUncachedQueryBudgetMs { get; set; } = 10000;

        /// <summary>
        /// Maximum bounded retries while connected before capping at the periodic 5-minute interval.
        /// </summary>
        public static int MaxBatteryRetries { get; set; } = 5;

        /// <summary>
        /// Duration to cache devices confirmed to NOT support GATT Battery Service.
        /// Default: 24 hours (queried once and then left alone).
        /// </summary>
        public static TimeSpan BatteryUnsupportedCacheDuration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Duration to retain the last-good battery reading across transient packet drops or timeouts.
        /// Default: 30 minutes (preserves value instead of blanking out to N/A).
        /// </summary>
        public static TimeSpan BatteryLastGoodRetentionDuration { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Exponential backoff schedule for transient battery query failures.
        /// Schedule: 30s -> 2m -> 5m (capped at 5m).
        /// </summary>
        public static TimeSpan GetTransientBackoff(int failureCount)
        {
            switch (failureCount)
            {
                case 1: return TimeSpan.FromSeconds(30);
                case 2: return TimeSpan.FromMinutes(2);
                default: return TimeSpan.FromMinutes(5);
            }
        }

        /// <summary>
        /// Extracts Bluetooth MAC address (ulong) from Windows WinRT BLE DeviceInformation.Id string.
        /// </summary>
        public static ulong TryExtractAddressFromBleDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return 0;
            try
            {
                int idx = deviceId.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string sub = deviceId.Substring(idx + "BluetoothLE".Length);
                    if (sub.StartsWith("#BluetoothLE", StringComparison.OrdinalIgnoreCase))
                    {
                        sub = sub.Substring("#BluetoothLE".Length);
                    }
                    int endIdx = sub.IndexOfAny(new[] { '-', '_', '#' });
                    if (endIdx > 0) sub = sub.Substring(0, endIdx);
                    sub = sub.Replace(":", "").Replace("-", "").Trim();
                    if (ulong.TryParse(sub, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                    {
                        return addr;
                    }
                }
            }
            catch { }
            return 0;
        }

        #endregion

        private readonly HashSet<ulong> _inFlightBatteryQueries = new HashSet<ulong>();
        private readonly object _batteryRetryLock = new object();
        private readonly ConcurrentDictionary<ulong, BatteryCacheEntry> _batteryCache =
            new ConcurrentDictionary<ulong, BatteryCacheEntry>();

        internal ConcurrentDictionary<ulong, BatteryCacheEntry> BatteryCache => _batteryCache;
        internal HashSet<ulong> InFlightBatteryQueries => _inFlightBatteryQueries;

        private void HandleDeviceDisconnected(ulong address)
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

        private static readonly ConcurrentDictionary<string, string> _pnpInstanceIdCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Holds open BluetoothLEDevice handles for actively connected BLE devices so Windows keeps the
        // BLE ACL link alive. The OS drops the link once all process-side handles are released.
        // Populated by ConnectBleDeviceAsync on success; removed and disposed by DisconnectBleDeviceAsync,
        // SetAdapterStateAsync(false), and Dispose(). Read (but never disposed) by GetPairedDevicesAsync,
        // IsDeviceConnectedAsync, and QueryBatteryViaGattAsync.
        private readonly ConcurrentDictionary<ulong, BluetoothLEDevice> _connectedBleDevices =
            new ConcurrentDictionary<ulong, BluetoothLEDevice>();
        private readonly ConcurrentDictionary<ulong, GattSession> _connectedBleSessions =
            new ConcurrentDictionary<ulong, GattSession>();

        private volatile Radio _bluetoothRadio;
        private volatile bool _radioDiscoveryAttempted;
        private DeviceWatcher _classicWatcher;
        private DeviceWatcher _bleWatcher;
        private volatile bool _classicEnumerationComplete;
        private volatile bool _bleEnumerationComplete;
        private readonly object _watcherDebounceLock = new object();
        private System.Threading.Timer _watcherDebounceTimer;
        private bool _disposed;
        private volatile bool _userRequestedAdapterOff;
        private readonly object _deviceOpCtsLock = new object();
        private CancellationTokenSource _deviceOpCts = new CancellationTokenSource();

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
                            Log(LogLevel.Debug, "DeviceWatcher: Debounce elapsed (500ms). Raising DeviceConnectionChanged.");
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

        /// <summary>
        /// Indicates whether the user explicitly requested the adapter to remain OFF.
        /// When true, radio bounce completion never powers the adapter back on.
        /// </summary>
        public bool UserRequestedAdapterOff
        {
            get => _userRequestedAdapterOff;
            set => _userRequestedAdapterOff = value;
        }

        /// <summary>
        /// Cancels in-flight device operations (connect, disconnect, radio bounce) and optionally marks
        /// that the user explicitly requested the adapter to remain OFF.
        /// </summary>
        public void CancelDeviceOperations(bool userRequestedAdapterOff = false)
        {
            if (userRequestedAdapterOff)
            {
                _userRequestedAdapterOff = true;
            }
            lock (_deviceOpCtsLock)
            {
                try
                {
                    _deviceOpCts.Cancel();
                }
                catch { }
                _deviceOpCts = new CancellationTokenSource();
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

        /// <summary>
        /// Removes all entries from _connectedBleDevices and _connectedBleSessions and disposes each stored handle/session,
        /// causing Windows to drop any BLE ACL links this process was keeping alive. Called on adapter-off and Dispose().
        /// </summary>
        private void ReleaseAllConnectedBleHandles(string reason)
        {
            var sessionKeys = _connectedBleSessions.Keys.ToArray();
            foreach (var addr in sessionKeys)
            {
                if (_connectedBleSessions.TryRemove(addr, out var session))
                {
                    try { session.MaintainConnection = false; session.Dispose(); } catch { }
                    Log($"  ReleaseAllConnectedBleHandles ({reason}): released GattSession for {addr:X12}");
                }
            }

            var keys = _connectedBleDevices.Keys.ToArray();
            foreach (var addr in keys)
            {
                if (_connectedBleDevices.TryRemove(addr, out var dev))
                {
                    try { dev?.Dispose(); } catch { }
                    Log($"  ReleaseAllConnectedBleHandles ({reason}): released handle for {addr:X12}");
                }
            }
        }

        /// <summary>
        /// Removes and disposes the tracked BLE handle and GattSession for a single address (called on disconnect).
        /// </summary>
        private void ReleaseConnectedBleHandle(ulong address)
        {
            if (_connectedBleSessions.TryRemove(address, out var session))
            {
                try { session.MaintainConnection = false; session.Dispose(); } catch { }
                Log($"  ReleaseConnectedBleHandle: released GattSession for {address:X12}");
            }

            if (_connectedBleDevices.TryRemove(address, out var dev))
            {
                try { dev?.Dispose(); } catch { }
                Log($"  ReleaseConnectedBleHandle: released handle for {address:X12}");
            }
        }

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
        /// Fired when an in-flight device operation updates its status message (e.g. during a radio bounce).
        /// </summary>
        public event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;

        /// <summary>
        /// True when a controlled radio bounce (Fix 1A/3A) is actively cycling the Bluetooth adapter.
        /// Allows the UI to avoid clearing device cards or misinterpreting the brief adapter off state.
        /// </summary>
        public bool IsRadioBounceInProgress { get; private set; }

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
                    if (ex.InnerException is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 5)
                    {
                        Log(LogLevel.Warning, "Elevation required: Win32 error 5 (Access Denied) when starting bthserv. Run as Administrator if service operations are needed.");
                    }
                    else
                    {
                        Log($"bthserv check note: {ex.Message}");
                    }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds and initializes the Bluetooth Radio object.
        /// Acquires _radioInitSemaphore using double-checked locking around _bluetoothRadio so concurrent callers
        /// at startup initialize and subscribe BluetoothRadio_StateChanged exactly once.
        /// Subsequent calls once _bluetoothRadio is non-null and valid are completely lock-free.
        /// If discovery was previously attempted and found no radio, returns null without re-acquiring the semaphore
        /// unless forceRefresh is true or the cached radio is in Unknown state.
        /// </summary>
        private async Task<Radio> GetBluetoothRadioAsync(CancellationToken ct = default, bool forceRefresh = false)
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
                        if (radio != null && radio.State != RadioState.Unknown)
                        {
                            _bluetoothRadio = radio;
                            _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                            Log($"Found Bluetooth Radio via Adapter: '{radio.Name}', State={radio.State}");
                            if (wasAbsent)
                            {
                                bool isEnabled = (radio.State == RadioState.On);
                                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
                            }
                            return _bluetoothRadio;
                        }
                        else
                        {
                            Log(radio == null ? "adapter.GetRadioAsync() returned null" : $"adapter.GetRadioAsync() returned radio with State={radio.State}");
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
                            if (radio.Kind == RadioKind.Bluetooth && radio.State != RadioState.Unknown)
                            {
                                _bluetoothRadio = radio;
                                _bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                                Log($"Found Bluetooth Radio via GetRadiosAsync: '{radio.Name}', State={radio.State}");
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
            finally
            {
                SafeRelease(_radioInitSemaphore);
            }
        }

        private void BluetoothRadio_StateChanged(Radio sender, object args)
        {
            try
            {
                Log($"BluetoothRadio_StateChanged event received. State={sender.State}");
                if (sender.State == RadioState.Unknown)
                {
                    Log("BluetoothRadio_StateChanged: Radio state is Unknown (adapter unplugged or reset). Invalidating cached radio.");
                    try { sender.StateChanged -= BluetoothRadio_StateChanged; } catch { }
                    _bluetoothRadio = null;
                    _radioDiscoveryAttempted = false;

                    // Release open BLE handles and purge state on external adapter removal (Fix Issue #7)
                    ReleaseAllConnectedBleHandles("adapter removed");
                    _batteryCache.Clear();
                    lock (_batteryRetryLock) { _inFlightBatteryQueries.Clear(); }
                    lock (_deviceOpCtsLock)
                    {
                        try { _deviceOpCts.Cancel(); } catch { }
                        _deviceOpCts = new CancellationTokenSource();
                    }

                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(null, "No Adapter"));
                    return;
                }

                bool isEnabled = (sender.State == RadioState.On);
                Log($"BluetoothRadio_StateChanged: isEnabled={isEnabled}");

                if (!isEnabled)
                {
                    // Release open BLE handles and reset retry state on external radio power-off (Fix Issue #7)
                    ReleaseAllConnectedBleHandles("external radio off");
                    lock (_batteryRetryLock) { _inFlightBatteryQueries.Clear(); }
                    foreach (var kvp in _batteryCache) { kvp.Value.FailureCount = 0; }
                    lock (_deviceOpCtsLock)
                    {
                        try { _deviceOpCts.Cancel(); } catch { }
                        _deviceOpCts = new CancellationTokenSource();
                    }
                }

                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(isEnabled));
            }
            catch (Exception ex)
            {
                Log($"BluetoothRadio_StateChanged exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if the Bluetooth adapter is on, false if off, null if no adapter.
        /// Reads _bluetoothRadio.State directly without acquiring any locks after initial startup.
        /// If cached radio is missing or in Unknown state, attempts a forced rediscovery.
        /// </summary>
        public async Task<bool?> GetAdapterStateAsync(CancellationToken ct = default)
        {
            Log("GetAdapterStateAsync called");
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
                    Log($"GetAdapterStateAsync: {isEnabled} (State={radio.State})");
                    return isEnabled;
                }

                Log("GetAdapterStateAsync: null (no radio found)");
                return null;
            }
            catch (Exception ex)
            {
                Log($"GetAdapterStateAsync exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Turns the Bluetooth adapter on or off.
        /// Returns true if successful, false otherwise.
        /// Guarded by _adapterToggleSemaphore.
        /// </summary>
        public async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default)
        {
            return await SetAdapterStateAsync(enable, ct, isUserInitiated: true).ConfigureAwait(false);
        }

        internal async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct, bool isUserInitiated)
        {
            Log($"SetAdapterStateAsync(enable={enable}, isUserInitiated={isUserInitiated}) called");
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

                    // When adapter is being turned OFF by the user (even if during an internal radio bounce),
                    // cancel any in-flight connect or disconnect operations immediately.
                    // Replace under lock without disposing old instance to prevent ObjectDisposedException
                    // in concurrent callers using linked tokens.
                    lock (_deviceOpCtsLock)
                    {
                        try
                        {
                            _deviceOpCts.Cancel();
                        }
                        catch { }
                        _deviceOpCts = new CancellationTokenSource();
                    }

                    // If a radio bounce is currently in progress, wait for it to unwind before toggling hardware
                    if (IsRadioBounceInProgress)
                    {
                        Log("SetAdapterStateAsync(OFF): Radio bounce is in progress. Cancelling bounce and awaiting unwind...");
                        var swUnwind = System.Diagnostics.Stopwatch.StartNew();
                        while (IsRadioBounceInProgress && swUnwind.ElapsedMilliseconds < 5000)
                        {
                            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                        }
                        Log($"SetAdapterStateAsync(OFF): Bounce unwind wait finished after {swUnwind.ElapsedMilliseconds}ms (IsRadioBounceInProgress={IsRadioBounceInProgress}).");
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
                Log("SetAdapterStateAsync: Radio is null or Unknown, cannot toggle");
                return false;
            }

            // If the user requested OFF and the radio is already in Off state (e.g. powered off by the bounce's finally block),
            // ensure state is synchronized and return success immediately.
            if (!enable && radio.State == RadioState.Off)
            {
                Log("SetAdapterStateAsync(OFF): Radio is already in Off state. Syncing UI event.");
                AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(false));
                return true;
            }

            await _adapterToggleSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RadioState targetState = enable ? RadioState.On : RadioState.Off;
                Log($"Calling radio.SetStateAsync({targetState})...");

                var result = await radio.SetStateAsync(targetState).AsTask(ct).ConfigureAwait(false);
                bool success = (result == RadioAccessStatus.Allowed);
                Log($"radio.SetStateAsync returned: {result}, success={success}");

                if (success)
                {
                    if (!enable && isUserInitiated)
                    {
                        // Commit destructive side effects only after the hardware off operation succeeded (Fix Issue #8)
                        ReleaseAllConnectedBleHandles("adapter off");
                        lock (_batteryRetryLock)
                        {
                            _inFlightBatteryQueries.Clear();
                        }
                        foreach (var kvp in _batteryCache)
                        {
                            kvp.Value.FailureCount = 0;
                        }
                    }
                }
                else
                {
                    if (!enable && isUserInitiated)
                    {
                        // Revert requested state if hardware call was rejected
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
                Log($"SetAdapterStateAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                SafeRelease(_adapterToggleSemaphore);
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all paired Bluetooth (Classic and BLE) devices with their connection status.
        /// Battery is queried via PnP property or BLE GATT fallback for connected devices.
        /// Returns an empty list if the adapter is off, no devices are paired, or any error occurs.
        /// Completely lock-free so background refreshes never block user clicks or device operations.
        /// </summary>
        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            Log("GetPairedDevicesAsync called");
            if (IsRadioBounceInProgress)
            {
                Log("GetPairedDevicesAsync: radio bounce in progress; suppressing device refresh (Fix Issue #6)");
                return null;
            }

            // Guard: check adapter state.
            // Fast lock-free path: if radio is already cached, read state directly without
            // awaiting or acquiring any initialization semaphore (Bug 12 fix).
            var radio = _bluetoothRadio;
            bool? state = (radio != null && radio.State != RadioState.Unknown)
                ? (radio.State == RadioState.On)
                : await GetAdapterStateAsync(ct).ConfigureAwait(false);

            if (state != true)
            {
                Log("GetPairedDevicesAsync: adapter is off or absent, returning empty list");
                return new List<DeviceInfo>();
            }

            try
            {
                // Synchronous and WinRT calls run on the thread pool to avoid freezing the UI thread (Rule A-2).
                var results = await Task.Run(async () =>
                {
                    var list = new List<DeviceInfo>();
                    var disconnectedClassicAddresses = new HashSet<ulong>();

                    // Pre-scan paired BLE collection to identify dual-mode vs classic-only devices
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
                                ulong parsedAddr = TryExtractAddressFromBleDeviceId(devInfo.Id);
                                if (parsedAddr != 0)
                                    pairedBleAddresses.Add(parsedAddr);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GetPairedDevicesAsync: Pre-scan BLE collection note: {ex.Message}");
                    }

                    // 1. Enumerate Classic Bluetooth paired devices
                    try
                    {
                        using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            Log($"GetPairedDevicesAsync: BluetoothClient.PairedDevices returned {paired?.Count() ?? 0} Classic device(s)");
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

                                    if (!isConnected)
                                    {
                                        // Do not purge _batteryCache or in-flight state inside the Classic loop
                                        // because the device might be connected via BLE (dual-mode).
                                        // Collect the disconnected address to process only after BLE enumeration finishes.
                                        disconnectedClassicAddresses.Add(address);
                                    }
                                    else
                                    {
                                        bool isBlePaired = (address != 0 && pairedBleAddresses.Contains(address)) ||
                                                           (!string.IsNullOrEmpty(name) && pairedBleNames.Contains(name));

                                        // Pass 1: PnP device property (fastest, no BLE connection needed)
                                        // Uses the Bluetooth address to locate the HFP/BLE PnP node across enumerators
                                        string addrHex = address.ToString("X12");
                                        battery = QueryBatteryViaPnp(addrHex);

                                        var cacheEntry = _batteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });

                                        if (battery != null)
                                        {
                                            cacheEntry.RecordSuccess(battery.Value, name);
                                        }
                                        else
                                        {
                                            battery = cacheEntry.GetEffectiveBattery();

                                            if (!isBlePaired)
                                            {
                                                // Skip GATT entirely for devices that are classic-only!
                                                if (cacheEntry.Outcome != BatteryQueryOutcome.Unsupported)
                                                {
                                                    Log($"  Battery GATT: Skipped for '{name}' ({address:X12}) — device is Classic-only (no BLE pairing). Caching as Unsupported.");
                                                    cacheEntry.RecordUnsupported(name, isClassicOnly: true);
                                                }
                                            }
                                            else
                                            {
                                                // Pass 2: Cached battery or asynchronous background GATT hydration (Fix Issue #20)
                                                if (battery == null || cacheEntry.ShouldRetry())
                                                {
                                                    ScheduleDeferredBatteryQuery(address, name, isClassicOnly: false, immediate: true);
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
                                                ? string.Join(", ", installed.Select(GetProfileName))
                                                : "empty";
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        installedProfilesStr = $"Error: {ex.Message}";
                                    }

                                    Log($"  Paired Classic device: '{name}' ({address:X12}), Connected={isConnected}, Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}, InstalledServices count={installedCount} [{installedProfilesStr}]");
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
                        Log($"GetPairedDevicesAsync Classic enumeration exception: {ex.Message}");
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
                            Log($"GetPairedDevicesAsync: Found {bleCollection.Count} paired BLE device(s)");
                            foreach (var devInfo in bleCollection)
                            {
                                ct.ThrowIfCancellationRequested();

                                BluetoothLEDevice bleDevice = null;
                                bool isCachedHandle = false;
                                try
                                {
                                    using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                                    {
                                        bleDevice = await BluetoothLEDevice.FromIdAsync(devInfo.Id)
                                            .AsTask(linkedCts.Token).ConfigureAwait(false);
                                    }

                                    if (bleDevice == null)
                                    {
                                        Log($"GetPairedDevicesAsync: FromIdAsync returned null for '{devInfo.Name}' ({devInfo.Id})");
                                        continue;
                                    }

                                    ulong address = bleDevice.BluetoothAddress;
                                    if (address == 0)
                                    {
                                        Log($"GetPairedDevicesAsync: BLE device '{bleDevice.Name}' address was 0; skipping");
                                        continue;
                                    }

                                    // If we already have a live tracked handle for this address, prefer it and
                                    // discard the freshly-created temporary one (safe to dispose since tracked
                                    // handle is a separate WinRT RCW keeping the link open independently).
                                    if (_connectedBleDevices.TryGetValue(address, out var cachedHandle))
                                    {
                                        try { bleDevice.Dispose(); } catch { }
                                        bleDevice = cachedHandle;
                                        isCachedHandle = true;
                                    }

                                    string name = !string.IsNullOrWhiteSpace(bleDevice.Name)
                                        ? bleDevice.Name
                                        : (!string.IsNullOrWhiteSpace(devInfo.Name) ? devInfo.Name : "Unknown BLE Device");

                                    bool isConnected = (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                    var deviceType = ClassifyBleDevice(bleDevice);

                                    int existingIndex = list.FindIndex(d => d.Address == address);
                                    if (existingIndex >= 0)
                                    {
                                        // Device was already discovered via Classic Bluetooth (dual-mode device)
                                        var existing = list[existingIndex];
                                        bool mergedConnected = existing.IsConnected || isConnected;
                                        string mergedName = (existing.Name == "Unknown Device" && name != "Unknown BLE Device") ? name : existing.Name;
                                        var mergedType = (existing.DeviceType == BluetoothDeviceType.Unknown && deviceType != BluetoothDeviceType.Unknown) ? deviceType : existing.DeviceType;
                                        int? mergedBattery = existing.BatteryPercent;

                                        if (mergedConnected && mergedBattery == null)
                                        {
                                            // Pass 1: PnP device property (fastest, no BLE connection needed)
                                            string addrHex = address.ToString("X12");
                                            mergedBattery = QueryBatteryViaPnp(addrHex);

                                            var cacheEntry = _batteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });
                                            if (mergedBattery != null)
                                            {
                                                cacheEntry.RecordSuccess(mergedBattery.Value, mergedName);
                                            }
                                            else
                                            {
                                                // Pass 2: Cached battery or asynchronous background GATT hydration
                                                mergedBattery = cacheEntry.GetEffectiveBattery();
                                                if (mergedBattery == null || cacheEntry.ShouldRetry())
                                                {
                                                    ScheduleDeferredBatteryQuery(address, mergedName, isClassicOnly: false, immediate: true);
                                                }
                                            }
                                        }

                                        Log($"  Paired BLE device: '{mergedName}' ({address:X12}) merged with Classic entry. Connected={mergedConnected}, Type={mergedType}, Battery={mergedBattery?.ToString() ?? "N/A"}");
                                        list[existingIndex] = new DeviceInfo(mergedName, mergedBattery, mergedType, mergedConnected, address);
                                    }
                                    else
                                    {
                                        // Pure BLE device
                                        int? battery = null;
                                        if (!isConnected)
                                        {
                                            HandleDeviceDisconnected(address);
                                        }
                                        else
                                        {
                                            string addrHex = address.ToString("X12");
                                            battery = QueryBatteryViaPnp(addrHex);

                                            var cacheEntry = _batteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });
                                            if (battery != null)
                                            {
                                                cacheEntry.RecordSuccess(battery.Value, name);
                                            }
                                            else
                                            {
                                                // Pass 2: Cached battery or asynchronous background GATT hydration
                                                battery = cacheEntry.GetEffectiveBattery();
                                                if (battery == null || cacheEntry.ShouldRetry())
                                                {
                                                    ScheduleDeferredBatteryQuery(address, name, isClassicOnly: false, immediate: true);
                                                }
                                            }
                                        }

                                        Log($"  Paired BLE device (Pure): '{name}' ({address:X12}), Connected={isConnected}, Type={deviceType}, Battery={battery?.ToString() ?? "N/A"}");
                                        list.Add(new DeviceInfo(name, battery, deviceType, isConnected, address));
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    Log($"GetPairedDevicesAsync: BLE entry processing exception for '{devInfo.Name}': {ex.Message}");
                                }
                                finally
                                {
                                    // Only dispose the temporary handle. Never dispose a cached handle —
                                    // disposing it would release the OS's reference count and drop the BLE link.
                                    if (!isCachedHandle)
                                    {
                                        try { bleDevice?.Dispose(); } catch { }
                                    }
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
                        Log($"GetPairedDevicesAsync BLE enumeration exception: {ex.Message}");
                    }

                    // Reset retry state for devices whose final merged state is disconnected
                    foreach (var addr in disconnectedClassicAddresses)
                    {
                        var dev = list.FirstOrDefault(d => d.Address == addr);
                        if (dev != null && !dev.IsConnected)
                        {
                            HandleDeviceDisconnected(addr);
                        }
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
        }

        /// <summary>
        /// Queries the current connection state of a specific paired Bluetooth device (Classic or BLE).
        /// Returns true if connected, false if disconnected, or null if the device or adapter is not found.
        /// Completely non-blocking and safe to call from UI event handlers to avoid acting on stale cached states.
        /// </summary>
        public async Task<bool?> IsDeviceConnectedAsync(ulong address, CancellationToken ct = default)
        {
            Log($"IsDeviceConnectedAsync called for {address:X12}");
            try
            {
                var radio = _bluetoothRadio;
                bool? adapterState = (radio != null && radio.State != RadioState.Unknown)
                    ? (radio.State == RadioState.On)
                    : await GetAdapterStateAsync(ct).ConfigureAwait(false);
                if (adapterState != true) return null;

                return await Task.Run(async () =>
                {
                    try
                    {
                        // 1. Check Classic Bluetooth
                        bool classicFound = false;
                        using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                        {
                            var paired = client.PairedDevices;
                            if (paired != null)
                            {
                                var target = paired.FirstOrDefault(d =>
                                {
                                    try { return d.DeviceAddress.ToUInt64() == address; }
                                    catch { return false; }
                                });

                                if (target != null)
                                {
                                    classicFound = true;
                                    try { target.Refresh(); } catch { }
                                    if (target.Connected)
                                    {
                                        return (bool?)true;
                                    }
                                }
                            }
                        }

                        // 2. Check Bluetooth LE (for pure BLE devices or dual-mode connected over BLE)
                        // Fast path: if we have an open tracked handle, read ConnectionStatus directly — no allocation.
                        if (_connectedBleDevices.TryGetValue(address, out var trackedDev))
                        {
                            bool isBleConnected = (trackedDev.ConnectionStatus == BluetoothConnectionStatus.Connected);
                            Log($"IsDeviceConnectedAsync: BLE device {address:X12} found in _connectedBleDevices, ConnectionStatus={trackedDev.ConnectionStatus}");
                            return (bool?)isBleConnected;
                        }

                        BluetoothLEDevice bleDevice = null;
                        try
                        {
                            using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lookupCts.Token))
                            {
                                bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                    .AsTask(linkedCts.Token).ConfigureAwait(false);

                                if (bleDevice != null)
                                {
                                    bool isBleConnected = (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                    Log($"IsDeviceConnectedAsync: BLE device {address:X12} found, ConnectionStatus={bleDevice.ConnectionStatus}");
                                    return (bool?)isBleConnected;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            Log($"IsDeviceConnectedAsync: BLE address lookup timed out for {address:X12}");
                        }
                        catch (Exception ex)
                        {
                            Log($"IsDeviceConnectedAsync: BLE address lookup exception for {address:X12}: {ex.Message}");
                        }
                        finally
                        {
                            // Safe to dispose: this is a temporary handle, not the tracked one.
                            try { bleDevice?.Dispose(); } catch { }
                        }

                        // If found in Classic (and target.Connected was false), return false
                        if (classicFound)
                        {
                            return (bool?)false;
                        }

                        Log($"IsDeviceConnectedAsync: device {address:X12} not found in Classic or BLE");
                        return (bool?)null;
                    }
                    catch (Exception ex)
                    {
                        Log($"IsDeviceConnectedAsync exception for {address:X12}: {ex.Message}");
                        return (bool?)null;
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"IsDeviceConnectedAsync outer exception for {address:X12}: {ex.Message}");
                return null;
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
        /// Requests uncached RFCOMM services for the device with a 2-second timeout.
        /// Forces Windows to open an active ACL baseband radio link to the paired device.
        /// Properly cancels in-flight WinRT operations and disposes the BluetoothDevice handle on timeout or cancellation.
        /// </summary>
        private static async Task WakeBasebandLinkAsync(ulong address, CancellationToken ct)
        {
            BluetoothDevice winRtDevice = null;
            try
            {
                Log($"  Waking baseband link for {address:X12} via WinRT uncached RFCOMM discovery...");
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    winRtDevice = await BluetoothDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);

                    if (winRtDevice != null)
                    {
                        var rfcommResult = await winRtDevice.GetRfcommServicesAsync(BluetoothCacheMode.Uncached)
                            .AsTask(linkedCts.Token).ConfigureAwait(false);
                        try
                        {
                            Log($"  WinRT RFCOMM wake completed. Found {rfcommResult?.Services?.Count ?? 0} service(s), Error={rfcommResult?.Error}");
                        }
                        finally
                        {
                            if (rfcommResult?.Services != null)
                            {
                                foreach (var svc in rfcommResult.Services)
                                {
                                    try { svc.Dispose(); } catch { }
                                }
                            }
                        }
                    }
                    else
                    {
                        Log($"  WinRT BluetoothDevice was null for address {address:X12}.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("  WinRT baseband link wake timed out (2s) or was cancelled; proceeding with connection.");
            }
            catch (Exception ex)
            {
                Log($"  WinRT baseband link wake exception: {ex.Message}; proceeding with connection.");
            }
            finally
            {
                try { winRtDevice?.Dispose(); } catch { }
            }
        }

        private static IConnector AsConnector(object obj)
        {
            if (obj == null) return null;
            if (obj is IConnector conn) return conn;

            IntPtr pUnk = IntPtr.Zero;
            IntPtr pConn = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(obj);
                Guid iid = typeof(IConnector).GUID;
                if (Marshal.QueryInterface(pUnk, ref iid, out pConn) == 0 && pConn != IntPtr.Zero)
                {
                    return (IConnector)Marshal.GetObjectForIUnknown(pConn);
                }
            }
            catch { }
            finally
            {
                if (pConn != IntPtr.Zero) Marshal.Release(pConn);
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
            return null;
        }

        private static IPart AsPart(object obj)
        {
            if (obj == null) return null;
            if (obj is IPart part) return part;

            IntPtr pUnk = IntPtr.Zero;
            IntPtr pPart = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(obj);
                Guid iid = typeof(IPart).GUID;
                if (Marshal.QueryInterface(pUnk, ref iid, out pPart) == 0 && pPart != IntPtr.Zero)
                {
                    return (IPart)Marshal.GetObjectForIUnknown(pPart);
                }
            }
            catch { }
            finally
            {
                if (pPart != IntPtr.Zero) Marshal.Release(pPart);
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
            return null;
        }

        /// <summary>
        /// Attempts instant reconnection or disconnection for audio endpoints via Windows Core Audio (IKsControl)
        /// using KSPROPSETID_BtAudio and KSPROPERTY_ONESHOT_RECONNECT / KSPROPERTY_ONESHOT_DISCONNECT.
        /// Uses dedicated watchdog thread and circuit breaker to guard against COM blocking.
        /// </summary>
        internal static bool TrySendCoreAudioBtCommand(ulong address, string deviceName, bool connect)
        {
            if (address == 0) return false;

            // Circuit breaker check
            if (_coreAudioCircuitBreakerOpen)
            {
                Log("  CoreAudio: Circuit breaker is OPEN (skipping Core Audio for this session due to repeated timeouts).");
                return false;
            }

            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                Log($"  CoreAudio: Attempting {(connect ? "reconnect" : "disconnect")} via IKsControl for {address:X12} ('{deviceName}')...");
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator == null) return false;

                int hr = enumerator.EnumAudioEndpoints(EDataFlow_eAll, DEVICE_STATEMASK_ALL, out collection);
                if (hr != 0 || collection == null)
                {
                    Log($"  CoreAudio: EnumAudioEndpoints failed with hr=0x{hr:X8}");
                    return false;
                }

                uint count = 0;
                collection.GetCount(out count);
                Log($"  CoreAudio: Found {count} audio endpoint(s)");

                string addrHex12 = address.ToString("X12").ToUpperInvariant();
                bool anyTriggered = false;

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice endpoint = null;
                    try
                    {
                        int hrItem = collection.Item(i, out endpoint);
                        if (hrItem != 0 || endpoint == null) continue;

                        bool matchedByAddress = false;
                        bool matchedByName = false;

                        // 1. Check ID string
                        string endpointId;
                        if (endpoint.GetId(out endpointId) == 0 && !string.IsNullOrEmpty(endpointId))
                        {
                            string idUpper = endpointId.ToUpperInvariant();
                            if (idUpper.Contains(addrHex12))
                            {
                                matchedByAddress = true;
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
                                    try
                                    {
                                        if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                                        {
                                            string btAddrProp = Marshal.PtrToStringUni(pv.pwszVal);
                                            if (!string.IsNullOrEmpty(btAddrProp))
                                            {
                                                string clean = btAddrProp.Replace(":", "").Replace("-", "").ToUpperInvariant();
                                                if (clean.Contains(addrHex12))
                                                {
                                                    matchedByAddress = true;
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        NativeMethods.PropVariantClear(ref pv);
                                    }
                                }

                                if (!matchedByAddress && !string.IsNullOrWhiteSpace(deviceName))
                                {
                                    var keyFriendlyName = new PROPERTYKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
                                    if (propStore.GetValue(ref keyFriendlyName, out pv) == 0)
                                    {
                                        try
                                        {
                                            if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                                            {
                                                string fn = Marshal.PtrToStringUni(pv.pwszVal);
                                                if (!string.IsNullOrEmpty(fn))
                                                {
                                                    if (string.Equals(fn, deviceName, StringComparison.OrdinalIgnoreCase) ||
                                                        fn.IndexOf($"({deviceName})", StringComparison.OrdinalIgnoreCase) >= 0)
                                                    {
                                                        matchedByName = true;
                                                    }
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            NativeMethods.PropVariantClear(ref pv);
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (propStore != null) Marshal.ReleaseComObject(propStore);
                        }

                        if (!matchedByAddress && !matchedByName) continue;

                        Log($"  CoreAudio: Endpoint matched: '{endpointId}' (byAddress={matchedByAddress}, byName={matchedByName})");

                        // Get IDeviceTopology
                        Guid iidTopology = typeof(IDeviceTopology).GUID;
                        object topologyObj = null;
                        int hrTopology = endpoint.Activate(ref iidTopology, CLSCTX_ALL, IntPtr.Zero, out topologyObj);
                        Log($"  CoreAudio: endpoint.Activate(IDeviceTopology) returned hr=0x{hrTopology:X8}");
                        if (hrTopology != 0 || topologyObj == null)
                            continue;

                        IDeviceTopology endpointTopology = topologyObj as IDeviceTopology;
                        if (endpointTopology == null)
                        {
                            Marshal.ReleaseComObject(topologyObj);
                            continue;
                        }

                        try
                        {
                            uint connectorCount = 0;
                            int hrConnCount = endpointTopology.GetConnectorCount(out connectorCount);
                            Log($"  CoreAudio: endpointTopology.GetConnectorCount returned hr=0x{hrConnCount:X8}, count={connectorCount}");
                            if (hrConnCount != 0 || connectorCount == 0)
                            {
                                Log($"  CoreAudio: Endpoint '{endpointId}' has 0 connectors; skipping.");
                                continue;
                            }

                            object endpointConnectorObj = null;
                            int hrGetConn = endpointTopology.GetConnector(0, out endpointConnectorObj);
                            Log($"  CoreAudio: endpointTopology.GetConnector(0) returned hr=0x{hrGetConn:X8}");
                            if (hrGetConn != 0 || endpointConnectorObj == null)
                            {
                                Log($"  CoreAudio: Endpoint '{endpointId}' GetConnector(0) failed; skipping.");
                                continue;
                            }

                            try
                            {
                                IConnector endpointConnector = AsConnector(endpointConnectorObj);
                                if (endpointConnector == null)
                                {
                                    Log($"  CoreAudio: Endpoint '{endpointId}' connector 0 does not implement IConnector; skipping.");
                                    continue;
                                }

                                object adapterConnectorObj = null;
                                int hrGetConnectedTo = endpointConnector.GetConnectedTo(out adapterConnectorObj);
                                Log($"  CoreAudio: endpointConnector.GetConnectedTo returned hr=0x{hrGetConnectedTo:X8}");
                                if (hrGetConnectedTo != 0 || adapterConnectorObj == null)
                                {
                                    Log($"  CoreAudio: Endpoint '{endpointId}' connector is not connected to an adapter; skipping.");
                                    continue;
                                }

                                try
                                {
                                    IPart adapterPart = AsPart(adapterConnectorObj);
                                    if (adapterPart == null)
                                    {
                                        Log($"  CoreAudio: Connected adapter object does not implement IPart; skipping.");
                                        continue;
                                    }

                                    IDeviceTopology adapterTopology = null;
                                    int hrAdapterTopo = adapterPart.GetTopologyObject(out adapterTopology);
                                    Log($"  CoreAudio: adapterPart.GetTopologyObject returned hr=0x{hrAdapterTopo:X8}");
                                    if (hrAdapterTopo != 0 || adapterTopology == null)
                                    {
                                        Log($"  CoreAudio: GetTopologyObject failed on adapter part; skipping.");
                                        continue;
                                    }

                                    try
                                    {
                                        string adapterDeviceId = null;
                                        int hrDeviceId = adapterTopology.GetDeviceId(out adapterDeviceId);
                                        Log($"  CoreAudio: adapterTopology.GetDeviceId returned hr=0x{hrDeviceId:X8}, adapterDeviceId='{adapterDeviceId}'");
                                        if (hrDeviceId != 0 || string.IsNullOrEmpty(adapterDeviceId))
                                            continue;

                                        // Verify that the adapter device is a Bluetooth audio adapter or matches the device's address
                                        bool isBtAdapter = adapterDeviceId.IndexOf(addrHex12, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                           adapterDeviceId.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                           adapterDeviceId.IndexOf("BLUETOOTH", StringComparison.OrdinalIgnoreCase) >= 0;
                                        if (!isBtAdapter)
                                        {
                                            Log($"  CoreAudio: Skipping non-Bluetooth adapter '{adapterDeviceId}' for endpoint '{endpointId}'");
                                            continue;
                                        }

                                        Guid iidKsControl = typeof(IKsControl).GUID;
                                        object ksControlObj = null;
                                        IKsControl ksControl = null;

                                        // 1. Primary: ALWAYS attempt adapterPart.Activate for IKsControl
                                        int hrPartActivate = adapterPart.Activate(CLSCTX_ALL, ref iidKsControl, out ksControlObj);
                                        Log($"  CoreAudio: adapterPart.Activate(IKsControl) returned hr=0x{hrPartActivate:X8}");
                                        if (hrPartActivate == 0 && ksControlObj != null)
                                        {
                                            ksControl = ksControlObj as IKsControl;
                                            if (ksControl == null)
                                            {
                                                Marshal.ReleaseComObject(ksControlObj);
                                                ksControlObj = null;
                                            }
                                        }

                                        // 2. Optional extra fallback: Attempt GetDevice(adapterDeviceId)
                                        IMMDevice adapterDevice = null;
                                        if (ksControl == null)
                                        {
                                            Log($"  CoreAudio: Attempting optional GetDevice('{adapterDeviceId}')...");
                                            int hrGetDevice = enumerator.GetDevice(adapterDeviceId, out adapterDevice);
                                            Log($"  CoreAudio: enumerator.GetDevice('{adapterDeviceId}') returned hr=0x{hrGetDevice:X8}");
                                            if (hrGetDevice == 0 && adapterDevice != null)
                                            {
                                                int hrDeviceActivate = adapterDevice.Activate(ref iidKsControl, CLSCTX_ALL, IntPtr.Zero, out ksControlObj);
                                                Log($"  CoreAudio: adapterDevice.Activate(IKsControl) returned hr=0x{hrDeviceActivate:X8}");
                                                if (hrDeviceActivate == 0 && ksControlObj != null)
                                                {
                                                    ksControl = ksControlObj as IKsControl;
                                                    if (ksControl == null)
                                                    {
                                                        Marshal.ReleaseComObject(ksControlObj);
                                                        ksControlObj = null;
                                                    }
                                                }
                                            }
                                        }

                                        if (ksControl != null)
                                        {
                                            if (ExecuteKsPropertyWithWatchdog(ksControl, connect, adapterDeviceId))
                                            {
                                                anyTriggered = true;
                                            }
                                        }

                                        if (adapterDevice != null)
                                        {
                                            try { Marshal.ReleaseComObject(adapterDevice); } catch { }
                                        }
                                    }
                                    finally
                                    {
                                        if (adapterTopology != null) Marshal.ReleaseComObject(adapterTopology);
                                    }
                                }
                                finally
                                {
                                    if (adapterConnectorObj != null) Marshal.ReleaseComObject(adapterConnectorObj);
                                }
                            }
                            finally
                            {
                                if (endpointConnectorObj != null) Marshal.ReleaseComObject(endpointConnectorObj);
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

                Log($"  CoreAudio: Completed. anyTriggered={anyTriggered}");
                return anyTriggered;
            }
            catch (Exception ex)
            {
                Log($"  CoreAudio outer exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (collection != null) { try { Marshal.ReleaseComObject(collection); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
        }

        /// <summary>
        /// Executes KsProperty (or test KsPropertyInvoker) on a dedicated background watchdog thread
        /// with timeout and circuit breaker protection.
        /// Transfers ownership of ksControl to the background thread so abandoned threads do not race.
        /// </summary>
        internal static bool ExecuteKsPropertyWithWatchdog(IKsControl ksControl, bool connect, string adapterDeviceId)
        {
            if (_coreAudioCircuitBreakerOpen)
            {
                Log($"  CoreAudio: Circuit breaker is OPEN. Skipping KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}'.");
                if (ksControl != null)
                {
                    try { Marshal.ReleaseComObject(ksControl); } catch { }
                }
                return false;
            }

            var ksProp = new KSIDENTIFIER
            {
                Set = KSPROPSETID_BtAudio,
                Id = connect ? KSPROPERTY_ONESHOT_RECONNECT : KSPROPERTY_ONESHOT_DISCONNECT,
                Flags = KSPROPERTY_TYPE_GET
            };

            int ksHr = -1;
            var doneEvent = new ManualResetEventSlim(false);

            // Dedicated background watchdog thread.
            // Ownership of ksControl is transferred to this thread.
            // If the thread is abandoned upon timeout, the calling thread
            // does NOT touch or release ksControl, preventing races and access violations.
            var workerThread = new Thread(() =>
            {
                try
                {
                    if (KsPropertyInvoker != null)
                    {
                        ksHr = KsPropertyInvoker(ksControl, ksProp);
                    }
                    else if (ksControl != null)
                    {
                        uint bytesRet = 0;
                        ksHr = ksControl.KsProperty(ref ksProp, (uint)Marshal.SizeOf(ksProp), IntPtr.Zero, 0, out bytesRet);
                    }
                }
                catch (Exception ex)
                {
                    Log($"  CoreAudio: KsProperty thread exception: {ex.Message}");
                }
                finally
                {
                    doneEvent.Set();
                    try
                    {
                        if (ksControl != null)
                        {
                            Marshal.ReleaseComObject(ksControl);
                        }
                    }
                    catch { }
                }
            })
            {
                IsBackground = true,
                Name = "CoreAudio-KsProperty-Watchdog"
            };

            workerThread.Start();
            bool timedOut = !doneEvent.Wait(CoreAudioWatchdogTimeoutMs);

            if (timedOut)
            {
                _coreAudioConsecutiveTimeouts++;
                Log($"  CoreAudio: KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}' TIMED OUT after {CoreAudioWatchdogTimeoutMs}ms. Abandoning thread (consecutive={_coreAudioConsecutiveTimeouts}).");
                if (_coreAudioConsecutiveTimeouts >= CoreAudioMaxConsecutiveTimeouts)
                {
                    _coreAudioCircuitBreakerOpen = true;
                    Log($"  CoreAudio: Circuit breaker TRIPPED OPEN after {_coreAudioConsecutiveTimeouts} consecutive timeouts.");
                }
                return false;
            }
            else
            {
                _coreAudioConsecutiveTimeouts = 0;
                Log($"  CoreAudio: KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}' returned hr=0x{ksHr:X8}");
                return ksHr >= 0;
            }
        }

        /// <summary>
        /// Manually resets (bounces) the Bluetooth radio adapter, cycling power OFF and ON.
        /// Persists a bounce marker in the registry to recover if an abnormal termination occurs.
        /// </summary>
        public async Task<bool> ManualResetBluetoothAsync(CancellationToken ct = default)
        {
            Log("ManualResetBluetoothAsync: User initiated manual Bluetooth reset.");
            if (_disposed) return false;
            if (ct.IsCancellationRequested) return false;

            IsRadioBounceInProgress = true;
            SetBounceInProgressMarker(true);
            bool radioTurnedOffByBounce = false;
            OperationProgressChanged?.Invoke(this, new DeviceOperationProgressEventArgs(0, "Resetting Bluetooth adapter..."));

            try
            {
                if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
                {
                    Log("ManualResetBluetoothAsync: Adapter is OFF or absent. Turning adapter ON...");
                    return await SetAdapterStateAsync(true, ct, isUserInitiated: true).ConfigureAwait(false);
                }

                bool offSuccess = await SetAdapterStateAsync(false, ct, isUserInitiated: false).ConfigureAwait(false);
                Log($"ManualResetBluetoothAsync: adapter turned off (success={offSuccess})");
                if (!offSuccess)
                {
                    Log("ManualResetBluetoothAsync: SetAdapterStateAsync(false) failed, aborting reset.");
                    return false;
                }
                radioTurnedOffByBounce = true;

                // Wait 1000ms for hardware power-down transition
                await Task.Delay(1000, ct).ConfigureAwait(false);

                // Retry loop to turn adapter back ON
                bool onSuccess = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (_userRequestedAdapterOff)
                    {
                        Log("ManualResetBluetoothAsync: aborted because user requested adapter OFF.");
                        break;
                    }

                    onSuccess = await SetAdapterStateAsync(true, CancellationToken.None, isUserInitiated: false).ConfigureAwait(false);
                    Log($"ManualResetBluetoothAsync: adapter turned on attempt {attempt}/3 (success={onSuccess})");
                    if (onSuccess) break;
                    await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
                }

                return onSuccess;
            }
            catch (OperationCanceledException)
            {
                Log("ManualResetBluetoothAsync was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Log($"ManualResetBluetoothAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (_userRequestedAdapterOff)
                    {
                        Log("ManualResetBluetoothAsync finally: user requested adapter OFF; ensuring radio is powered OFF in hardware...");
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
                                Log($"ManualResetBluetoothAsync finally: SetStateAsync(Off) returned {res}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"ManualResetBluetoothAsync finally: power off exception: {ex.Message}");
                        }
                    }
                    else if (radioTurnedOffByBounce &&
                        (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On) &&
                        !_disposed)
                    {
                        Log("ManualResetBluetoothAsync finally: radio was left OFF; restoring power...");
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
                        : ((_bluetoothRadio != null && _bluetoothRadio.State != RadioState.Unknown) ? (_bluetoothRadio.State == RadioState.On) : (bool?)null);
                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(finalState));
                }
                catch { }
            }
        }

        /// <summary>
        /// Attempts to connect to a paired Bluetooth device using WinRT baseband link wake,
        /// Core Audio IKsControl reconnect, profile cycling across supported profiles,
        /// and a controlled radio bounce fallback.
        /// Verifies actual connection status before returning. Guarded by _connectSemaphore.
        /// </summary>
        public async Task<bool> ConnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            Log($"ConnectDeviceAsync called for {address:X12}");
            if (_disposed) return false;

            // Guard: Do not attempt to connect if Bluetooth radio is currently off or absent
            if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
            {
                Log($"  ConnectDeviceAsync: Bluetooth adapter is OFF or absent. Aborting connection for {address:X12}.");
                return false;
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
                    Log($"ConnectDeviceAsync: Cancelled while waiting for lock ({address:X12})");
                    return false;
                }

                try
                {
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            string targetDeviceName;
                            bool isBlePaired = false;
                            using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                            {
                                var paired = client.PairedDevices;
                                var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                                if (device == null)
                                {
                                    Log($"  ConnectDeviceAsync: device {address:X12} not found in Classic PairedDevices. Attempting BLE connection...");
                                    return await ConnectBleDeviceAsync(address, token).ConfigureAwait(false);
                                }

                                targetDeviceName = device.DeviceName;

                                // Step 1: Refresh device state before acting
                                device.Refresh();
                                Log($"  device.Refresh() called for '{targetDeviceName}', initial Connected={device.Connected}");
                                if (device.Connected)
                                {
                                    Log($"  '{targetDeviceName}' is already connected.");
                                    return true;
                                }

                                // Dual-mode and BLE peripheral guard:
                                // Some devices (BLE mice, keyboards, controllers, modern headsets) register in Win32
                                // PairedDevices yet connect exclusively over the BLE transport. Running the
                                // Classic RFCOMM wake / CoreAudio / SetServiceState chain on them is wrong and
                                // triggers the unnecessary 15-second Step 6 Radio Bounce.
                                bool bleConnected = false;
                                if (_connectedBleDevices.TryGetValue(address, out var existingBleHandle))
                                {
                                    bleConnected = (existingBleHandle.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                }

                                isBlePaired = false;
                                BluetoothDeviceType bleType = BluetoothDeviceType.Unknown;
                                try
                                {
                                    using (var bleLookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                                    using (var bleLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, bleLookupCts.Token))
                                    using (var tempBle = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                        .AsTask(bleLinkedCts.Token).ConfigureAwait(false))
                                    {
                                        if (tempBle != null)
                                        {
                                            if (tempBle.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                                bleConnected = true;
                                            isBlePaired = (tempBle.DeviceInformation?.Pairing?.IsPaired == true);
                                            bleType = ClassifyBleDevice(tempBle);
                                        }
                                    }
                                }
                                catch { }

                                if (bleConnected)
                                {
                                    Log($"  ConnectDeviceAsync: '{targetDeviceName}' found connected over BLE. Routing to BLE connect path.");
                                    return await ConnectBleDeviceAsync(address, token).ConfigureAwait(false);
                                }

                                var classicType = ClassifyDevice(device);
                                bool isClassicPeripheral = classicType == BluetoothDeviceType.Mouse ||
                                                           classicType == BluetoothDeviceType.Keyboard ||
                                                           classicType == BluetoothDeviceType.Controller;
                                bool isBlePeripheral = bleType == BluetoothDeviceType.Mouse ||
                                                       bleType == BluetoothDeviceType.Keyboard ||
                                                       bleType == BluetoothDeviceType.Controller;
                                bool isPeripheral = isClassicPeripheral || isBlePeripheral;

                                int codMajor = 0;
                                try
                                {
                                    int cod = (int)device.ClassOfDevice.Value;
                                    codMajor = (cod >> 8) & 0x1F;
                                }
                                catch { }

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
                                            ? string.Join(", ", installedServices.Select(GetProfileName))
                                            : "empty";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    installedReadSuccess = false;
                                    installedProfilesStr = $"Read error: {ex.Message}";
                                    Log($"  Reading InstalledServices for '{targetDeviceName}' note: {ex.Message}");
                                }

                                Log($"  [InstalledServices] Device '{targetDeviceName}' ({address:X12}, Connected={device.Connected}): count={installedCount}, profiles=[{installedProfilesStr}]");

                                var capability = ClassifyTransportCapability(isBlePaired, installedReadSuccess, installedServices, codMajor);
                                var route = DetermineConnectionRoute(capability, codMajor, isPeripheral);

                                Log($"  [Routing Decision] Device '{targetDeviceName}' ({address:X12}): Capability={capability}, Route={route}, isBlePaired={isBlePaired}, CoDMajor=0x{codMajor:X2}, isPeripheral={isPeripheral}");

                                if (route == ConnectionRoute.BleDirect)
                                {
                                    Log($"  ConnectDeviceAsync: Routing '{targetDeviceName}' directly to BLE connect path.");
                                    return await ConnectBleDeviceAsync(address, token).ConfigureAwait(false);
                                }

                                if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
                                {
                                    Log($"  ConnectDeviceAsync: Adapter turned off or absent before baseband wake. Aborting.");
                                    return false;
                                }

                                // Step 2: Wake baseband link via WinRT uncached discovery (with 4s timeout)
                                if (EnableStepWake)
                                {
                                    var swWake = System.Diagnostics.Stopwatch.StartNew();
                                    await WakeBasebandLinkAsync(address, token).ConfigureAwait(false);
                                    swWake.Stop();
                                    device.Refresh();
                                    Log($"  [Step 2: Wake] elapsed={swWake.ElapsedMilliseconds}ms, Connected={device.Connected}");
                                    if (device.Connected)
                                    {
                                        Log($"  '{targetDeviceName}' connected after baseband wake in {swWake.ElapsedMilliseconds}ms.");
                                        return true;
                                    }
                                }
                                else
                                {
                                    Log("  [Step 2: Wake] SKIPPED via diagnostic switch.");
                                }

                                if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
                                {
                                    Log($"  ConnectDeviceAsync: Adapter turned off or absent before CoreAudio reconnect. Aborting.");
                                    return false;
                                }

                                // Step 3: Best-effort Windows Core Audio (IKsControl) Reconnect for Audio Endpoints
                                if (EnableStepCoreAudio)
                                {
                                    try
                                    {
                                        var swCore = System.Diagnostics.Stopwatch.StartNew();
                                        bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, targetDeviceName, connect: true);
                                        swCore.Stop();
                                        Log($"  [Step 3: CoreAudio] triggered={coreAudioTriggered}, elapsed={swCore.ElapsedMilliseconds}ms");

                                        if (coreAudioTriggered)
                                        {
                                            Log($"  CoreAudio command sent. Polling connection status (3 attempts x 500ms = 1.5s max)...");
                                            for (int caAttempt = 1; caAttempt <= 3; caAttempt++)
                                            {
                                                await Task.Delay(500, token).ConfigureAwait(false);
                                                try
                                                {
                                                    device.Refresh();
                                                    Log($"  CoreAudio reconnect verify attempt {caAttempt}/5: Connected={device.Connected}");
                                                    if (device.Connected)
                                                    {
                                                        Log($"  '{targetDeviceName}' reconnected successfully via CoreAudio on attempt {caAttempt}/5 in {swCore.ElapsedMilliseconds}ms.");
                                                        return true;
                                                    }
                                                }
                                                catch (OperationCanceledException) { throw; }
                                                catch (Exception ex)
                                                {
                                                    Log($"  CoreAudio verify attempt {caAttempt} exception: {ex.Message}");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"  CoreAudio reconnect attempt note: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Log("  [Step 3: CoreAudio] SKIPPED via diagnostic switch.");
                                }

                                if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On)
                                {
                                    Log($"  ConnectDeviceAsync: Adapter turned off or absent before service state cycle. Aborting.");
                                    return false;
                                }

                                // Step 4: Primary Strategy — Two-pass Service State Cycle (disable all first, wait 200ms, then enable all)
                                if (EnableStepServiceCycle)
                                {
                                    var swCycle = System.Diagnostics.Stopwatch.StartNew();
                                    bool isHidDevice = (route == ConnectionRoute.ClassicWithHid) ||
                                                       classicType == BluetoothDeviceType.Mouse ||
                                                       classicType == BluetoothDeviceType.Keyboard ||
                                                       classicType == BluetoothDeviceType.Controller;

                                    var candidateProfiles = isHidDevice
                                        ? new[] { InTheHand.Net.Bluetooth.BluetoothService.HumanInterfaceDevice }
                                        : new[]
                                        {
                                            InTheHand.Net.Bluetooth.BluetoothService.AudioSink,
                                            InTheHand.Net.Bluetooth.BluetoothService.Handsfree,
                                            InTheHand.Net.Bluetooth.BluetoothService.Headset
                                        };

                                    // Only cycle profiles that are actually installed/supported on this device
                                    var profilesToCycle = new List<Guid>();
                                    if (installedServices != null && installedServices.Count > 0)
                                    {
                                        foreach (var cand in candidateProfiles)
                                        {
                                            if (installedServices.Contains(cand))
                                            {
                                                profilesToCycle.Add(cand);
                                            }
                                        }
                                    }

                                    // Fallback if InstalledServices was empty or not populated (e.g. disabled profile state)
                                    if (profilesToCycle.Count == 0)
                                    {
                                        var fallbackProfile = isHidDevice
                                            ? InTheHand.Net.Bluetooth.BluetoothService.HumanInterfaceDevice
                                            : InTheHand.Net.Bluetooth.BluetoothService.AudioSink;
                                        profilesToCycle.Add(fallbackProfile);
                                        Log($"  Using fallback profile {GetProfileName(fallbackProfile)} for '{targetDeviceName}'");
                                    }

                                    var disabledProfiles = new List<Guid>();
                                    try
                                    {
                                        foreach (var profile in profilesToCycle)
                                        {
                                            if (token.IsCancellationRequested) break;
                                            try
                                            {
                                                RecordPendingProfileRestore(address, profile);
                                                device.SetServiceState(profile, false);
                                                disabledProfiles.Add(profile);
                                                Log($"  SetServiceState({profile}, false) succeeded for '{targetDeviceName}'");
                                            }
                                            catch (Exception ex)
                                            {
                                                ClearPendingProfileRestore(address, profile);
                                                Log($"  SetServiceState({profile}, false) note: {ex.Message}");
                                            }
                                        }

                                        if (disabledProfiles.Count > 0)
                                        {
                                            await Task.Delay(200, token).ConfigureAwait(false);
                                        }
                                    }
                                    finally
                                    {
                                        // CRITICAL DRIVER HYGIENE: Always restore all disabled profile drivers to true,
                                        // even if cancellation was requested (e.g. user toggled Bluetooth off or closed the app)
                                        // or an exception occurred during the delay. Leaving profiles disabled permanently
                                        // unbinds the PnP driver in the Windows registry (Fix Issue #11).
                                        foreach (var profile in disabledProfiles)
                                        {
                                            try
                                            {
                                                device.SetServiceState(profile, true);
                                                Log($"  SetServiceState({profile}, true) restored for '{targetDeviceName}'");
                                            }
                                            catch (Exception ex)
                                            {
                                                Log($"  SetServiceState({profile}, true) cleanup note: {ex.Message}");
                                            }
                                            finally
                                            {
                                                ClearPendingProfileRestore(address, profile);
                                            }
                                        }
                                    }

                                    token.ThrowIfCancellationRequested();

                                    // Step 5: Verify Actual Connection Status Before Returning
                                    // Initial delay of 500ms after SetServiceState(true) before starting the poll loop,
                                    // giving Windows time to initiate the baseband connection
                                    await Task.Delay(500, token).ConfigureAwait(false);

                                    bool connected = false;
                                    for (int attempt = 1; attempt <= 5; attempt++)
                                    {
                                        try
                                        {
                                            if (attempt > 1)
                                            {
                                                await Task.Delay(500, token).ConfigureAwait(false);
                                            }

                                            device.Refresh();
                                            Log($"  Connection verify attempt {attempt}/5: Connected={device.Connected}");
                                            if (device.Connected)
                                            {
                                                connected = true;
                                                break;
                                            }
                                        }
                                        catch (OperationCanceledException) { throw; }
                                        catch (Exception ex)
                                        {
                                            Log($"  Connection verify attempt {attempt} exception: {ex.Message}");
                                        }
                                    }

                                    swCycle.Stop();
                                    Log($"  [Step 4: ServiceCycle] elapsed={swCycle.ElapsedMilliseconds}ms, Connected={connected}");

                                    if (connected)
                                    {
                                        Log($"  '{targetDeviceName}' connected successfully via service state cycle in {swCycle.ElapsedMilliseconds}ms.");
                                        return true;
                                    }
                                }
                                else
                                {
                                    Log("  [Step 4: ServiceCycle] SKIPPED via diagnostic switch.");
                                }
                            }

                            // Fallback for dual-mode devices before radio bounce: try BLE connection
                            if (isBlePaired || _connectedBleDevices.ContainsKey(address))
                            {
                                Log($"  Steps 1-5 failed for dual-mode device '{targetDeviceName}'. Attempting BLE connection fallback before radio bounce...");
                                if (await ConnectBleDeviceAsync(address, token).ConfigureAwait(false))
                                {
                                    Log($"  '{targetDeviceName}' connected successfully via BLE fallback.");
                                    return true;
                                }
                            }

                            // Step 6: Controlled Radio Bounce
                            // ONLY perform radio bounce if adapter is currently ON and operation was NOT canceled.
                            // If the user turned off Bluetooth, DO NOT bounce or turn Bluetooth back on!
                            if (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On || token.IsCancellationRequested || _userRequestedAdapterOff)
                            {
                                Log($"  ConnectDeviceAsync: Adapter is OFF or operation was cancelled. Skipping Step 6 (Radio Bounce).");
                                return false;
                            }

                            // 1. By default, never bounce automatically. Offer manual action instead.
                            if (!AllowAutomaticRadioBounce)
                            {
                                Log($"  ConnectDeviceAsync: Automatic radio bounce is disabled by default. Skipping Step 6 (Radio Bounce). Use 'Reset Bluetooth' to bounce manually.");
                                return false;
                            }

                            // 2. Guard: Even with setting on, skip when any HID device is connected (mouse/keyboard/controller)
                            if (HasConnectedPeripherals(address, out string hidName, out BluetoothDeviceType hidType))
                            {
                                Log($"  ConnectDeviceAsync: Connected input peripheral '{hidName}' ({hidType}) detected. Skipping destructive Step 6 (Radio Bounce) to prevent input disconnect.");
                                return false;
                            }

                            if (!EnableStepBounce)
                            {
                                Log($"  ConnectDeviceAsync: Step 6 (Radio Bounce) SKIPPED via diagnostic switch.");
                                return false;
                            }

                            Log($"  Steps 1-5 failed to connect '{targetDeviceName}'. Initiating Step 6: Controlled Radio Bounce...");
                            var swBounce = System.Diagnostics.Stopwatch.StartNew();
                            IsRadioBounceInProgress = true;
                            SetBounceInProgressMarker(true);
                            // DO NOT reset _userRequestedAdapterOff here (Fix Issue #4/5)
                            bool radioTurnedOffByBounce = false;
                            OperationProgressChanged?.Invoke(this, new DeviceOperationProgressEventArgs(address, "Reconnecting Bluetooth..."));
                            try
                            {
                                bool offSuccess = await SetAdapterStateAsync(false, token, isUserInitiated: false).ConfigureAwait(false);
                                Log($"  Radio bounce: adapter turned off (success={offSuccess})");
                                if (!offSuccess)
                                {
                                    Log("  Radio bounce: SetAdapterStateAsync(false) failed, aborting radio bounce.");
                                    return false;
                                }
                                radioTurnedOffByBounce = true;

                                // Wait 1000ms for USB/PCIe hardware power-down transition
                                await Task.Delay(1000, token).ConfigureAwait(false);

                                if (_userRequestedAdapterOff)
                                {
                                    Log("  Radio bounce: user requested adapter OFF during power-down; aborting radio bounce.");
                                    return false;
                                }

                                // Retry loop to ensure adapter successfully powers back on
                                bool onSuccess = false;
                                for (int onAttempt = 1; onAttempt <= 3; onAttempt++)
                                {
                                    if (_userRequestedAdapterOff)
                                    {
                                        Log("  Radio bounce power-on aborted: user requested adapter OFF.");
                                        break;
                                    }

                                    // Use CancellationToken.None so turning the adapter back on is never aborted if caller token is canceled,
                                    // but respect explicit user adapter OFF request
                                    onSuccess = await SetAdapterStateAsync(true, CancellationToken.None, isUserInitiated: false).ConfigureAwait(false);
                                    Log($"  Radio bounce: adapter turned on attempt {onAttempt}/3 (success={onSuccess}, userRequestedOff={_userRequestedAdapterOff})");
                                    if (_userRequestedAdapterOff)
                                    {
                                        Log("  Radio bounce power-on aborted after attempt: user requested adapter OFF.");
                                        break;
                                    }
                                    if (onSuccess) break;
                                    if (_userRequestedAdapterOff)
                                    {
                                        Log("  Radio bounce power-on aborted before retry delay: user requested adapter OFF.");
                                        break;
                                    }
                                    await Task.Delay(1000 * onAttempt, token).ConfigureAwait(false);
                                }

                                if (_userRequestedAdapterOff)
                                {
                                    Log("  Radio bounce: user requested adapter OFF; skipping recovery & verification.");
                                    return false;
                                }

                                if (!onSuccess)
                                {
                                    Log("  Radio bounce: standard SetAdapterStateAsync(true) failed after 3 attempts. Attempting forced radio refresh...");
                                    var freshRadio = await GetBluetoothRadioAsync(CancellationToken.None, forceRefresh: true).ConfigureAwait(false);
                                    if (freshRadio != null && !_userRequestedAdapterOff)
                                    {
                                        try
                                        {
                                            var res = await freshRadio.SetStateAsync(RadioState.On).AsTask(CancellationToken.None).ConfigureAwait(false);
                                            onSuccess = (res == RadioAccessStatus.Allowed);
                                            Log($"  Radio bounce: direct SetStateAsync(On) returned {res}, success={onSuccess}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"  Radio bounce: direct SetStateAsync(On) exception: {ex.Message}");
                                        }
                                    }
                                }

                                if (!onSuccess && (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On))
                                {
                                    Log("  Radio bounce: CRITICAL — could not restore Bluetooth adapter power! Aborting polling.");
                                    return false;
                                }

                                // Allow 1.5s for Windows Bluetooth stack & PnP to re-enumerate
                                await Task.Delay(1500, token).ConfigureAwait(false);

                                // Poll device.Connected up to 8 times at 1-second intervals (8 seconds total)
                                for (int attempt = 1; attempt <= 8; attempt++)
                                {
                                    if (_userRequestedAdapterOff)
                                    {
                                        Log("  Radio bounce verify aborted: user requested adapter OFF.");
                                        return false;
                                    }

                                    await Task.Delay(1000, token).ConfigureAwait(false);
                                    try
                                    {
                                        using (var bounceClient = new InTheHand.Net.Sockets.BluetoothClient())
                                        {
                                            var bounceDevice = bounceClient.PairedDevices?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                                            if (bounceDevice != null)
                                            {
                                                bounceDevice.Refresh();
                                                Log($"  Radio bounce verify attempt {attempt}/8: Connected={bounceDevice.Connected}");
                                                if (bounceDevice.Connected)
                                                {
                                                    Log($"  '{bounceDevice.DeviceName}' connected successfully after radio bounce on attempt {attempt}/8.");
                                                    return true;
                                                }
                                            }
                                            else
                                            {
                                                Log($"  Radio bounce verify attempt {attempt}/8: device not found in PairedDevices");
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception ex)
                                    {
                                        Log($"  Radio bounce verify attempt {attempt} exception: {ex.Message}");
                                    }
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Log($"  Radio bounce exception: {ex.Message}");
                            }
                            finally
                            {
                                try
                                {
                                    if (_userRequestedAdapterOff)
                                    {
                                        Log("  Radio bounce finally: user requested adapter OFF mid-bounce; ensuring radio is powered OFF in hardware...");
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
                                                Log($"  Radio bounce finally: SetStateAsync(Off) returned {res}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"  Radio bounce finally: power off exception: {ex.Message}");
                                        }
                                    }
                                    else if (radioTurnedOffByBounce &&
                                        (_bluetoothRadio == null || _bluetoothRadio.State != RadioState.On) &&
                                        !_disposed)
                                    {
                                        Log("  Radio bounce finally: radio was left OFF by bounce; restoring power (SetAdapterStateAsync(true))...");
                                        try
                                        {
                                            await SetAdapterStateAsync(true, CancellationToken.None, isUserInitiated: false).ConfigureAwait(false);
                                        }
                                        catch (Exception ex)
                                        {
                                            Log($"  Radio bounce finally: power restoration exception: {ex.Message}");
                                        }
                                    }
                                }
                                catch { }

                                SetBounceInProgressMarker(false);
                                IsRadioBounceInProgress = false;
                                // Raise AdapterStateChanged with the verified current state so Form1 restores
                                // labelStatus from "Reconnecting..." to "Enabled" or "Disabled" and updates the toggle switch.
                                try
                                {
                                    bool? finalState = _userRequestedAdapterOff
                                        ? false
                                        : ((_bluetoothRadio != null && _bluetoothRadio.State != RadioState.Unknown) ? (_bluetoothRadio.State == RadioState.On) : (bool?)null);
                                    AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs(finalState));
                                }
                                catch { }
                            }

                            Log($"  ConnectDeviceAsync failed for '{targetDeviceName}' after all 6 steps.");
                            if (isBlePaired && !token.IsCancellationRequested && !_userRequestedAdapterOff)
                            {
                                Log($"  Classic connect chain did not establish connection for dual-mode device '{targetDeviceName}'. Attempting BLE fallback...");
                                if (await ConnectBleDeviceAsync(address, token).ConfigureAwait(false))
                                {
                                    Log($"  '{targetDeviceName}' connected successfully via BLE fallback.");
                                    return true;
                                }
                            }
                            return false;
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
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log($"ConnectDeviceAsync cancelled for {address:X12}");
                    return false;
                }
                finally
                {
                    SafeRelease(sem);
                }
            }
        }

        /// <summary>
        /// Attempts to disconnect a paired Bluetooth device using Core Audio IKsControl disconnect
        /// and native driver IOCTL baseband teardown (IOCTL_BTH_DISCONNECT_DEVICE).
        /// Preserves all Windows PnP profile drivers (AudioSink, Handsfree, Headset, HID) enabled in the registry
        /// so devices can seamlessly auto-reconnect on next power-on.
        /// Guarded by _connectSemaphore.
        /// </summary>
        public async Task<bool> DisconnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            Log($"DisconnectDeviceAsync called for {address:X12}");
            if (_disposed) return false;

            // Note: Defer battery cache purge until disconnection actually succeeds (Fix Issue #17)

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
                    Log($"DisconnectDeviceAsync: Cancelled while waiting for lock ({address:X12})");
                    return false;
                }

                try
                {
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            string targetName;
                            bool disconnected = false;

                            using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                            {
                                var paired = client.PairedDevices;
                                var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                                if (device == null)
                                {
                                    Log($"  DisconnectDeviceAsync: device {address:X12} not found in Classic PairedDevices. Attempting BLE disconnect...");
                                    return await DisconnectBleDeviceAsync(address, token).ConfigureAwait(false);
                                }

                                targetName = device.DeviceName;

                                // Step 1: Refresh device state before acting
                                device.Refresh();
                                Log($"  device.Refresh() called for '{targetName}', Connected={device.Connected}");
                                if (!device.Connected)
                                {
                                    // Dual-mode guard: device is in PairedDevices but Classic says not connected.
                                    // It may still be connected over BLE (e.g. BLE mouse/keyboard/dual-mode headset).
                                    // Check the BLE side before declaring victory and potentially leaving a live BLE link.
                                    Log($"  '{targetName}' Classic link not connected. Checking BLE connection status...");
                                    bool bleStillConnected = false;
                                    if (_connectedBleDevices.TryGetValue(address, out var bleHandle))
                                    {
                                        bleStillConnected = (bleHandle.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                    }
                                    if (!bleStillConnected)
                                    {
                                        // No tracked handle — do a live check
                                        try
                                        {
                                            using (var bleLookupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                                            using (var bleLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, bleLookupCts.Token))
                                            using (var checkDev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                                .AsTask(bleLinkedCts.Token).ConfigureAwait(false))
                                            {
                                                if (checkDev != null)
                                                    bleStillConnected = (checkDev.ConnectionStatus == BluetoothConnectionStatus.Connected);
                                            }
                                        }
                                        catch { }
                                    }

                                    if (bleStillConnected)
                                    {
                                        Log($"  '{targetName}' is connected over BLE — routing to BLE disconnect.");
                                        return await DisconnectBleDeviceAsync(address, token).ConfigureAwait(false);
                                    }

                                    HandleDeviceDisconnected(address);
                                    Log($"  '{targetName}' is already disconnected (Classic and BLE).");
                                    return true;
                                }

                                // Step 2: Core Audio disconnect (best-effort for audio endpoints)
                                if (EnableDisconnectCoreAudio)
                                {
                                    try
                                    {
                                        var swCad = System.Diagnostics.Stopwatch.StartNew();
                                        bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, targetName, connect: false);
                                        swCad.Stop();
                                        Log($"  [Disconnect Step 2: CoreAudio] triggered={coreAudioTriggered}, elapsed={swCad.ElapsedMilliseconds}ms");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"  CoreAudio disconnect note: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Log("  [Disconnect Step 2: CoreAudio] SKIPPED via diagnostic switch.");
                                }

                                // Step 3: Native Bluetooth Port Driver ACL Link Disconnect (IOCTL_BTH_DISCONNECT_DEVICE)
                                if (EnableDisconnectIoctl)
                                {
                                    try
                                    {
                                        var swIoctl = System.Diagnostics.Stopwatch.StartNew();
                                        bool ioctlSuccess = DisconnectDeviceViaIoctl(address);
                                        swIoctl.Stop();
                                        Log($"  [Disconnect Step 3: IOCTL] result={ioctlSuccess}, elapsed={swIoctl.ElapsedMilliseconds}ms");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"  Native IOCTL disconnect note: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    Log("  [Disconnect Step 3: IOCTL] SKIPPED via diagnostic switch.");
                                }

                                // Step 4: Extended Polling Window (5 x 800ms = 4s total)
                                for (int attempt = 1; attempt <= 5; attempt++)
                                {
                                    try
                                    {
                                        await Task.Delay(800, token).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException) { throw; }

                                    try
                                    {
                                        device.Refresh();
                                        Log($"  Disconnection verify attempt {attempt}/5: Connected={device.Connected}");
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

                                bool isConnectedNow = false;
                                try { isConnectedNow = device.Connected; } catch { }

                                if (disconnected || !isConnectedNow)
                                {
                                    // Dual-mode cleanup: also release any tracked BLE handle
                                    ReleaseConnectedBleHandle(address);

                                    HandleDeviceDisconnected(address);
                                    Log($"  '{targetName}' disconnected successfully without disabling profile drivers.");
                                    return true;
                                }
                            }

                            // Dual-mode fallback: ensure tracked BLE handle is released
                            ReleaseConnectedBleHandle(address);

                            Log($"  DisconnectDeviceAsync failed for '{targetName}'.");
                            return false;
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
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Log($"DisconnectDeviceAsync cancelled for {address:X12}");
                    return false;
                }
                finally
                {
                    SafeRelease(sem);
                }
            }
        }

        /// <summary>
        /// Sends IOCTL_BTH_DISCONNECT_DEVICE to all local Bluetooth radio handles.
        /// Enumerates all radios on the system (supporting multi-adapter machines with motherboard + USB dongle)
        /// to ensure the target device's baseband ACL connection is cleanly terminated regardless of which radio hosts it,
        /// without unbinding or disabling the device's PnP profile drivers (AudioSink, Handsfree, Headset, HID) in the registry.
        /// </summary>
        private static bool DisconnectDeviceViaIoctl(ulong address)
        {
            if (address == 0) return false;

            var radioHandles = new List<IntPtr>();
            IntPtr hFind = IntPtr.Zero;
            try
            {
                var radioParams = new NativeMethods.BLUETOOTH_FIND_RADIO_PARAMS();
                radioParams.dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.BLUETOOTH_FIND_RADIO_PARAMS));

                IntPtr hRadio = IntPtr.Zero;
                hFind = NativeMethods.BluetoothFindFirstRadio(ref radioParams, out hRadio);
                if (hFind != IntPtr.Zero && hRadio != IntPtr.Zero)
                {
                    radioHandles.Add(hRadio);
                    while (NativeMethods.BluetoothFindNextRadio(hFind, out IntPtr nextRadio))
                    {
                        if (nextRadio != IntPtr.Zero)
                        {
                            radioHandles.Add(nextRadio);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  DisconnectDeviceViaIoctl radio enumeration exception: {ex.Message}");
            }
            finally
            {
                if (hFind != IntPtr.Zero)
                {
                    try { NativeMethods.BluetoothFindRadioClose(hFind); } catch { }
                }
            }

            try
            {
                if (radioHandles.Count == 0)
                {
                    Log("  DisconnectDeviceViaIoctl: No local Bluetooth radio handle found.");
                    return false;
                }

                Log($"  DisconnectDeviceViaIoctl: Enumerated {radioHandles.Count} radio(s). Sending IOCTL_BTH_DISCONNECT_DEVICE for {address:X12}...");
                bool anyDisconnected = false;
                long inBuffer = (long)address;

                for (int i = 0; i < radioHandles.Count; i++)
                {
                    IntPtr hRadio = radioHandles[i];
                    int bytesReturned = 0;
                    bool success = NativeMethods.DeviceIoControl(
                        hRadio,
                        NativeMethods.IOCTL_BTH_DISCONNECT_DEVICE,
                        ref inBuffer,
                        sizeof(long),
                        IntPtr.Zero,
                        0,
                        out bytesReturned,
                        IntPtr.Zero
                    );

                    if (success)
                    {
                        Log($"  DisconnectDeviceViaIoctl: IOCTL succeeded on radio #{i + 1}");
                        anyDisconnected = true;
                    }
                    else
                    {
                        int win32Err = Marshal.GetLastWin32Error();
                        Log($"  DisconnectDeviceViaIoctl: Radio #{i + 1} DeviceIoControl returned false. Win32 error={win32Err}");
                    }
                }

                return anyDisconnected;
            }
            catch (Exception ex)
            {
                Log($"  DisconnectDeviceViaIoctl exception: {ex.Message}");
                return false;
            }
            finally
            {
                foreach (var hRadio in radioHandles)
                {
                    if (hRadio != IntPtr.Zero)
                    {
                        try { NativeMethods.CloseHandle(hRadio); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Connects to a paired Bluetooth LE device by requesting uncached GATT services to trigger link establishment.
        /// On success, stores the open BluetoothLEDevice handle in _connectedBleDevices so Windows keeps the BLE ACL
        /// link alive. The handle is released when the device disconnects or the adapter is turned off.
        /// </summary>
        private async Task<bool> ConnectBleDeviceAsync(ulong address, CancellationToken token)
        {
            Log($"  ConnectBleDeviceAsync called for {address:X12}");
            BluetoothLEDevice bleDevice = null;
            bool success = false;
            try
            {
                using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, lookupCts.Token))
                {
                    bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);
                }

                if (bleDevice == null)
                {
                    Log($"  ConnectBleDeviceAsync: BluetoothLEDevice {address:X12} not found.");
                    return false;
                }

                string bleName = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : "BLE Device";
                Log($"  ConnectBleDeviceAsync: Found '{bleName}', initial ConnectionStatus={bleDevice.ConnectionStatus}");

                if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    Log($"  ConnectBleDeviceAsync: '{bleName}' is already connected. Storing handle.");
                    // Store the handle to keep the link alive (replace any stale entry).
                    var old = _connectedBleDevices.AddOrUpdate(address, bleDevice, (_, prev) =>
                    {
                        if (!ReferenceEquals(prev, bleDevice)) { try { prev.Dispose(); } catch { } }
                        return bleDevice;
                    });
                    success = true;
                    return true;
                }

                // Querying uncached GATT services prompts Windows to initiate an active connection to the paired BLE device.
                // We deliberately do NOT dispose the GATT service handles here — keeping them open extends the
                // OS reference count and helps maintain the connection during the polling loop below.
                Log($"  ConnectBleDeviceAsync: Requesting uncached GATT services to trigger connection for '{bleName}'...");
                GattDeviceServicesResult gattResult = null;
                try
                {
                    using (var gattCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using (var linkedGattCts = CancellationTokenSource.CreateLinkedTokenSource(token, gattCts.Token))
                    {
                        gattResult = await bleDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                            .AsTask(linkedGattCts.Token).ConfigureAwait(false);
                        Log($"  ConnectBleDeviceAsync: GATT query completed with status {gattResult?.Status}.");
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log($"  ConnectBleDeviceAsync: GATT service query timed out; checking connection status...");
                }
                catch (Exception ex)
                {
                    Log($"  ConnectBleDeviceAsync: GATT service query note: {ex.Message}");
                }

                // Verify connection status (poll 5 times x 800ms)
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    Log($"  ConnectBleDeviceAsync: Verify attempt {attempt}/5: ConnectionStatus={bleDevice.ConnectionStatus}");
                    if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        Log($"  ConnectBleDeviceAsync: '{bleName}' connected successfully on attempt {attempt}/5. Storing handle.");
                        // Store the open handle — do NOT dispose bleDevice here.
                        var old = _connectedBleDevices.AddOrUpdate(address, bleDevice, (_, prev) =>
                        {
                            if (!ReferenceEquals(prev, bleDevice)) { try { prev.Dispose(); } catch { } }
                            return bleDevice;
                        });

                        // Issue #18: Establish GattSession with MaintainConnection = true
                        try
                        {
                            var session = await GattSession.FromDeviceIdAsync(bleDevice.BluetoothDeviceId).AsTask(token).ConfigureAwait(false);
                            if (session != null)
                            {
                                session.MaintainConnection = true;
                                _connectedBleSessions.AddOrUpdate(address, session, (_, prev) =>
                                {
                                    try { prev.MaintainConnection = false; prev.Dispose(); } catch { }
                                    return session;
                                });
                                Log($"  ConnectBleDeviceAsync: GattSession MaintainConnection set for '{bleName}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  ConnectBleDeviceAsync: GattSession initialization note: {ex.Message}");
                        }

                        success = true;
                        return true;
                    }
                    await Task.Delay(800, token).ConfigureAwait(false);
                }

                // Dispose GATT service handles since connection failed.
                if (gattResult?.Services != null)
                {
                    foreach (var svc in gattResult.Services) { try { svc.Dispose(); } catch { } }
                }

                Log($"  ConnectBleDeviceAsync failed for '{bleName}' ({address:X12}).");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"  ConnectBleDeviceAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                // Only dispose the device handle if the connection failed.
                // On success, bleDevice is now owned by _connectedBleDevices.
                if (!success)
                {
                    try { bleDevice?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Disconnects a paired Bluetooth LE device by releasing any tracked BLE handle, sending an IOCTL
        /// baseband teardown, and verifying the connection drops via polling.
        /// </summary>
        private async Task<bool> DisconnectBleDeviceAsync(ulong address, CancellationToken token)
        {
            Log($"  DisconnectBleDeviceAsync called for {address:X12}");

            // Step 0: Release the tracked BLE handle FIRST so our process stops keeping the ACL link alive.
            // This is intentionally done before IOCTL so the OS can drop the link on its own too.
            ReleaseConnectedBleHandle(address);

            BluetoothLEDevice bleDevice = null;
            try
            {
                using (var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, lookupCts.Token))
                {
                    bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                        .AsTask(linkedCts.Token).ConfigureAwait(false);
                }

                if (bleDevice == null)
                {
                    Log($"  DisconnectBleDeviceAsync: BluetoothLEDevice {address:X12} not found.");
                    return false;
                }

                string bleName = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : "BLE Device";
                Log($"  DisconnectBleDeviceAsync: Found '{bleName}', initial ConnectionStatus={bleDevice.ConnectionStatus}");

                if (bleDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    HandleDeviceDisconnected(address);
                    Log($"  DisconnectBleDeviceAsync: '{bleName}' is already disconnected.");
                    return true;
                }

                // Step 1: Native IOCTL baseband disconnect
                try
                {
                    bool ioctlSuccess = DisconnectDeviceViaIoctl(address);
                    Log($"  DisconnectBleDeviceAsync: IOCTL disconnect returned {ioctlSuccess} for '{bleName}'");
                }
                catch (Exception ex)
                {
                    Log($"  DisconnectBleDeviceAsync: IOCTL disconnect note: {ex.Message}");
                }

                // Step 2: Release the temporary lookup handle so our process holds no references at all.
                try
                {
                    bleDevice.Dispose();
                    bleDevice = null;
                }
                catch { }

                // Step 3: Polling verification (5 x 800ms)
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(800, token).ConfigureAwait(false);

                    try
                    {
                        using (var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        using (var linkedCheckCts = CancellationTokenSource.CreateLinkedTokenSource(token, checkCts.Token))
                        {
                            using (var checkDev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                                .AsTask(linkedCheckCts.Token).ConfigureAwait(false))
                            {
                                if (checkDev == null || checkDev.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                                {
                                    HandleDeviceDisconnected(address);
                                    Log($"  DisconnectBleDeviceAsync: '{bleName}' disconnected on attempt {attempt}/5.");
                                    return true;
                                }
                                Log($"  DisconnectBleDeviceAsync: Verify attempt {attempt}/5: ConnectionStatus={checkDev.ConnectionStatus}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log($"  DisconnectBleDeviceAsync verify attempt {attempt} note: {ex.Message}");
                    }
                }

                Log($"  DisconnectBleDeviceAsync failed for '{bleName}' ({address:X12}).");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"  DisconnectBleDeviceAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                try { bleDevice?.Dispose(); } catch { }
            }
        }

        // ── Battery Reading ───────────────────────────────────────────────────

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
                            Log($"    Battery PnP (cached instance): {level}% from '{instanceId}'");
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

                if (addrUpper.Length > 0 && addrUpper.Length < 12)
                {
                    addrUpper = addrUpper.PadLeft(12, '0');
                }

                // Check cached instance ID first (O(1) direct device property read without enumerating, Fix Issue #24)
                if (_pnpInstanceIdCache.TryGetValue(addrUpper, out string cachedInstanceId) && !string.IsNullOrEmpty(cachedInstanceId))
                {
                    int? directBattery = ReadPnpBatteryDirect(cachedInstanceId);
                    if (directBattery != null)
                    {
                        return directBattery;
                    }
                }

                Log($"    Battery PnP: searching for address '{addrUpper}'");

                // The Bluetooth HFP battery property key
                var batteryPropertyKey = new NativeMethods.DEVPROPKEY
                {
                    fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
                    pid = 2
                };

                // Search all Bluetooth enumerator buses where battery properties appear:
                // BTHENUM (Classic Bluetooth), BTHHFENUM (Hands-Free Profile audio devices), BTHLEDEVICE (BLE devices)
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
                                    Log($"    Battery PnP: {level}% from '{instanceId}' ({enumerator})");
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

                // Fallback: enumerate by Bluetooth device setup class GUID
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
                                    Log($"    Battery PnP: {level}% from fallback GUID '{instanceId}'");
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
            public const uint DEVPROP_TYPE_BYTE = 0x00000003;

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
            public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern bool SetupDiOpenDeviceInfo(
                IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);

            [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

            [System.Runtime.InteropServices.DllImport("ole32.dll")]
            public static extern int PropVariantClear(ref PROPVARIANT pvar);

            public const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000C;

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct BLUETOOTH_FIND_RADIO_PARAMS
            {
                public uint dwSize;
            }

            [System.Runtime.InteropServices.DllImport("bthprops.cpl", SetLastError = true)]
            public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

            [System.Runtime.InteropServices.DllImport("bthprops.cpl", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

            [System.Runtime.InteropServices.DllImport("bthprops.cpl", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool BluetoothFindRadioClose(IntPtr hFind);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            public static extern bool DeviceIoControl(
                IntPtr hDevice,
                uint dwIoControlCode,
                ref long lpInBuffer,
                int nInBufferSize,
                IntPtr lpOutBuffer,
                int nOutBufferSize,
                out int lpBytesReturned,
                IntPtr lpOverlapped);
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

        [StructLayout(LayoutKind.Explicit, Size = 24)]
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
            [FieldOffset(16)] private long _padding64;
        }

        [ComImport]
        [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDeviceTopology
        {
            [PreserveSig]
            int GetConnectorCount(out uint pCount);

            [PreserveSig]
            int GetConnector(uint nIndex, [MarshalAs(UnmanagedType.Interface)] out object ppConnector);

            [PreserveSig]
            int GetSubunitCount(out uint pCount);

            [PreserveSig]
            int GetSubunit(uint nIndex, out IntPtr ppSubunit);

            [PreserveSig]
            int GetPartById(uint nId, [MarshalAs(UnmanagedType.Interface)] out object ppPart);

            [PreserveSig]
            int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string pDeviceId);

            [PreserveSig]
            int GetSignalPath(IPart pIPartFrom, IPart pIPartTo, bool bRejectProcesses, out IntPtr ppParts);
        }

        [ComImport]
        [Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
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
            int GetConnectedTo([MarshalAs(UnmanagedType.Interface)] out object ppConTo);

            [PreserveSig]
            int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrConnectorId);

            [PreserveSig]
            int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
        }

        [ComImport]
        [Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
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
        /// Schedules a deferred battery query after a short delay (or immediately if requested) for connected devices.
        /// Performs fast cached read first, and if cold/absent, performs slow background discovery with bounded backoff retries.
        /// Caches outcome (Success, Unsupported, TransientFailure) and preserves last-good values on transient failures.
        /// If a valid battery value is found on retry, fires DeviceConnectionChanged to trigger a UI refresh.
        /// </summary>
        private void ScheduleDeferredBatteryQuery(ulong address, string deviceName, bool isClassicOnly = false, bool immediate = false)
        {
            if (address == 0 || _disposed) return;

            // Check cache entry status
            if (_batteryCache.TryGetValue(address, out var existingEntry))
            {
                if (existingEntry.Outcome == BatteryQueryOutcome.Unsupported && DateTime.UtcNow < existingEntry.NextAllowedRetryUtc)
                {
                    Log($"  Battery GATT: '{deviceName}' ({address:X12}) confirmed Unsupported. Leaving alone.");
                    return;
                }
                if (existingEntry.Outcome == BatteryQueryOutcome.TransientFailure && DateTime.UtcNow < existingEntry.NextAllowedRetryUtc)
                {
                    Log($"  Battery GATT: '{deviceName}' ({address:X12}) in transient backoff until {existingEntry.NextAllowedRetryUtc:HH:mm:ss} UTC (failure #{existingEntry.FailureCount}). Skipping.");
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

            CancellationToken opToken;
            lock (_deviceOpCtsLock)
            {
                opToken = _deviceOpCts.Token;
            }

            Log($"  Scheduling {(immediate ? "immediate" : "deferred (3s)")} battery query for '{deviceName}' ({address:X12})...");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!immediate)
                    {
                        await Task.Delay(3000, opToken).ConfigureAwait(false);
                    }

                    if (_disposed || opToken.IsCancellationRequested) return;

                    // Verify device is still registered and connected
                    lock (_batteryRetryLock)
                    {
                        if (!_inFlightBatteryQueries.Contains(address))
                        {
                            Log($"  Deferred battery query aborted: '{deviceName}' ({address:X12}) was cancelled or disconnected.");
                            return;
                        }
                    }

                    bool? isConnected = await IsDeviceConnectedAsync(address, opToken).ConfigureAwait(false);
                    if (isConnected != true)
                    {
                        Log($"  Deferred battery query aborted: '{deviceName}' ({address:X12}) is not connected.");
                        HandleDeviceDisconnected(address);
                        return;
                    }

                    string addrHex = address.ToString("X12");
                    int? retriedBattery = QueryBatteryViaPnp(addrHex);

                    var cacheEntry = _batteryCache.GetOrAdd(address, a => new BatteryCacheEntry { Address = a });

                    if (retriedBattery != null)
                    {
                        cacheEntry.RecordSuccess(retriedBattery.Value, deviceName);
                        Log($"  Battery query SUCCESS via PnP for '{deviceName}': {retriedBattery}%. Firing DeviceConnectionChanged.");
                        DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    // Skip GATT entirely for devices that are classic-only!
                    if (isClassicOnly)
                    {
                        Log($"  Battery: '{deviceName}' ({address:X12}) is Classic-only and PnP returned null. Skipping GATT and caching as Unsupported.");
                        cacheEntry.RecordUnsupported(deviceName, isClassicOnly: true);
                        return;
                    }

                    // Guard: verify connection before running GATT query
                    isConnected = await IsDeviceConnectedAsync(address, opToken).ConfigureAwait(false);
                    if (isConnected != true)
                    {
                        Log($"  Deferred battery query aborted before GATT: '{deviceName}' ({address:X12}) is not connected.");
                        HandleDeviceDisconnected(address);
                        return;
                    }

                    var gattResult = await QueryBatteryViaGattAsync(address, deviceName, opToken).ConfigureAwait(false);

                    // Check if disconnected during query
                    lock (_batteryRetryLock)
                    {
                        if (!_inFlightBatteryQueries.Contains(address))
                        {
                            Log($"  Deferred battery query result discarded: '{deviceName}' ({address:X12}) was disconnected during query.");
                            return;
                        }
                    }

                    if (gattResult.Outcome == BatteryQueryOutcome.Success && gattResult.BatteryLevel.HasValue)
                    {
                        cacheEntry.RecordSuccess(gattResult.BatteryLevel.Value, deviceName);
                        Log($"  Deferred battery query SUCCESS via GATT for '{deviceName}': {gattResult.BatteryLevel.Value}%. Firing DeviceConnectionChanged.");
                        DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (gattResult.Outcome == BatteryQueryOutcome.Unsupported)
                    {
                        cacheEntry.RecordUnsupported(deviceName, isClassicOnly: false);
                        Log($"  Deferred battery query: '{deviceName}' confirmed Unsupported (cached for {BatteryUnsupportedCacheDuration.TotalHours}h). Leaving alone.");
                    }
                    else
                    {
                        cacheEntry.RecordTransientFailure(deviceName);
                        Log($"  Deferred battery query: Transient failure #{cacheEntry.FailureCount} for '{deviceName}'. Preserving last good value ({cacheEntry.GetEffectiveBattery()?.ToString() ?? "N/A"}). Next retry at {cacheEntry.NextAllowedRetryUtc:HH:mm:ss} UTC.");

                        // Bounded retry while connected
                        if (cacheEntry.FailureCount <= MaxBatteryRetries)
                        {
                            ScheduleBoundedRetryAfterBackoff(address, deviceName, cacheEntry.FailureCount, opToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Log($"  Deferred battery query cancelled for '{deviceName}' ({address:X12}).");
                }
                catch (Exception ex)
                {
                    Log($"  Deferred battery query exception for '{deviceName}': {ex.Message}");
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

        private void ScheduleBoundedRetryAfterBackoff(ulong address, string deviceName, int failureCount, CancellationToken opToken)
        {
            var backoff = GetTransientBackoff(failureCount);
            Log($"  Battery GATT: Scheduling bounded retry #{failureCount + 1} for '{deviceName}' in {backoff.TotalSeconds}s...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(backoff, opToken).ConfigureAwait(false);
                    if (!opToken.IsCancellationRequested && !_disposed)
                    {
                        bool? stillConnected = await IsDeviceConnectedAsync(address, opToken).ConfigureAwait(false);
                        if (stillConnected == true)
                        {
                            ScheduleDeferredBatteryQuery(address, deviceName, isClassicOnly: false, immediate: true);
                        }
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// Reads battery via BLE GATT Battery Service (UUID 0x180F / characteristic 0x2A19).
        /// Split into two phases:
        /// 1. Fast Cached Read (BatteryCachedQueryBudgetMs, default 2000ms): uses cached service/characteristic layout.
        /// 2. Slow Background Discovery (BatteryUncachedQueryBudgetMs, default 10000ms): over-the-air discovery if cache is cold or absent.
        /// Logs elapsed-ms and status for every step.
        /// Caches outcome separately: Success, Unsupported (service confirmed absent), or TransientFailure (timeout/error).
        /// Disposes DataReader using deterministic using statements.
        /// </summary>
        private async Task<BatteryQueryResult> QueryBatteryViaGattAsync(ulong bluetoothAddress, string deviceName, CancellationToken ct)
        {
            var totalSw = Stopwatch.StartNew();
            BluetoothLEDevice bleDevice = null;
            bool isCachedHandle = false;

            try
            {
                // 0. Fast path: reuse an existing tracked handle from _connectedBleDevices
                if (bluetoothAddress != 0 && _connectedBleDevices.TryGetValue(bluetoothAddress, out var cachedDev))
                {
                    bleDevice = cachedDev;
                    isCachedHandle = true;
                    Log($"    Battery GATT: Reusing tracked handle for {bluetoothAddress:X12} ('{bleDevice.Name}')");
                }

                // 1. Look up by Bluetooth address first (exact match)
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

                // 2. If address lookup returned null, fall back to name matching via DeviceInformation
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
                                    Log($"    Battery GATT: Matched paired BLE device '{match.Name}' (Id={match.Id}) for '{deviceName}'");
                                    bleDevice = await BluetoothLEDevice.FromIdAsync(match.Id).AsTask(linkedFind.Token).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"    Battery GATT: DeviceInformation.FindAllAsync lookup note: {ex.Message}");
                    }
                }

                if (bleDevice == null)
                {
                    Log($"    Battery GATT: BLE device not found for '{deviceName}' (address {bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
                    return new BatteryQueryResult
                    {
                        Outcome = BatteryQueryOutcome.TransientFailure,
                        ElapsedMs = totalSw.ElapsedMilliseconds,
                        Details = "BLE device not found"
                    };
                }

                // =========================================================================
                // Phase 1: Fast Cached Read (Budget: BatteryCachedQueryBudgetMs, e.g. 2000ms)
                // =========================================================================
                bool fastReadSucceeded = false;
                int fastBatteryLevel = -1;

                try
                {
                    using (var fastCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(BatteryCachedQueryBudgetMs)))
                    using (var linkedFastCts = CancellationTokenSource.CreateLinkedTokenSource(ct, fastCts.Token))
                    {
                        var fastToken = linkedFastCts.Token;
                        var swCachedServices = Stopwatch.StartNew();
                        var cachedServicesResult = await bleDevice
                            .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
                            .AsTask(fastToken).ConfigureAwait(false);
                        long cachedServicesMs = swCachedServices.ElapsedMilliseconds;

                        Log($"    Battery GATT [Fast Cached]: GetGattServices took {cachedServicesMs}ms, status={cachedServicesResult?.Status}, count={cachedServicesResult?.Services?.Count ?? 0}");

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

                                Log($"    Battery GATT [Fast Cached]: GetCharacteristics took {cachedCharsMs}ms, status={cachedCharsResult?.Status}, count={cachedCharsResult?.Characteristics?.Count ?? 0}");

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

                                    Log($"    Battery GATT [Fast Cached]: ReadValueAsync took {cachedReadMs}ms, status={readResult?.Status}");

                                    if (readResult != null && readResult.Status == GattCommunicationStatus.Success && readResult.Value != null)
                                    {
                                        using (var reader = DataReader.FromBuffer(readResult.Value))
                                        {
                                            byte level = reader.ReadByte();
                                            fastBatteryLevel = (int)level;
                                            fastReadSucceeded = true;
                                            Log($"    Battery GATT [Fast Cached SUCCESS]: {level}% for '{bleDevice.Name}' ({bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
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
                    Log($"    Battery GATT [Fast Cached]: Cache lookup did not complete: {ex.Message} ({totalSw.ElapsedMilliseconds}ms)");
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

                // =========================================================================
                // Phase 2: Slow Background Discovery (Budget: BatteryUncachedQueryBudgetMs, e.g. 10000ms)
                // =========================================================================
                Log($"    Battery GATT: Transitioning to Slow Background Discovery for '{bleDevice.Name}' ({BatteryUncachedQueryBudgetMs}ms budget)...");

                using (var slowCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(BatteryUncachedQueryBudgetMs)))
                using (var linkedSlowCts = CancellationTokenSource.CreateLinkedTokenSource(ct, slowCts.Token))
                {
                    var slowToken = linkedSlowCts.Token;
                    var swUncachedServices = Stopwatch.StartNew();
                    var uncachedServicesResult = await bleDevice
                        .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                        .AsTask(slowToken).ConfigureAwait(false);
                    long uncachedServicesMs = swUncachedServices.ElapsedMilliseconds;

                    Log($"    Battery GATT [Slow Discovery]: GetGattServices (Uncached) took {uncachedServicesMs}ms, status={uncachedServicesResult?.Status}, count={uncachedServicesResult?.Services?.Count ?? 0}");

                    // Outcome: Unsupported (Service confirmed absent)
                    if (uncachedServicesResult != null &&
                        uncachedServicesResult.Status == GattCommunicationStatus.Success &&
                        (uncachedServicesResult.Services == null || uncachedServicesResult.Services.Count == 0))
                    {
                        Log($"    Battery GATT [Unsupported]: Device '{bleDevice.Name}' confirmed absent Battery Service in {totalSw.ElapsedMilliseconds}ms.");
                        return new BatteryQueryResult
                        {
                            Outcome = BatteryQueryOutcome.Unsupported,
                            ElapsedMs = totalSw.ElapsedMilliseconds,
                            Details = "Battery Service absent"
                        };
                    }

                    if (uncachedServicesResult == null || uncachedServicesResult.Status != GattCommunicationStatus.Success)
                    {
                        Log($"    Battery GATT [Transient Failure]: Services discovery status={uncachedServicesResult?.Status} in {totalSw.ElapsedMilliseconds}ms.");
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

                        Log($"    Battery GATT [Slow Discovery]: GetCharacteristics (Uncached) took {uncachedCharsMs}ms, status={uncachedCharsResult?.Status}, count={uncachedCharsResult?.Characteristics?.Count ?? 0}");

                        if (uncachedCharsResult != null &&
                            uncachedCharsResult.Status == GattCommunicationStatus.Success &&
                            (uncachedCharsResult.Characteristics == null || uncachedCharsResult.Characteristics.Count == 0))
                        {
                            Log($"    Battery GATT [Unsupported]: BatteryLevel characteristic absent on '{bleDevice.Name}' in {totalSw.ElapsedMilliseconds}ms.");
                            return new BatteryQueryResult
                            {
                                Outcome = BatteryQueryOutcome.Unsupported,
                                ElapsedMs = totalSw.ElapsedMilliseconds,
                                Details = "BatteryLevel characteristic absent"
                            };
                        }

                        if (uncachedCharsResult == null || uncachedCharsResult.Status != GattCommunicationStatus.Success)
                        {
                            Log($"    Battery GATT [Transient Failure]: Characteristics query status={uncachedCharsResult?.Status} in {totalSw.ElapsedMilliseconds}ms.");
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

                        Log($"    Battery GATT [Slow Discovery]: ReadValueAsync took {uncachedReadMs}ms, status={readResult?.Status}");

                        if (readResult != null && readResult.Status == GattCommunicationStatus.Success && readResult.Value != null)
                        {
                            using (var reader = DataReader.FromBuffer(readResult.Value))
                            {
                                byte level = reader.ReadByte();
                                Log($"    Battery GATT [Slow Discovery SUCCESS]: {level}% for '{bleDevice.Name}' ({bluetoothAddress:X12}) in {totalSw.ElapsedMilliseconds}ms");
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
                Log($"    Battery GATT [Timeout/Cancelled]: Operation timed out or cancelled in {totalSw.ElapsedMilliseconds}ms for '{deviceName}' ({bluetoothAddress:X12})");
                return new BatteryQueryResult
                {
                    Outcome = BatteryQueryOutcome.TransientFailure,
                    ElapsedMs = totalSw.ElapsedMilliseconds,
                    Details = "Timed out or cancelled"
                };
            }
            catch (Exception ex)
            {
                Log($"    Battery GATT [Exception]: Error querying '{deviceName}' ({bluetoothAddress:X12}): {ex.Message} ({totalSw.ElapsedMilliseconds}ms)");
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

        // ── Device Classification ─────────────────────────────────────────────

        private static BluetoothDeviceType GetDeviceClassificationOverride(ulong address)
        {
            if (address == 0) return BluetoothDeviceType.Unknown;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Connectify\DeviceOverrides"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(address.ToString("X12"));
                        if (val is string str && Enum.TryParse<BluetoothDeviceType>(str, true, out var parsed))
                        {
                            return parsed;
                        }
                        if (val is int intVal && Enum.IsDefined(typeof(BluetoothDeviceType), intVal))
                        {
                            return (BluetoothDeviceType)intVal;
                        }
                    }
                }
            }
            catch { }
            return BluetoothDeviceType.Unknown;
        }

        /// <summary>
        /// Classifies a Bluetooth LE device using its BluetoothLEAppearance or name-based heuristics.
        /// </summary>
        private static BluetoothDeviceType ClassifyBleDevice(BluetoothLEDevice bleDevice)
        {
            if (bleDevice == null) return BluetoothDeviceType.Unknown;

            // 1. User per-address override from registry (highest precedence, Fix Issue #32)
            var userOverride = GetDeviceClassificationOverride(bleDevice.BluetoothAddress);
            if (userOverride != BluetoothDeviceType.Unknown) return userOverride;

            // 2. Bluetooth SIG Appearance
            try
            {
                var appearance = bleDevice.Appearance;
                if (appearance != null)
                {
                    var fromAppearance = ClassifyByAppearance(appearance.Category, appearance.SubCategory);
                    if (fromAppearance != BluetoothDeviceType.Unknown)
                        return fromAppearance;
                }
            }
            catch (Exception ex)
            {
                Log($"  ClassifyBleDevice appearance exception: {ex.Message}");
            }

            // 3. Name-based heuristics (last resort)
            string name = bleDevice.Name ?? string.Empty;
            return ClassifyByName(name);
        }

        public static BluetoothDeviceType ClassifyByAppearance(ushort category, ushort subCategory)
        {
            switch (category)
            {
                case 0x03C0: // Human Interface Device
                    if (subCategory == 1) return BluetoothDeviceType.Keyboard;
                    if (subCategory == 2) return BluetoothDeviceType.Mouse;
                    if (subCategory == 3 || subCategory == 4) return BluetoothDeviceType.Controller;
                    return BluetoothDeviceType.Mouse;
                case 0x0040: // Phone
                    return BluetoothDeviceType.Phone;
                case 0x0080: // Computer
                    return BluetoothDeviceType.Computer;
                case 0x00C0: // Watch / Wearable
                    return BluetoothDeviceType.Smartwatch;
                case 0x0840: // Media Player / Audio (subcategories, Fix Issue #32)
                    switch (subCategory)
                    {
                        case 1: return BluetoothDeviceType.Earbuds;     // Wearable Audio / In-ear
                        case 2: return BluetoothDeviceType.Headphones;  // Over-ear headphones
                        case 3: return BluetoothDeviceType.Speaker;     // Loudspeaker
                        case 4: return BluetoothDeviceType.Speaker;     // Soundbar
                        default: return BluetoothDeviceType.AudioDevice;
                    }
                default:
                    return BluetoothDeviceType.Unknown;
            }
        }

        /// <summary>
        /// Reads the Bluetooth Class of Device (CoD) bitmask to determine device type.
        /// Falls back to name-pattern matching for unclassified CoDs.
        /// </summary>
        private static BluetoothDeviceType ClassifyDevice(InTheHand.Net.Sockets.BluetoothDeviceInfo device)
        {
            ulong address = 0;
            try { address = device.DeviceAddress.ToUInt64(); } catch { }

            // 1. User per-address override from registry (highest precedence, Fix Issue #32)
            var userOverride = GetDeviceClassificationOverride(address);
            if (userOverride != BluetoothDeviceType.Unknown) return userOverride;

            // 2. Class of Device (CoD) bitmask
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

            // 3. Name-based heuristics (last resort)
            string name = device.DeviceName ?? string.Empty;
            return ClassifyByName(name);
        }

        internal static Func<ulong, (bool hasPeripherals, string name, BluetoothDeviceType type)> ConnectedPeripheralsProbe { get; set; }

        internal bool HasConnectedPeripherals(ulong currentTargetAddress, out string peripheralName, out BluetoothDeviceType peripheralType)
        {
            peripheralName = null;
            peripheralType = BluetoothDeviceType.Unknown;

            if (ConnectedPeripheralsProbe != null)
            {
                var probeResult = ConnectedPeripheralsProbe(currentTargetAddress);
                if (probeResult.hasPeripherals)
                {
                    peripheralName = probeResult.name;
                    peripheralType = probeResult.type;
                    return true;
                }
            }

            try
            {
                // 1. Check actively connected BLE peripherals
                foreach (var kvp in _connectedBleDevices)
                {
                    if (kvp.Key == currentTargetAddress) continue;
                    var dev = kvp.Value;
                    if (dev != null && dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        var type = ClassifyBleDevice(dev);
                        if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                        {
                            peripheralName = dev.Name;
                            peripheralType = type;
                            Log($"HasConnectedPeripherals: Active BLE peripheral found: '{dev.Name}' ({type})");
                            return true;
                        }
                    }
                }

                // 2. Check actively connected Classic peripherals
                using (var client = new InTheHand.Net.Sockets.BluetoothClient())
                {
                    var paired = client.PairedDevices;
                    if (paired != null)
                    {
                        foreach (var d in paired)
                        {
                            if (d.DeviceAddress.ToUInt64() == currentTargetAddress) continue;
                            if (d.Connected)
                            {
                                var type = ClassifyDevice(d);
                                if (type == BluetoothDeviceType.Mouse || type == BluetoothDeviceType.Keyboard || type == BluetoothDeviceType.Controller)
                                {
                                    peripheralName = d.DeviceName;
                                    peripheralType = type;
                                    Log($"HasConnectedPeripherals: Active Classic peripheral found: '{d.DeviceName}' ({type})");
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"HasConnectedPeripherals check exception: {ex.Message}");
            }
            return false;
        }

        internal bool HasConnectedPeripherals(ulong currentTargetAddress)
        {
            return HasConnectedPeripherals(currentTargetAddress, out _, out _);
        }

        public static BluetoothDeviceType ClassifyByCod(int major, int minor)
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
        /// Checks whether a specific word or token exists in text matching word boundaries (\b).
        /// Prevents false positives where short substrings match unrelated models (e.g. "mp3" or "mk2").
        /// </summary>
        private static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
                return false;

            return Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Name-pattern fallback for devices whose CoD is Uncategorized or 0.
        /// Matches common brand/model keywords case-insensitively.
        /// Uses word boundaries for short tokens to prevent false positives.
        /// Evaluates phone before keyboard (prevents "Joanne's iPhone" -> Keyboard, Fix Issue #32).
        /// </summary>
        public static BluetoothDeviceType ClassifyByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BluetoothDeviceType.Unknown;

            string n = name.ToLowerInvariant();

            // ── Phones (evaluated early to avoid name collisions) ────────────
            if (n.Contains("iphone")   || n.Contains("galaxy s") || n.Contains("galaxy a") ||
                n.Contains("galaxy z") || n.Contains("pixel")    || n.Contains("oneplus")  ||
                n.Contains("redmi")    || n.Contains("poco")     || n.Contains("realme")   ||
                n.Contains("huawei")   || n.Contains("honor")    || n.Contains("xperia")   ||
                n.Contains("smartphone"))
                return BluetoothDeviceType.Phone;

            // ── Controllers ───────────────────────────────────────────────────
            if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("xbox") ||
                n.Contains("8bitdo")    || n.Contains("controller") || n.Contains("gamepad") ||
                n.Contains("joycon")    || n.Contains("pro con"))
                return BluetoothDeviceType.Controller;

            // ── Mouse ─────────────────────────────────────────────────────────
            if (n.Contains("mouse")   || n.Contains("mx master") || n.Contains("mx any") ||
                n.Contains("pro click")|| n.Contains("basilisk") || n.Contains("deathadder") ||
                n.Contains("pebble")  || n.Contains("trackball"))
                return BluetoothDeviceType.Mouse;

            // ── Keyboard ─────────────────────────────────────────────────────
            if (n.Contains("keyboard") || n.Contains("keychron") || n.Contains("mx keys") ||
                ContainsWord(n, "anne pro") || n.Contains("ducky")    ||
                ContainsWord(n, "k2")  || ContainsWord(n, "k4")   ||
                ContainsWord(n, "k8")  || ContainsWord(n, "k10"))
                return BluetoothDeviceType.Keyboard;

            // ── Over-ear headphones / headset (Pulse 3D is a headset, not a speaker)
            if (n.Contains("pulse 3d") || n.Contains("wh-")     || n.Contains("xm4")    ||
                n.Contains("xm5")      || n.Contains("qc45")    || n.Contains("qc35")   ||
                n.Contains("qc700")    || n.Contains("momentum")|| n.Contains("arctis") ||
                n.Contains("nova")     || n.Contains("void")    || n.Contains("cloud")  ||
                n.Contains("stealth")  || n.Contains("headphone") || n.Contains("headset"))
                return BluetoothDeviceType.Headphones;

            // ── Earbuds ──────────────────────────────────────────────────────
            if (n.Contains("airpod")   || n.Contains("galaxy bud") || n.Contains(" buds") ||
                n.Contains("freebuds") || n.Contains("nothing ear")|| n.Contains("liberty") ||
                n.Contains("soundcore")|| n.Contains("jabra")      || n.Contains("earbud") ||
                n.Contains("wf-")      || n.Contains("tws"))
                return BluetoothDeviceType.Earbuds;

            // ── Speakers (no bare pulse/charge/flip false-positives) ─────────
            if (n.Contains("speaker")     || n.Contains("soundbar")   || n.Contains("echo") ||
                n.Contains("homepod")     || n.Contains("boombox")    || n.Contains("ue boom") ||
                n.Contains("wonderboom")  || n.Contains("jbl flip")   || n.Contains("jbl charge") ||
                n.Contains("jbl pulse")   || n.Contains("charge 3")   || n.Contains("charge 4") ||
                n.Contains("charge 5")    || n.Contains("flip 4")     || n.Contains("flip 5") ||
                n.Contains("flip 6")      || n.Contains("xtreme"))
                return BluetoothDeviceType.Speaker;

            // ── Computers ────────────────────────────────────────────────────
            if (n.Contains("desktop") || n.Contains("laptop") || n.Contains("macbook") ||
                n.Contains("surface") || n.Contains("pc "))
                return BluetoothDeviceType.Computer;

            // ── Smartwatch ────────────────────────────────────────────────────
            if (n.Contains("watch")    || n.Contains("galaxy watch") || n.Contains("gear") ||
                n.Contains("mi band")  || n.Contains("smart band")   || n.Contains("fitbit") ||
                n.Contains("garmin")   || n.Contains("vivosmart")    || ContainsWord(n, "band"))
                return BluetoothDeviceType.Smartwatch;

            return BluetoothDeviceType.Unknown;
        }

        /// <summary>
        /// Returns a human-readable service name for a standard Bluetooth profile GUID.
        /// </summary>
        public static string GetProfileName(Guid guid)
        {
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.AudioSink) return "AudioSink (A2DP)";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.Handsfree) return "Handsfree (HFP)";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.Headset) return "Headset (HSP)";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.HumanInterfaceDevice) return "HID";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.AudioSource) return "AudioSource";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.AdvancedAudioDistribution) return "A2DP";
            if (guid == InTheHand.Net.Bluetooth.BluetoothService.GenericAudio) return "GenericAudio";
            return guid.ToString();
        }

        /// <summary>
        /// Three-state transport capability classification for Bluetooth devices.
        /// Classic-capable: Audio or HID profiles are present.
        /// BLE-only: Requires positive evidence (BLE paired, successful read with no classic profiles, not Classic CoD).
        /// Unknown: Empty or exception.
        /// Pure function for unit-testing without hardware.
        /// </summary>
        public static DeviceTransportCapability ClassifyTransportCapability(
            bool isBlePaired,
            bool installedServicesReadSuccess,
            IReadOnlyCollection<Guid> installedServices,
            int codMajor)
        {
            // Empty or exception means Unknown
            if (!installedServicesReadSuccess || installedServices == null)
            {
                return DeviceTransportCapability.Unknown;
            }

            bool hasClassicAudio = false;
            bool hasClassicHid = false;

            foreach (var profile in installedServices)
            {
                if (profile == InTheHand.Net.Bluetooth.BluetoothService.AudioSink ||
                    profile == InTheHand.Net.Bluetooth.BluetoothService.Handsfree ||
                    profile == InTheHand.Net.Bluetooth.BluetoothService.Headset ||
                    profile == InTheHand.Net.Bluetooth.BluetoothService.AudioSource ||
                    profile == InTheHand.Net.Bluetooth.BluetoothService.AdvancedAudioDistribution)
                {
                    hasClassicAudio = true;
                }
                else if (profile == InTheHand.Net.Bluetooth.BluetoothService.HumanInterfaceDevice)
                {
                    hasClassicHid = true;
                }
            }

            // 1. Classic-capable: Audio or HID profiles are present
            if (hasClassicAudio || hasClassicHid)
            {
                return DeviceTransportCapability.ClassicCapable;
            }

            // 2. Empty installed services: if read succeeded but count is 0,
            // if CoD indicates Classic Audio (0x04) or Peripheral (0x05),
            // empty profiles may just mean profiles are disabled (SetServiceState(false)) or unpopulated.
            // Therefore, do NOT declare it BleOnly.
            if (installedServices.Count == 0)
            {
                if (codMajor == 0x04 || codMajor == 0x05)
                {
                    return DeviceTransportCapability.Unknown;
                }
            }

            // 3. BLE-only requires positive evidence:
            // BLE paired, successful read with NO classic profiles, and not a Classic CoD.
            if (isBlePaired && installedServices.Count == 0 && codMajor != 0x04 && codMajor != 0x05)
            {
                return DeviceTransportCapability.BleOnly;
            }

            return DeviceTransportCapability.Unknown;
        }

        /// <summary>
        /// Maps transport capability, CoD major class, and peripheral flags to a connection route.
        /// Pure function with a truth table.
        /// </summary>
        public static ConnectionRoute DetermineConnectionRoute(
            DeviceTransportCapability capability,
            int codMajor,
            bool isPeripheral)
        {
            // 1. BLE-only routes directly to BLE
            if (capability == DeviceTransportCapability.BleOnly)
            {
                return ConnectionRoute.BleDirect;
            }

            // 2. Classic-capable: route to Classic HID if peripheral, else Classic Audio
            if (capability == DeviceTransportCapability.ClassicCapable)
            {
                if (codMajor == 0x05 || isPeripheral)
                {
                    return ConnectionRoute.ClassicWithHid;
                }
                return ConnectionRoute.ClassicWithAudio;
            }

            // 3. Unknown: Fall back to Class of Device (audio or peripheral major class),
            // then to the classic path with the existing fallback profile list.
            if (codMajor == 0x05 || isPeripheral)
            {
                return ConnectionRoute.ClassicWithHid;
            }

            // Audio (0x04) or unclassified -> ClassicWithAudio fallback
            return ConnectionRoute.ClassicWithAudio;
        }

        // ── Device Watcher (real-time connect/disconnect for Classic and BLE) ─

        /// <summary>
        /// Starts DeviceWatchers for both Classic and BLE devices that raise DeviceConnectionChanged
        /// whenever a Bluetooth device connects, disconnects, pairs, or unpairs.
        /// Safe to call multiple times — returns early if already active.
        /// </summary>
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
                Log("DeviceWatcher: Both Classic and BLE watchers already running, ignoring start request");
                return;
            }

            StopDeviceWatcher();
            Log("DeviceWatcher: Starting Classic and BLE watchers...");

            _classicEnumerationComplete = false;
            _bleEnumerationComplete = false;

            try
            {
                // Watch all paired Classic Bluetooth devices (Fix Issue #25: only paired devices)
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
                Log("DeviceWatcher: Classic watcher started");
            }
            catch (Exception ex)
            {
                Log($"DeviceWatcher: Failed to start Classic watcher: {ex.Message}");
            }

            try
            {
                // Watch all paired Bluetooth LE devices
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
                Log("DeviceWatcher: BLE watcher started");
            }
            catch (Exception ex)
            {
                Log($"DeviceWatcher: Failed to start BLE watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops and cleans up both Classic and BLE DeviceWatchers. Safe to call even if no watcher is running.
        /// </summary>
        public void StopDeviceWatcher()
        {
            if (_classicWatcher == null && _bleWatcher == null) return;
            Log("DeviceWatcher: Stopping watchers...");

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
                    Log($"DeviceWatcher: Stop Classic watcher exception: {ex.Message}");
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
                    Log($"DeviceWatcher: Stop BLE watcher exception: {ex.Message}");
                }
                finally
                {
                    _bleWatcher = null;
                    _bleEnumerationComplete = false;
                }
            }

            Log("DeviceWatcher: Stopped all watchers");
        }

        private void ClassicWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            _classicEnumerationComplete = true;
            Log("DeviceWatcher (Classic): Initial enumeration complete — now live-watching for changes");
        }

        private void ClassicWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            if (!_classicEnumerationComplete) return;
            Log($"DeviceWatcher (Classic): Device connected/paired: '{args.Name}'");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void ClassicWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_classicEnumerationComplete) return;
            Log($"DeviceWatcher (Classic): Device disconnected/unpaired: {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void ClassicWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_classicEnumerationComplete) return;
            if (!args.Properties.ContainsKey("System.Devices.Aep.IsConnected")) return;
            Log($"DeviceWatcher (Classic): IsConnected changed for {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            _bleEnumerationComplete = true;
            Log("DeviceWatcher (BLE): Initial enumeration complete — now live-watching for changes");
        }

        private void BleWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            if (!_bleEnumerationComplete) return;
            Log($"DeviceWatcher (BLE): Device connected/paired: '{args.Name}'");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_bleEnumerationComplete) return;
            Log($"DeviceWatcher (BLE): Device disconnected/unpaired: {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        private void BleWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            if (!_bleEnumerationComplete) return;
            if (!args.Properties.ContainsKey("System.Devices.Aep.IsConnected")) return;
            Log($"DeviceWatcher (BLE): IsConnected changed for {args.Id}");
            TriggerDeviceConnectionChangedDebounced();
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopDeviceWatcher();

            lock (_watcherDebounceLock)
            {
                try { _watcherDebounceTimer?.Dispose(); _watcherDebounceTimer = null; } catch { }
            }

            if (_bluetoothRadio != null)
            {
                try
                {
                    _bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged;
                }
                catch { }
                _bluetoothRadio = null;
            }
            _radioDiscoveryAttempted = false;

            // Release all open BLE handles so Windows can drop those ACL links cleanly.
            ReleaseAllConnectedBleHandles("dispose");

            _batteryCache.Clear();
            lock (_batteryRetryLock)
            {
                _inFlightBatteryQueries.Clear();
            }

            lock (_deviceOpCtsLock)
            {
                try { _deviceOpCts?.Cancel(); _deviceOpCts?.Dispose(); } catch { }
            }

            _deviceSemaphores.Clear();

            // Note on semaphores (_radioInitSemaphore, _adapterToggleSemaphore, _connectSemaphore):
            // We do NOT call .Dispose() on the semaphores here to eliminate race conditions where background
            // tasks (ConnectDeviceAsync, DisconnectDeviceAsync, SetAdapterStateAsync, GetBluetoothRadioAsync)
            // are unwinding their finally blocks upon application shutdown. Calling .Dispose() on a SemaphoreSlim
            // causes subsequent .Release() calls to throw ObjectDisposedException. Since SemaphoreSlim does not
            // allocate unmanaged handles unless AvailableWaitHandle is accessed (which this codebase does not use),
            // omitting Dispose prevents ObjectDisposedException while allowing GC to collect them normally.
            // Additionally, SafeRelease guards all .Release() calls against ObjectDisposedException.
        }
    }
}
