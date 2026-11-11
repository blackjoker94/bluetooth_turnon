using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace bluetooth_turnon.Services
{
    public sealed class ClassicAudioConnectionStrategy : IConnectionStrategy
    {
        private const string PendingProfilesRegistryPath = @"SOFTWARE\Connectify\PendingProfileRestores";

        private static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        private const uint KSPROPERTY_TYPE_GET = 0x00000001;
        private const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
        private const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
        private const int EDataFlow_eAll = 2;
        private const uint DEVICE_STATEMASK_ALL = 0x0000000F;
        private const uint STGM_READ = 0x00000000;
        private const uint CLSCTX_ALL = 23;

        #region Core Audio Watchdog & Circuit Breaker Settings
        public static int CoreAudioWatchdogTimeoutMs = 5000;
        private static int _coreAudioConsecutiveTimeouts = 0;
        private static volatile bool _coreAudioCircuitBreakerOpen = false;
        private static DateTime _circuitBreakerTripUtc = DateTime.MinValue;
        private const int CoreAudioMaxConsecutiveTimeouts = 3;

        public static bool CoreAudioCircuitBreakerOpen
        {
            get
            {
                if (_coreAudioCircuitBreakerOpen)
                {
                    if (_circuitBreakerTripUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - _circuitBreakerTripUtc).TotalSeconds >= 60)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "  CoreAudio: Circuit breaker cooldown (60s) elapsed. Transitioning to half-open trial.");
                        _coreAudioCircuitBreakerOpen = false;
                        _circuitBreakerTripUtc = DateTime.MinValue;
                        return false;
                    }
                    return true;
                }
                return false;
            }
            set
            {
                _coreAudioCircuitBreakerOpen = value;
                if (value)
                {
                    _circuitBreakerTripUtc = DateTime.UtcNow;
                }
                else
                {
                    _circuitBreakerTripUtc = DateTime.MinValue;
                    Interlocked.Exchange(ref _coreAudioConsecutiveTimeouts, 0);
                }
            }
        }
        public static int CoreAudioConsecutiveTimeouts => Thread.VolatileRead(ref _coreAudioConsecutiveTimeouts);
        internal static Func<IKsControl, KSIDENTIFIER, int> KsPropertyInvoker { get; set; }
        #endregion

        private readonly IRadioService _radioService;
        private readonly IBatteryService _batteryService;
        private readonly Func<ulong, (bool hasPeripherals, string name, BluetoothDeviceType type)> _peripheralsChecker;
        private readonly Action<ulong, string> _progressNotifier;

        public ClassicAudioConnectionStrategy(
            IRadioService radioService,
            IBatteryService batteryService,
            Func<ulong, (bool hasPeripherals, string name, BluetoothDeviceType type)> peripheralsChecker = null,
            Action<ulong, string> progressNotifier = null)
        {
            _radioService = radioService ?? throw new ArgumentNullException(nameof(radioService));
            _batteryService = batteryService;
            _peripheralsChecker = peripheralsChecker;
            _progressNotifier = progressNotifier;
        }

        public bool CanHandle(ConnectionRoute route) => route == ConnectionRoute.ClassicWithAudio;

        public static void RecordPendingProfileRestore(ulong address, Guid profile)
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

        public static void ClearPendingProfileRestore(ulong address, Guid profile)
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

        public static void RecoverPendingProfileRestores()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PendingProfilesRegistryPath, true))
                {
                    if (key == null) return;
                    string[] names = key.GetValueNames();
                    if (names == null || names.Length == 0) return;

                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"Startup: Found {names.Length} pending profile restore marker(s) from previous abnormal shutdown.");
                    using (var client = new BluetoothClient())
                    {
                        var paired = client.PairedDevices;
                        foreach (string name in names)
                        {
                            bool shouldDelete = false;
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
                                        shouldDelete = true;
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Startup recovery: Restored service {profile} on device '{dev.DeviceName}' ({addr:X12})");
                                    }
                                    else
                                    {
                                        shouldDelete = true;
                                    }
                                }
                                else
                                {
                                    shouldDelete = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  Startup recovery exception for {name}: {ex.Message}");
                            }
                            finally
                            {
                                if (shouldDelete)
                                {
                                    try { key.DeleteValue(name, false); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"RecoverPendingProfileRestores exception: {ex.Message}");
            }
        }

        public static async Task WakeBasebandLinkAsync(ulong address, CancellationToken ct)
        {
            BluetoothDevice winRtDevice = null;
            try
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Waking baseband link for {address:X12} via WinRT uncached RFCOMM discovery...");
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
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  WinRT RFCOMM wake completed. Found {rfcommResult?.Services?.Count ?? 0} service(s), Error={rfcommResult?.Error}");
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
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  WinRT BluetoothDevice was null for address {address:X12}.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "  WinRT baseband link wake timed out (2s) or was cancelled; proceeding with connection.");
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  WinRT baseband link wake exception: {ex.Message}; proceeding with connection.");
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

        public static bool TrySendCoreAudioBtCommand(ulong address, string deviceName, bool connect)
        {
            if (address == 0) return false;

            if (CoreAudioCircuitBreakerOpen)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, "  CoreAudio: Circuit breaker is OPEN (skipping Core Audio for this session due to repeated timeouts).");
                return false;
            }

            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  CoreAudio: Attempting {(connect ? "reconnect" : "disconnect")} via IKsControl for {address:X12} ('{deviceName}')...");
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator == null) return false;

                int hr = enumerator.EnumAudioEndpoints(EDataFlow_eAll, DEVICE_STATEMASK_ALL, out collection);
                if (hr != 0 || collection == null)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  CoreAudio: EnumAudioEndpoints failed with hr=0x{hr:X8}");
                    return false;
                }

                uint count = 0;
                collection.GetCount(out count);
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Found {count} audio endpoint(s)");

                string addrHex12 = address.ToString("X12").ToUpperInvariant();
                bool anyTriggered = false;
                var triggeredAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice endpoint = null;
                    try
                    {
                        int hrItem = collection.Item(i, out endpoint);
                        if (hrItem != 0 || endpoint == null) continue;

                        bool matchedByAddress = false;
                        bool matchedByName = false;

                        string endpointId;
                        if (endpoint.GetId(out endpointId) == 0 && !string.IsNullOrEmpty(endpointId))
                        {
                            string idUpper = endpointId.ToUpperInvariant();
                            if (idUpper.Contains(addrHex12))
                            {
                                matchedByAddress = true;
                            }
                        }

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
                                                        fn.IndexOf($"({deviceName}", StringComparison.OrdinalIgnoreCase) >= 0)
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

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  CoreAudio: Endpoint matched: '{endpointId}' (byAddress={matchedByAddress}, byName={matchedByName})");

                        Guid iidTopology = typeof(IDeviceTopology).GUID;
                        object topologyObj = null;
                        int hrTopology = endpoint.Activate(ref iidTopology, CLSCTX_ALL, IntPtr.Zero, out topologyObj);
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: endpoint.Activate(IDeviceTopology) returned hr=0x{hrTopology:X8}");
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
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: endpointTopology.GetConnectorCount returned hr=0x{hrConnCount:X8}, count={connectorCount}");
                            if (hrConnCount != 0 || connectorCount == 0)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Endpoint '{endpointId}' has 0 connectors; skipping.");
                                continue;
                            }

                            object endpointConnectorObj = null;
                            int hrGetConn = endpointTopology.GetConnector(0, out endpointConnectorObj);
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: endpointTopology.GetConnector(0) returned hr=0x{hrGetConn:X8}");
                            if (hrGetConn != 0 || endpointConnectorObj == null)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Endpoint '{endpointId}' GetConnector(0) failed; skipping.");
                                continue;
                            }

                            try
                            {
                                IConnector endpointConnector = AsConnector(endpointConnectorObj);
                                if (endpointConnector == null)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Endpoint '{endpointId}' connector 0 does not implement IConnector; skipping.");
                                    continue;
                                }

                                object adapterConnectorObj = null;
                                int hrGetConnectedTo = endpointConnector.GetConnectedTo(out adapterConnectorObj);
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: endpointConnector.GetConnectedTo returned hr=0x{hrGetConnectedTo:X8}");
                                if (hrGetConnectedTo != 0 || adapterConnectorObj == null)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Endpoint '{endpointId}' connector is not connected to an adapter; skipping.");
                                    continue;
                                }

                                try
                                {
                                    IPart adapterPart = AsPart(adapterConnectorObj);
                                    if (adapterPart == null)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "  CoreAudio: Connected adapter object does not implement IPart; skipping.");
                                        continue;
                                    }

                                    IDeviceTopology adapterTopology = null;
                                    int hrAdapterTopo = adapterPart.GetTopologyObject(out adapterTopology);
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: adapterPart.GetTopologyObject returned hr=0x{hrAdapterTopo:X8}");
                                    if (hrAdapterTopo != 0 || adapterTopology == null)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, "  CoreAudio: GetTopologyObject failed on adapter part; skipping.");
                                        continue;
                                    }

                                    try
                                    {
                                        string adapterDeviceId = null;
                                        int hrDeviceId = adapterTopology.GetDeviceId(out adapterDeviceId);
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: adapterTopology.GetDeviceId returned hr=0x{hrDeviceId:X8}, adapterDeviceId='{adapterDeviceId}'");
                                        if (hrDeviceId != 0 || string.IsNullOrEmpty(adapterDeviceId))
                                            continue;

                                        bool isBtAdapter = adapterDeviceId.IndexOf(addrHex12, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                           adapterDeviceId.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                           adapterDeviceId.IndexOf("BLUETOOTH", StringComparison.OrdinalIgnoreCase) >= 0;
                                        if (!isBtAdapter)
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Skipping non-Bluetooth adapter '{adapterDeviceId}' for endpoint '{endpointId}'");
                                            continue;
                                        }

                                        if (triggeredAdapters.Contains(adapterDeviceId))
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Skipping already triggered adapter '{adapterDeviceId}' for endpoint '{endpointId}'");
                                            continue;
                                        }

                                        Guid iidKsControl = typeof(IKsControl).GUID;
                                        object ksControlObj = null;
                                        IKsControl ksControl = null;

                                        int hrPartActivate = adapterPart.Activate(CLSCTX_ALL, ref iidKsControl, out ksControlObj);
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: adapterPart.Activate(IKsControl) returned hr=0x{hrPartActivate:X8}");
                                        if (hrPartActivate == 0 && ksControlObj != null)
                                        {
                                            ksControl = ksControlObj as IKsControl;
                                            if (ksControl == null)
                                            {
                                                Marshal.ReleaseComObject(ksControlObj);
                                                ksControlObj = null;
                                            }
                                        }

                                        IMMDevice adapterDevice = null;
                                        if (ksControl == null)
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: Attempting optional GetDevice('{adapterDeviceId}')...");
                                            int hrGetDevice = enumerator.GetDevice(adapterDeviceId, out adapterDevice);
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: enumerator.GetDevice('{adapterDeviceId}') returned hr=0x{hrGetDevice:X8}");
                                            if (hrGetDevice == 0 && adapterDevice != null)
                                            {
                                                int hrDeviceActivate = adapterDevice.Activate(ref iidKsControl, CLSCTX_ALL, IntPtr.Zero, out ksControlObj);
                                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: adapterDevice.Activate(IKsControl) returned hr=0x{hrDeviceActivate:X8}");
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
                                                triggeredAdapters.Add(adapterDeviceId);
                                                break;
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
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  CoreAudio endpoint loop exception: {ex.Message}");
                    }
                    finally
                    {
                        if (endpoint != null) Marshal.ReleaseComObject(endpoint);
                    }
                }

                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  CoreAudio: Completed. anyTriggered={anyTriggered}");
                return anyTriggered;
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  CoreAudio outer exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (collection != null) { try { Marshal.ReleaseComObject(collection); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
        }

        internal static bool ExecuteKsPropertyWithWatchdog(IKsControl ksControl, bool connect, string adapterDeviceId)
        {
            if (CoreAudioCircuitBreakerOpen)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  CoreAudio: Circuit breaker is OPEN. Skipping KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}'.");
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
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  CoreAudio: KsProperty thread exception: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (ksControl != null)
                        {
                            Marshal.ReleaseComObject(ksControl);
                        }
                    }
                    catch { }
                    try
                    {
                        doneEvent.Set();
                    }
                    catch (ObjectDisposedException) { }
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
                int timeouts = Interlocked.Increment(ref _coreAudioConsecutiveTimeouts);
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  CoreAudio: KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}' TIMED OUT after {CoreAudioWatchdogTimeoutMs}ms. Abandoning thread (consecutive={timeouts}).");
                if (timeouts >= CoreAudioMaxConsecutiveTimeouts)
                {
                    _coreAudioCircuitBreakerOpen = true;
                    _circuitBreakerTripUtc = DateTime.UtcNow;
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  CoreAudio: Circuit breaker TRIPPED OPEN after {timeouts} consecutive timeouts.");
                }
                Task.Run(() =>
                {
                    try
                    {
                        if (workerThread.Join(30000))
                        {
                            doneEvent.Dispose();
                        }
                    }
                    catch { }
                });
                return false;
            }
            else
            {
                Interlocked.Exchange(ref _coreAudioConsecutiveTimeouts, 0);
                try { doneEvent.Dispose(); } catch { }
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio: KsProperty({(connect ? "RECONNECT" : "DISCONNECT")}) on adapter '{adapterDeviceId}' returned hr=0x{ksHr:X8}");
                return ksHr >= 0;
            }
        }

        private async Task<bool> CheckIsBleSupportedAsync(ulong address)
        {
            if (_batteryService != null && _batteryService.BatteryCache.TryGetValue(address, out var entry))
            {
                if (entry.IsClassicOnly) return false;
            }

            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address);
                var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);
                if (devices != null && devices.Count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"CheckIsBleSupportedAsync({address:X12}) exception: {ex.Message}");
            }

            return false;
        }

        public async Task<DeviceOperationResult> ConnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicAudioConnectionStrategy.ConnectAsync called for {address:X12}");

            return await Task.Run(async () =>
            {
                try
                {
                    bool isBleSupported = await CheckIsBleSupportedAsync(address).ConfigureAwait(false);
                    string targetDeviceName;
                    using (var client = new BluetoothClient())
                    {
                        var paired = client.PairedDevices;
                        var device = paired?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                        if (device == null)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicAudio: device {address:X12} not found in PairedDevices.");
                            return DeviceOperationResult.DeviceNotFound($"Classic device {address:X12} not found in PairedDevices.");
                        }

                        targetDeviceName = device.DeviceName ?? (name ?? "Audio Device");
                        device.Refresh();
                        if (device.Connected)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicAudio: '{targetDeviceName}' is already connected.");
                            return DeviceOperationResult.AlreadyConnected($"'{targetDeviceName}' is already connected.");
                        }

                        // Step 2: Wake baseband link
                        if (BluetoothManager.EnableStepWake)
                        {
                            var swWake = Stopwatch.StartNew();
                            await WakeBasebandLinkAsync(address, ct).ConfigureAwait(false);
                            swWake.Stop();
                            device.Refresh();
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  [Step 2: Wake] elapsed={swWake.ElapsedMilliseconds}ms, Connected={device.Connected}");
                            if (device.Connected)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetDeviceName}' connected after baseband wake in {swWake.ElapsedMilliseconds}ms.");
                                _batteryService?.ScheduleDeferredBatteryQuery(address, targetDeviceName, isClassicOnly: !isBleSupported, immediate: true);
                                return DeviceOperationResult.Succeeded($"'{targetDeviceName}' connected after baseband wake in {swWake.ElapsedMilliseconds}ms.");
                            }
                        }

                        // Step 3: Best-effort Windows Core Audio (IKsControl)
                        if (BluetoothManager.EnableStepCoreAudio)
                        {
                            var swCore = Stopwatch.StartNew();
                            bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, targetDeviceName, connect: true);
                            swCore.Stop();
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  [Step 3: CoreAudio] triggered={coreAudioTriggered}, elapsed={swCore.ElapsedMilliseconds}ms");

                            if (coreAudioTriggered)
                            {
                                for (int caAttempt = 1; caAttempt <= 3; caAttempt++)
                                {
                                    await Task.Delay(500, ct).ConfigureAwait(false);
                                    try
                                    {
                                        device.Refresh();
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio reconnect verify attempt {caAttempt}/3: Connected={device.Connected}");
                                        if (device.Connected)
                                        {
                                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetDeviceName}' reconnected successfully via CoreAudio in {swCore.ElapsedMilliseconds}ms.");
                                            _batteryService?.ScheduleDeferredBatteryQuery(address, targetDeviceName, isClassicOnly: !isBleSupported, immediate: true);
                                            return DeviceOperationResult.Succeeded($"'{targetDeviceName}' reconnected successfully via CoreAudio in {swCore.ElapsedMilliseconds}ms.");
                                        }
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch (Exception ex)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio verify attempt {caAttempt} exception: {ex.Message}");
                                    }
                                }
                            }
                        }

                        // Step 4: Two-pass service state cycle
                        if (BluetoothManager.EnableStepServiceCycle)
                        {
                            var swCycle = Stopwatch.StartNew();
                            var candidateProfiles = new[]
                            {
                                BluetoothService.AudioSink,
                                BluetoothService.Handsfree,
                                BluetoothService.Headset
                            };

                            var profilesToCycle = new List<Guid>();
                            try
                            {
                                var installed = device.InstalledServices;
                                if (installed != null && installed.Count > 0)
                                {
                                    foreach (var cand in candidateProfiles)
                                    {
                                        if (installed.Contains(cand))
                                        {
                                            profilesToCycle.Add(cand);
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (profilesToCycle.Count == 0)
                            {
                                profilesToCycle.Add(BluetoothService.AudioSink);
                            }

                            var disabledProfiles = new List<Guid>();
                            try
                            {
                                foreach (var profile in profilesToCycle)
                                {
                                    if (ct.IsCancellationRequested) break;
                                    try
                                    {
                                        RecordPendingProfileRestore(address, profile);
                                        device.SetServiceState(profile, false);
                                        disabledProfiles.Add(profile);
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  SetServiceState({profile}, false) succeeded for '{targetDeviceName}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        ClearPendingProfileRestore(address, profile);
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  SetServiceState({profile}, false) note: {ex.Message}");
                                    }
                                }

                                if (disabledProfiles.Count > 0)
                                {
                                    await Task.Delay(200, ct).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                foreach (var profile in disabledProfiles)
                                {
                                    try
                                    {
                                        device.SetServiceState(profile, true);
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  SetServiceState({profile}, true) restored for '{targetDeviceName}'");
                                    }
                                    catch (Exception ex)
                                    {
                                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  SetServiceState({profile}, true) cleanup note: {ex.Message}");
                                    }
                                    finally
                                    {
                                        ClearPendingProfileRestore(address, profile);
                                    }
                                }
                            }

                            ct.ThrowIfCancellationRequested();

                            await Task.Delay(500, ct).ConfigureAwait(false);

                            bool connected = false;
                            for (int attempt = 1; attempt <= 5; attempt++)
                            {
                                try
                                {
                                    if (attempt > 1) await Task.Delay(500, ct).ConfigureAwait(false);
                                    device.Refresh();
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Connection verify attempt {attempt}/5: Connected={device.Connected}");
                                    if (device.Connected)
                                    {
                                        connected = true;
                                        break;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Connection verify attempt {attempt} exception: {ex.Message}");
                                }
                            }

                            swCycle.Stop();
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  [Step 4: ServiceCycle] elapsed={swCycle.ElapsedMilliseconds}ms, Connected={connected}");

                            if (connected)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetDeviceName}' connected successfully via service state cycle in {swCycle.ElapsedMilliseconds}ms.");
                                _batteryService?.ScheduleDeferredBatteryQuery(address, targetDeviceName, isClassicOnly: !isBleSupported, immediate: true);
                                return DeviceOperationResult.Succeeded($"'{targetDeviceName}' connected successfully via service state cycle in {swCycle.ElapsedMilliseconds}ms.");
                            }
                        }
                    }

                    // Step 6: Controlled Radio Bounce
                    if (ct.IsCancellationRequested || _radioService.UserRequestedAdapterOff)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "  ConnectAsync: User requested adapter OFF or cancelled. Skipping Step 6 (Radio Bounce).");
                        return DeviceOperationResult.Cancelled("User requested adapter OFF or cancelled.");
                    }

                    if (!RadioService.AllowAutomaticRadioBounce)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "  ConnectAsync: Automatic radio bounce is disabled by default. Skipping Step 6 (Radio Bounce).");
                        return DeviceOperationResult.Failed("Automatic radio bounce is disabled by default.");
                    }

                    if (_peripheralsChecker != null)
                    {
                        var periph = _peripheralsChecker(address);
                        if (periph.hasPeripherals)
                        {
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ConnectAsync: Connected input peripheral '{periph.name}' ({periph.type}) detected. Skipping Step 6 (Radio Bounce).");
                            return DeviceOperationResult.PeripheralProtected($"Connected input peripheral '{periph.name}' ({periph.type}) detected. Skipping radio bounce.");
                        }
                    }

                    if (!BluetoothManager.EnableStepBounce)
                    {
                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, "  ConnectAsync: Step 6 (Radio Bounce) SKIPPED via diagnostic switch.");
                        return DeviceOperationResult.Failed("Step 6 (Radio Bounce) skipped via diagnostic switch.");
                    }

                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  Steps 1-4 failed to connect '{targetDeviceName}'. Initiating Step 6: Controlled Radio Bounce...");
                    _progressNotifier?.Invoke(address, "Reconnecting Bluetooth...");

                    bool resetOk = await _radioService.ManualResetBluetoothAsync(ct).ConfigureAwait(false);
                    if (!resetOk) return DeviceOperationResult.Failed("Bluetooth radio reset failed.");

                    await Task.Delay(1500, ct).ConfigureAwait(false);

                    for (int attempt = 1; attempt <= 8; attempt++)
                    {
                        if (_radioService.UserRequestedAdapterOff) return DeviceOperationResult.Cancelled("User requested adapter OFF during radio bounce.");
                        await Task.Delay(1000, ct).ConfigureAwait(false);

                        using (var bounceClient = new BluetoothClient())
                        {
                            var bounceDevice = bounceClient.PairedDevices?.FirstOrDefault(d => d.DeviceAddress.ToUInt64() == address);
                            if (bounceDevice != null)
                            {
                                bounceDevice.Refresh();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Radio bounce verify attempt {attempt}/8: Connected={bounceDevice.Connected}");
                                if (bounceDevice.Connected)
                                {
                                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{bounceDevice.DeviceName}' connected successfully after radio bounce on attempt {attempt}/8.");
                                    _batteryService?.ScheduleDeferredBatteryQuery(address, bounceDevice.DeviceName, isClassicOnly: !isBleSupported, immediate: true);
                                    return DeviceOperationResult.Succeeded($"'{bounceDevice.DeviceName}' connected successfully after radio bounce on attempt {attempt}/8.");
                                }
                            }
                        }
                    }

                    return DeviceOperationResult.TimedOut($"Connection timed out after radio bounce for '{targetDeviceName}'.");
                }
                catch (OperationCanceledException)
                {
                    return DeviceOperationResult.Cancelled();
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  ClassicAudioConnectionStrategy.ConnectAsync exception: {ex.Message}");
                    return DeviceOperationResult.Failed(ex.Message);
                }
            }, ct).ConfigureAwait(false);
        }

        public async Task<DeviceOperationResult> DisconnectAsync(ulong address, string name, CancellationToken ct)
        {
            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicAudioConnectionStrategy.DisconnectAsync called for {address:X12}");

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
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicAudio: device {address:X12} not found in PairedDevices.");
                            return DeviceOperationResult.DeviceNotFound($"Classic device {address:X12} not found in PairedDevices.");
                        }

                        string targetName = device.DeviceName ?? (name ?? "Audio Device");
                        device.Refresh();
                        if (!device.Connected)
                        {
                            _batteryService?.HandleDeviceDisconnected(address);
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  ClassicAudio: '{targetName}' is already disconnected.");
                            return DeviceOperationResult.AlreadyDisconnected($"'{targetName}' is already disconnected.");
                        }

                        // Step 2: Core Audio disconnect
                        if (BluetoothManager.EnableDisconnectCoreAudio)
                        {
                            try
                            {
                                var swCad = Stopwatch.StartNew();
                                bool coreAudioTriggered = TrySendCoreAudioBtCommand(address, targetName, connect: false);
                                swCad.Stop();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  [Disconnect Step 2: CoreAudio] triggered={coreAudioTriggered}, elapsed={swCad.ElapsedMilliseconds}ms");
                            }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  CoreAudio disconnect note: {ex.Message}");
                            }
                        }

                        // Step 3: IOCTL disconnect
                        if (BluetoothManager.EnableDisconnectIoctl)
                        {
                            try
                            {
                                var swIoctl = Stopwatch.StartNew();
                                bool ioctlSuccess = NativeMethods.DisconnectDeviceViaIoctl(address);
                                swIoctl.Stop();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  [Disconnect Step 3: IOCTL] result={ioctlSuccess}, elapsed={swIoctl.ElapsedMilliseconds}ms");
                            }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Native IOCTL disconnect note: {ex.Message}");
                            }
                        }

                        // Step 4: Polling window
                        bool disconnected = false;
                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            await Task.Delay(800, ct).ConfigureAwait(false);
                            try
                            {
                                device.Refresh();
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Disconnection verify attempt {attempt}/5: Connected={device.Connected}");
                                if (!device.Connected)
                                {
                                    disconnected = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Debug, $"  Disconnection verify attempt {attempt} exception: {ex.Message}");
                            }
                        }

                        if (disconnected || !device.Connected)
                        {
                            _batteryService?.HandleDeviceDisconnected(address);
                            AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Info, $"  '{targetName}' disconnected successfully.");
                            return DeviceOperationResult.Succeeded($"'{targetName}' disconnected successfully.");
                        }

                        AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Warning, $"  ClassicAudio: Disconnect failed for '{targetName}'.");
                        return DeviceOperationResult.Failed($"Disconnect failed for '{targetName}'.");
                    }
                }
                catch (OperationCanceledException)
                {
                    return DeviceOperationResult.Cancelled();
                }
                catch (Exception ex)
                {
                    AsyncFileLogger.Log(AsyncFileLogger.LogLevel.Error, $"  ClassicAudio.DisconnectAsync exception: {ex.Message}");
                    return DeviceOperationResult.Failed(ex.Message);
                }
            }, ct).ConfigureAwait(false);
        }
    }
}
