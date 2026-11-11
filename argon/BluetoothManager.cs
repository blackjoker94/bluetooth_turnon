using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using bluetooth_turnon.Services;

namespace bluetooth_turnon
{
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

    public enum AdapterUiState
    {
        NoAdapter,
        Disabled,
        Enabled,
        TurningOn,
        TurningOff,
        Reconnecting
    }

    public enum DeviceOperationStatus
    {
        Success,
        Failed,
        Cancelled,
        AccessDenied,
        AlreadyInDesiredState,
        RadioOff,
        DeviceNotFound,
        TimedOut,
        PeripheralInputProtected
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
        public static DeviceOperationResult RadioOff(string reason = "Bluetooth adapter is off") => new DeviceOperationResult(DeviceOperationStatus.RadioOff, reason);
        public static DeviceOperationResult TimedOut(string reason = "Operation timed out") => new DeviceOperationResult(DeviceOperationStatus.TimedOut, reason);
        public static DeviceOperationResult DeviceNotFound(string reason = "Device not found") => new DeviceOperationResult(DeviceOperationStatus.DeviceNotFound, reason);
        public static DeviceOperationResult PeripheralProtected(string reason = "Radio reset skipped to protect active keyboard/mouse") => new DeviceOperationResult(DeviceOperationStatus.PeripheralInputProtected, reason);

        public string ToUiStatusString()
        {
            switch (Status)
            {
                case DeviceOperationStatus.Success:
                    return Message ?? "Success";
                case DeviceOperationStatus.AlreadyInDesiredState:
                    return Message ?? "Already in desired state";
                case DeviceOperationStatus.RadioOff:
                    return "Bluetooth is turned off";
                case DeviceOperationStatus.AccessDenied:
                    return "Administrator permissions required";
                case DeviceOperationStatus.Cancelled:
                    return "Operation cancelled";
                case DeviceOperationStatus.TimedOut:
                    return "Device did not respond (timed out)";
                case DeviceOperationStatus.DeviceNotFound:
                    return "Device not found or out of range";
                case DeviceOperationStatus.PeripheralInputProtected:
                    return "Bluetooth reset prevented (input device in use)";
                case DeviceOperationStatus.Failed:
                default:
                    return Message ?? "Operation failed";
            }
        }
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
    /// Service facade (Strangler Pattern) providing the single source of truth for all Bluetooth
    /// adapter and device operations. Delegates to specialized services behind clear interfaces.
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

        #region Diagnostic Switches (Public static fields to preserve reflection baseline)
        public static bool EnableStepWake = true;
        public static bool EnableStepCoreAudio = true;
        public static bool EnableStepServiceCycle = true;
        public static bool EnableStepBounce = true;
        public static bool EnableDisconnectCoreAudio = true;
        public static bool EnableDisconnectIoctl = true;
        #endregion

        #region Core Audio Watchdog & Circuit Breaker Settings
        internal static int CoreAudioWatchdogTimeoutMs
        {
            get => ClassicAudioConnectionStrategy.CoreAudioWatchdogTimeoutMs;
            set => ClassicAudioConnectionStrategy.CoreAudioWatchdogTimeoutMs = value;
        }

        internal static bool CoreAudioCircuitBreakerOpen
        {
            get => ClassicAudioConnectionStrategy.CoreAudioCircuitBreakerOpen;
            set => ClassicAudioConnectionStrategy.CoreAudioCircuitBreakerOpen = value;
        }

        internal static int CoreAudioConsecutiveTimeouts => ClassicAudioConnectionStrategy.CoreAudioConsecutiveTimeouts;

        internal static Func<IKsControl, KSIDENTIFIER, int> KsPropertyInvoker
        {
            get => ClassicAudioConnectionStrategy.KsPropertyInvoker;
            set => ClassicAudioConnectionStrategy.KsPropertyInvoker = value;
        }

        internal static bool ExecuteKsPropertyWithWatchdog(IKsControl ksControl, bool connect, string adapterDeviceId)
        {
            return ClassicAudioConnectionStrategy.ExecuteKsPropertyWithWatchdog(ksControl, connect, adapterDeviceId);
        }

        internal static bool TrySendCoreAudioBtCommand(ulong address, string deviceName, bool connect)
        {
            return ClassicAudioConnectionStrategy.TrySendCoreAudioBtCommand(address, deviceName, connect);
        }
        #endregion

        #region Automatic Radio Bounce & Recovery
        public static bool AllowAutomaticRadioBounce
        {
            get => RadioService.AllowAutomaticRadioBounce;
            set => RadioService.AllowAutomaticRadioBounce = value;
        }

        internal static bool LoadAllowAutomaticRadioBounce() => RadioService.LoadAllowAutomaticRadioBounce();
        internal static void SetBounceInProgressMarker(bool inProgress) => RadioService.SetBounceInProgressMarker(inProgress);
        internal static bool GetBounceInProgressMarker() => RadioService.GetBounceInProgressMarker();

        internal static Func<Task<bool>> IncompleteBounceRadioRestorerHook
        {
            get => RadioService.IncompleteBounceRadioRestorerHook;
            set => RadioService.IncompleteBounceRadioRestorerHook = value;
        }

        internal static async Task<bool> CheckAndRecoverIncompleteBounceAsync(BluetoothManager manager = null)
        {
            return await RadioService.CheckAndRecoverIncompleteBounceAsync(manager?._radioService).ConfigureAwait(false);
        }
        #endregion

        #region Battery Configuration & Policy Delegations
        public static int BatteryCachedQueryBudgetMs
        {
            get => BatteryPolicy.BatteryCachedQueryBudgetMs;
            set => BatteryPolicy.BatteryCachedQueryBudgetMs = value;
        }

        public static int BatteryUncachedQueryBudgetMs
        {
            get => BatteryPolicy.BatteryUncachedQueryBudgetMs;
            set => BatteryPolicy.BatteryUncachedQueryBudgetMs = value;
        }

        public static int MaxBatteryRetries
        {
            get => BatteryPolicy.MaxBatteryRetries;
            set => BatteryPolicy.MaxBatteryRetries = value;
        }

        public static TimeSpan BatteryUnsupportedCacheDuration
        {
            get => BatteryPolicy.BatteryUnsupportedCacheDuration;
            set => BatteryPolicy.BatteryUnsupportedCacheDuration = value;
        }

        public static TimeSpan BatteryLastGoodRetentionDuration
        {
            get => BatteryPolicy.BatteryLastGoodRetentionDuration;
            set => BatteryPolicy.BatteryLastGoodRetentionDuration = value;
        }

        public static TimeSpan GetTransientBackoff(int failureCount) => BatteryPolicy.GetTransientBackoff(failureCount);
        public static ulong TryExtractAddressFromBleDeviceId(string deviceId) => BatteryPolicy.TryExtractAddressFromBleDeviceId(deviceId);
        #endregion

        #region Classification & Routing Policy Delegations
        public static BluetoothDeviceType ClassifyByAppearance(ushort category, ushort subCategory) => DeviceClassifier.ClassifyByAppearance(category, subCategory);
        public static BluetoothDeviceType ClassifyByCod(int major, int minor) => DeviceClassifier.ClassifyByCod(major, minor);
        public static BluetoothDeviceType ClassifyByName(string name) => DeviceClassifier.ClassifyByName(name);

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

        public static DeviceTransportCapability ClassifyTransportCapability(
            bool isBlePaired,
            bool installedServicesReadSuccess,
            IReadOnlyCollection<Guid> installedServices,
            int codMajor)
        {
            return ConnectRoutingPolicy.ClassifyTransportCapability(isBlePaired, installedServicesReadSuccess, installedServices, codMajor);
        }

        public static ConnectionRoute DetermineConnectionRoute(
            DeviceTransportCapability capability,
            int codMajor,
            bool isPeripheral)
        {
            return ConnectRoutingPolicy.DetermineConnectionRoute(capability, codMajor, isPeripheral);
        }
        #endregion

        #region Logging Forwarders
        public static string MaskAddress(string input) => AsyncFileLogger.MaskAddress(input);
        public static void Log(string msg) => AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, msg);
        public static void Log(LogLevel level, string msg) => AsyncFileLogger.Log((AsyncFileLogger.LogLevel)level, msg);
        #endregion

        #region Testing Probes
        internal static Func<ulong, (bool hasPeripherals, string name, BluetoothDeviceType type)> ConnectedPeripheralsProbe { get; set; }
        internal ConcurrentDictionary<ulong, BatteryCacheEntry> BatteryCache => _batteryService.BatteryCache;
        internal HashSet<ulong> InFlightBatteryQueries => _batteryService.InFlightBatteryQueries;
        #endregion

        private readonly IRadioService _radioService;
        private readonly IBatteryService _batteryService;
        private readonly IDeviceEnumerator _deviceEnumerator;
        private readonly IConnectionService _connectionService;
        private bool _disposed;

        public event EventHandler<AdapterStateChangedEventArgs> AdapterStateChanged;
        public event EventHandler DeviceConnectionChanged;
        public event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;

        public bool IsRadioBounceInProgress
        {
            get => _radioService.IsRadioBounceInProgress;
            private set
            {
                if (_radioService is RadioService rs)
                {
                    rs.IsRadioBounceInProgress = value;
                }
            }
        }

        public bool UserRequestedAdapterOff
        {
            get => _radioService.UserRequestedAdapterOff;
            set => _radioService.UserRequestedAdapterOff = value;
        }

        public BluetoothManager()
            : this(null, null, null, null)
        {
        }

        internal BluetoothManager(
            IRadioService radioService,
            IBatteryService batteryService,
            IDeviceEnumerator deviceEnumerator,
            IConnectionService connectionService)
        {
            _radioService = radioService ?? new RadioService();
            _batteryService = batteryService ?? new BatteryService();
            _connectionService = connectionService ?? new ConnectionService(_radioService, _batteryService);
            _deviceEnumerator = deviceEnumerator ?? new DeviceEnumerator(_radioService, _batteryService, _connectionService);

            if (_batteryService is BatteryService concreteBattery)
            {
                concreteBattery.SetDeviceConnectionChecker(_deviceEnumerator.IsDeviceConnectedAsync);
                concreteBattery.SetCachedBleDeviceGetter(addr =>
                {
                    _connectionService.ConnectedBleDevices.TryGetValue(addr, out var dev);
                    return dev;
                });
            }

            if (_connectionService is ConnectionService concreteConn)
            {
                concreteConn.SetDeviceEnumerator(_deviceEnumerator);
            }

            // Wire events
            _radioService.AdapterStateChanged += (s, e) => AdapterStateChanged?.Invoke(this, e);
            _radioService.OperationProgressChanged += (s, e) => OperationProgressChanged?.Invoke(this, e);
            _connectionService.OperationProgressChanged += (s, e) => OperationProgressChanged?.Invoke(this, e);
            _deviceEnumerator.DeviceConnectionChanged += (s, e) => DeviceConnectionChanged?.Invoke(this, e);

            if (_batteryService is BatteryService bs)
            {
                bs.BatteryUpdated += (addr, lvl) =>
                {
                    _deviceEnumerator?.InvalidateDeviceSnapshotCache();
                    DeviceConnectionChanged?.Invoke(this, EventArgs.Empty);
                };
            }

            _radioService.RadioTurnedOff += (reason) =>
            {
                CancelDeviceOperations(userRequestedAdapterOff: false);
                _connectionService.ReleaseAllConnectedBleHandles(reason);
                _batteryService.HandleAdapterTurnedOff();
            };

            Task.Run(async () =>
            {
                await RadioService.CheckAndRecoverIncompleteBounceAsync(_radioService).ConfigureAwait(false);
                ClassicAudioConnectionStrategy.RecoverPendingProfileRestores();
            });
        }

        public async Task<bool?> GetAdapterStateAsync(CancellationToken ct = default)
        {
            return await _radioService.GetAdapterStateAsync(ct).ConfigureAwait(false);
        }

        public async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default)
        {
            return await _radioService.SetAdapterStateAsync(enable, ct, isUserInitiated: true).ConfigureAwait(false);
        }

        internal async Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct, bool isUserInitiated)
        {
            return await _radioService.SetAdapterStateAsync(enable, ct, isUserInitiated).ConfigureAwait(false);
        }

        public async Task<bool> ManualResetBluetoothAsync(CancellationToken ct = default)
        {
            return await _radioService.ManualResetBluetoothAsync(ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default)
        {
            return await _deviceEnumerator.GetPairedDevicesAsync(ct).ConfigureAwait(false);
        }

        public async Task<bool?> IsDeviceConnectedAsync(ulong address, CancellationToken ct = default)
        {
            return await _deviceEnumerator.IsDeviceConnectedAsync(address, ct).ConfigureAwait(false);
        }

        public void StartDeviceWatcher()
        {
            _deviceEnumerator.StartDeviceWatcher();
        }

        public void StopDeviceWatcher()
        {
            _deviceEnumerator.StopDeviceWatcher();
        }

        internal bool HasConnectedPeripherals(ulong currentTargetAddress, out string peripheralName, out BluetoothDeviceType peripheralType)
        {
            return _deviceEnumerator.HasConnectedPeripherals(currentTargetAddress, out peripheralName, out peripheralType);
        }

        internal bool HasConnectedPeripherals(ulong currentTargetAddress)
        {
            return _deviceEnumerator.HasConnectedPeripherals(currentTargetAddress, out _, out _);
        }

        internal async Task<(bool hasPeripherals, string name, BluetoothDeviceType type)> CheckConnectedPeripheralsAsync(ulong currentTargetAddress, CancellationToken ct = default)
        {
            return await _deviceEnumerator.CheckConnectedPeripheralsAsync(currentTargetAddress, ct).ConfigureAwait(false);
        }

        public void InvalidateDeviceSnapshotCache()
        {
            _deviceEnumerator?.InvalidateDeviceSnapshotCache();
        }

        public async Task<bool> ConnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            return await _connectionService.ConnectDeviceAsync(address, ct).ConfigureAwait(false);
        }

        internal async Task<DeviceOperationResult> ConnectDeviceWithResultAsync(ulong address, CancellationToken ct = default)
        {
            return await _connectionService.ConnectDeviceWithResultAsync(address, ct).ConfigureAwait(false);
        }

        public async Task<bool> DisconnectDeviceAsync(ulong address, CancellationToken ct = default)
        {
            return await _connectionService.DisconnectDeviceAsync(address, ct).ConfigureAwait(false);
        }

        internal async Task<DeviceOperationResult> DisconnectDeviceWithResultAsync(ulong address, CancellationToken ct = default)
        {
            return await _connectionService.DisconnectDeviceWithResultAsync(address, ct).ConfigureAwait(false);
        }

        public void CancelDeviceOperations(bool userRequestedAdapterOff = false)
        {
            _connectionService.CancelDeviceOperations(userRequestedAdapterOff);
        }

        internal void HandleDeviceDisconnected(ulong address)
        {
            _batteryService.HandleDeviceDisconnected(address);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { (_batteryService as IDisposable)?.Dispose(); } catch { }
            try { _deviceEnumerator?.Dispose(); } catch { }
            try { _connectionService?.Dispose(); } catch { }
            try { _radioService?.Dispose(); } catch { }
        }
    }
}
