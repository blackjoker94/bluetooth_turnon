using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private bool _isUpdatingUiFromState;
        private bool _isTogglingAdapter;
        // THREAD SAFETY: _isRefreshingDevices and _refreshPending must ONLY be accessed on the UI thread.
        // Access is marshaled via SafeBeginInvokeAsync to prevent cross-thread race conditions.
        private bool _isRefreshingDevices;
        private bool _refreshPending;
        private bool _forceRefreshPending;
        private int _refreshGeneration;
        private readonly DevicePresenter _devicePresenter;
        private readonly Dictionary<ulong, DeviceCardView> _cardViews = new Dictionary<ulong, DeviceCardView>();
        private Label _emptyLabel;
        private readonly System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();

        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private Timer _refreshTimer;
        private Timer _deviceChangeDebounceTimer;

        private AdapterUiState _currentUiState = AdapterUiState.NoAdapter;
        private bool _needsRefreshOnRestore = false;
        private bool IsAppVisible => this.Visible && this.WindowState != FormWindowState.Minimized;

        private void SetUiState(AdapterUiState state)
        {
            _currentUiState = state;
            switch (state)
            {
                case AdapterUiState.Enabled:
                    labelStatus.Text = "Enabled";
                    labelStatus.ForeColor = Color.FromArgb(100, 180, 255);
                    break;
                case AdapterUiState.Disabled:
                    labelStatus.Text = "Disabled";
                    labelStatus.ForeColor = Color.FromArgb(200, 80, 80);
                    break;
                case AdapterUiState.NoAdapter:
                    labelStatus.Text = "No Adapter";
                    labelStatus.ForeColor = Color.FromArgb(140, 140, 150);
                    break;
                case AdapterUiState.TurningOn:
                    labelStatus.Text = "Turning On...";
                    labelStatus.ForeColor = Color.FromArgb(255, 200, 100);
                    break;
                case AdapterUiState.TurningOff:
                    labelStatus.Text = "Turning Off...";
                    labelStatus.ForeColor = Color.FromArgb(255, 200, 100);
                    break;
                case AdapterUiState.Reconnecting:
                    labelStatus.Text = "Reconnecting...";
                    labelStatus.ForeColor = Color.FromArgb(255, 200, 100);
                    break;
            }
        }

        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private readonly int _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private readonly SynchronizationContext _syncContext;

        public bool StartMinimized { get; set; }
        private bool _isLoaded;

        /// <summary>
        /// Determines whether marshaling to the UI thread is required.
        /// WinForms Control.InvokeRequired returns false on background threads when IsHandleCreated == false.
        /// This method checks the thread ID against the main UI thread ID first, guaranteeing accurate
        /// thread affinity detection even before the native window handle is created.
        /// </summary>
        private bool CheckInvokeRequired()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                return true;
            }
            return IsHandleCreated && InvokeRequired;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (StartMinimized && !this.IsHandleCreated)
            {
                StartMinimized = false;
                value = false;
                CreateHandle();
                if (!_isLoaded)
                {
                    main_Load(this, EventArgs.Empty);
                }
            }
            base.SetVisibleCore(value);
        }

        /// <summary>
        /// Marshals an action to the UI thread safely.
        /// Uses CheckInvokeRequired and SynchronizationContext fallback when IsHandleCreated is false.
        /// </summary>
        private void SafeBeginInvoke(Action action)
        {
            if (action == null || IsDisposed || Disposing) return;

            try
            {
                if (CheckInvokeRequired())
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!IsDisposed && !Disposing) action();
                            }
                            catch (Exception ex)
                            {
                                BluetoothManager.Log($"SafeBeginInvoke action exception: {ex.Message}");
                            }
                        }));
                    }
                    else
                    {
                        _syncContext.Post(_ =>
                        {
                            try
                            {
                                if (!IsDisposed && !Disposing) action();
                            }
                            catch (Exception ex)
                            {
                                BluetoothManager.Log($"SafeBeginInvoke syncContext action exception: {ex.Message}");
                            }
                        }, null);
                    }
                }
                else
                {
                    try
                    {
                        if (!IsDisposed && !Disposing) action();
                    }
                    catch (Exception ex)
                    {
                        BluetoothManager.Log($"SafeBeginInvoke direct action exception: {ex.Message}");
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Marshals an async function to the UI thread safely.
        /// Returns a Task representing the completion of the operation.
        /// Catches and logs exceptions, and guards against ObjectDisposedException/InvalidOperationException.
        /// Prevents async void lambda exceptions from escaping to the SynchronizationContext.
        /// </summary>
        private Task SafeBeginInvokeAsync(Func<Task> asyncFunc)
        {
            if (asyncFunc == null || IsDisposed || Disposing || _cts.IsCancellationRequested)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();

            CancellationTokenRegistration ctsReg = default;
            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    ctsReg = _cts.Token.Register(() => tcs.TrySetCanceled());
                }
                catch { }
            }

            Action cleanup = () =>
            {
                ctsReg.Dispose();
            };

            Action wrappedAction = async () =>
            {
                try
                {
                    if (IsDisposed || Disposing || _cts.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }
                    await asyncFunc().ConfigureAwait(true);
                    tcs.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    BluetoothManager.Log($"SafeBeginInvokeAsync exception: {ex.Message}");
                    tcs.TrySetResult(false);
                }
                finally
                {
                    cleanup();
                }
            };

            try
            {
                if (CheckInvokeRequired())
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(wrappedAction);
                    }
                    else
                    {
                        _syncContext.Post(_ => wrappedAction(), null);
                    }
                }
                else
                {
                    wrappedAction();
                }
            }
            catch (ObjectDisposedException)
            {
                cleanup();
                tcs.TrySetCanceled();
            }
            catch (InvalidOperationException)
            {
                cleanup();
                tcs.TrySetCanceled();
            }

            return tcs.Task;
        }

        private void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null)
                    {
                        string appName = "Connectify";
                        if (enable)
                        {
                            string path = $"\"{Application.ExecutablePath}\" -minimized";
                            rk.SetValue(appName, path);
                        }
                        else
                        {
                            rk.DeleteValue(appName, false);
                        }
                    }
                }
            }
            catch { }
        }

        public bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (rk != null)
                    {
                        string val = rk.GetValue("Connectify") as string;
                        if (!string.IsNullOrEmpty(val))
                        {
                            return val.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        private static readonly Dictionary<string, Image> _iconCache = new Dictionary<string, Image>();

        private static Image LoadCachedIcon(string assetsDir, string fileName)
        {
            if (fileName == null) return null;
            if (!_iconCache.TryGetValue(fileName, out var img))
            {
                string path = Path.Combine(assetsDir, fileName);
                if (System.IO.File.Exists(path))
                {
                    using (var stream = new MemoryStream(System.IO.File.ReadAllBytes(path)))
                    using (var temp = Image.FromStream(stream))
                    {
                        img = new Bitmap(temp);
                    }
                }
                _iconCache[fileName] = img;
            }
            return img;
        }

        // Alternating row background colors, matching the old hardcoded style
        private static readonly Color RowColorOdd  = Color.FromArgb(18, 18, 24);
        private static readonly Color RowColorEven = Color.FromArgb(22, 22, 30);

        // Static reusable fonts to prevent GDI+ handle leaks across card rebuilds
        private static readonly Font EmptyStateFont  = new Font("Segoe UI", 10F);
        private static readonly Font CardNameFont    = new Font("Segoe UI", 10F, FontStyle.Regular);
        private static readonly Font CardTypeFont    = new Font("Segoe UI", 8F);
        private static readonly Font CardBatteryFont = new Font("Segoe UI", 8F, FontStyle.Bold);

        public main()
        {
            InitializeComponent();
            _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _bluetoothManager = new BluetoothManager();
            _devicePresenter = new DevicePresenter(_bluetoothManager);
            _bluetoothManager.AdapterStateChanged    += BluetoothManager_AdapterStateChanged;
            _bluetoothManager.DeviceConnectionChanged += OnDeviceConnectionChanged;
            _bluetoothManager.OperationProgressChanged += BluetoothManager_OperationProgressChanged;
            this.FormClosing += main_FormClosing;
            this.Disposed += (s, e) =>
            {
                try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BluetoothManager.Log($"[Form Handle] OnHandleCreated: Handle=0x{this.Handle.ToInt64():X}");

            // Pitfall: During handle recreation, IsHandleCreated is briefly false and refreshes skip.
            // Queue a fresh refresh once the new handle is established.
            if (_isLoaded && !_cts.IsCancellationRequested && !IsDisposed)
            {
                SafeBeginInvoke(async () =>
                {
                    await RefreshDevicesIfIdleAsync(force: true);
                });
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            BluetoothManager.Log($"[Form Handle] OnHandleDestroyed: Handle destroyed (recreating={this.RecreatingHandle})");
            base.OnHandleDestroyed(e);
            // CRITICAL: Do NOT cancel _cts here (Fix Issue #26).
            // HandleDestroyed fires on handle recreation (e.g. flipping ShowInTaskbar, DPI changes, style changes).
            // _cts is only cancelled on actual application exit / non-user form closing.
        }



        /// <summary>
        /// Restores and activates the form if hidden or minimized in tray.
        /// Thread-safe via SafeBeginInvoke.
        /// </summary>
        public void ShowAndActivate()
        {
            if (CheckInvokeRequired())
            {
                SafeBeginInvoke(ShowAndActivate);
                return;
            }

            Show();
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
            BringToFront();

            if (_needsRefreshOnRestore)
            {
                _needsRefreshOnRestore = false;
                _ = RefreshDevicesIfIdleAsync(force: true);
            }
        }

        private const int WM_DEVICECHANGE = 0x0219;

        /// <summary>
        /// Listens for Windows hardware device arrival/removal messages (WM_DEVICECHANGE).
        /// Filters for DBT_DEVNODES_CHANGED, DBT_DEVICEARRIVAL, or DBT_DEVICEREMOVECOMPLETE,
        /// and uses a 500ms single-shot debounce timer to avoid WM_DEVICECHANGE storms.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && !_cts.IsCancellationRequested && !IsDisposed && IsHandleCreated)
            {
                int wParam = m.WParam.ToInt32();
                if (wParam == DBT_DEVNODES_CHANGED || wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE)
                {
                    if (_isLoaded && !_isTogglingAdapter && _bluetoothManager != null && !_bluetoothManager.IsRadioBounceInProgress)
                    {
                        RestartDeviceChangeDebounceTimer();
                    }
                }
            }
        }

        private void RestartDeviceChangeDebounceTimer()
        {
            if (_deviceChangeDebounceTimer == null)
            {
                _deviceChangeDebounceTimer = new Timer();
                _deviceChangeDebounceTimer.Interval = 500;
                _deviceChangeDebounceTimer.Tick += async (s, e) =>
                {
                    _deviceChangeDebounceTimer.Stop();
                    if (IsDisposed || Disposing || _cts.IsCancellationRequested) return;
                    if (_isTogglingAdapter || (_bluetoothManager != null && _bluetoothManager.IsRadioBounceInProgress)) return;

                    if (!IsAppVisible)
                    {
                        _needsRefreshOnRestore = true;
                        return;
                    }

                    try
                    {
                        var previousState = _currentUiState;
                        await RefreshAdapterStateAsync();
                        if (previousState == AdapterUiState.NoAdapter && _currentUiState == AdapterUiState.Enabled)
                        {
                            await RefreshDevicesIfIdleAsync(force: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        BluetoothManager.Log($"Device change debounce timer exception: {ex.Message}");
                    }
                };
            }
            _deviceChangeDebounceTimer.Stop();
            _deviceChangeDebounceTimer.Start();
        }

        private async void main_Load(object sender, EventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;

            try
            {
                // Setup NotifyIcon
                _trayMenu = new ContextMenuStrip();
                _trayMenu.Items.Add("Show", null, (s, ev) => ShowAndActivate());
                
                var startupMenuItem = new ToolStripMenuItem("Start with Windows");
                startupMenuItem.CheckOnClick = true;
                startupMenuItem.Checked = IsStartupEnabled();
                startupMenuItem.CheckedChanged += (s, ev) => SetStartup(startupMenuItem.Checked);
                _trayMenu.Items.Add(startupMenuItem);

                var autoBounceMenuItem = new ToolStripMenuItem("Allow automatic radio bounce");
                autoBounceMenuItem.CheckOnClick = true;
                autoBounceMenuItem.Checked = BluetoothManager.AllowAutomaticRadioBounce;
                autoBounceMenuItem.CheckedChanged += (s, ev) => BluetoothManager.AllowAutomaticRadioBounce = autoBounceMenuItem.Checked;
                _trayMenu.Items.Add(autoBounceMenuItem);

                _trayMenu.Items.Add("Reset Bluetooth (disconnects other devices)", null, async (s, ev) =>
                {
                    await ResetBluetoothWithConfirmationAsync();
                });
                
                _trayMenu.Items.Add("[Debug] Recreate Handle (Flip ShowInTaskbar)", null, (s, ev) =>
                {
                    BluetoothManager.Log("[Debug Tray] Recreating handle via ShowInTaskbar toggle...");
                    this.ShowInTaskbar = !this.ShowInTaskbar;
                });
                
                _trayMenu.Items.Add(new ToolStripSeparator());
                _trayMenu.Items.Add("Exit", null, (s, ev) => { Application.Exit(); });

                _trayIcon = new NotifyIcon();
                _trayIcon.Text = "Connectify";
                _trayIcon.Icon = this.Icon;
                _trayIcon.ContextMenuStrip = _trayMenu;
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += (s, ev) => ShowAndActivate();

                // Setup Refresh Timer
                _refreshTimer = new Timer();
                _refreshTimer.Interval = 60000; // 60 seconds
                _refreshTimer.Tick += async (s, ev) =>
                {
                    try
                    {
                        if (!IsAppVisible)
                        {
                            _needsRefreshOnRestore = true;
                            return;
                        }

                        if (_currentUiState == AdapterUiState.NoAdapter)
                        {
                            await RefreshAdapterStateAsync();
                        }
                        await RefreshDevicesIfIdleAsync();
                    }
                    catch (Exception ex)
                    {
                        BluetoothManager.Log($"Refresh timer exception: {ex.Message}");
                    }
                };
                _refreshTimer.Start();

                // Enable DoubleBuffered on child scroll panel to prevent GDI repaint flicker
                SetDoubleBuffered(panelDeviceList);

                // Force center the title — labels live inside panelHeader, so use its width
                labelBluetooth.Left = 0;
                labelBluetooth.Width = panelHeader.Width;
                labelBluetooth.TextAlignment = ContentAlignment.MiddleCenter;

                labelStatus.Left = 0;
                labelStatus.Width = panelHeader.Width;
                labelStatus.TextAlignment = ContentAlignment.MiddleCenter;

                // Also center the logo PictureBox horizontally inside the panel
                pictureBoxBT.Left = (panelHeader.Width - pictureBoxBT.Width) / 2;

                // And center the toggle switch
                on_off.Left = (panelHeader.Width - on_off.Width) / 2;

                labelDevicesHeader.Text = "PAIRED DEVICES";
                labelDevicesHeader.Width = this.ClientSize.Width - 40;

                // Center footer label
                labelFooter.Left = 0;
                labelFooter.Width = this.ClientSize.Width;
                labelFooter.TextAlignment = ContentAlignment.MiddleCenter;
                labelFooter.Text = "Connectify  v2.0";

                // Detect actual Bluetooth adapter state on form load (Rule B-2)
                await RefreshAdapterStateAsync();

                // Load devices initially
                await RefreshDevicesAsync(force: true);
            }
            catch (Exception ex)
            {
                BluetoothManager.Log($"main_Load unhandled exception: {ex}");
            }
        }

        // ── Adapter state ─────────────────────────────────────────────────────

        private async Task RefreshAdapterStateAsync()
        {
            try
            {
                bool? isEnabled = await _bluetoothManager.GetAdapterStateAsync();
                ApplyAdapterStateToUi(isEnabled);
            }
            catch
            {
                ApplyAdapterStateToUi(null);
            }
        }

        private async Task ResetBluetoothWithConfirmationAsync()
        {
            if (_bluetoothManager.IsRadioBounceInProgress)
            {
                MessageBox.Show("A Bluetooth reset is already in progress.", "Reset Bluetooth", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_bluetoothManager.HasConnectedPeripherals(0, out string hidName, out BluetoothDeviceType hidType))
            {
                var confirm = MessageBox.Show(
                    $"Warning: An active input peripheral '{hidName}' ({hidType}) is currently connected.\n\n" +
                    "Resetting Bluetooth will cycle adapter power and temporarily disconnect your mouse/keyboard.\n\n" +
                    "Do you want to continue?",
                    "Reset Bluetooth",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes) return;
            }
            else
            {
                var confirm = MessageBox.Show(
                    "Resetting Bluetooth will power cycle the adapter and temporarily disconnect all connected Bluetooth devices.\n\n" +
                    "Do you want to continue?",
                    "Reset Bluetooth",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (confirm != DialogResult.Yes) return;
            }

            SetUiState(AdapterUiState.Reconnecting);
            bool resetOk = await _bluetoothManager.ManualResetBluetoothAsync();
            if (!this.IsDisposed && this.IsHandleCreated)
            {
                await RefreshAdapterStateAsync();
                await RefreshDevicesIfIdleAsync(force: true);
            }
        }

        private void ApplyAdapterStateToUi(bool? isEnabled)
        {
            if (CheckInvokeRequired())
            {
                SafeBeginInvoke(() => ApplyAdapterStateToUi(isEnabled));
                return;
            }

            // If a controlled radio bounce is in progress AND user did not explicitly request adapter off, show "Reconnecting..." in the header
            if (_bluetoothManager != null && _bluetoothManager.IsRadioBounceInProgress && !_bluetoothManager.UserRequestedAdapterOff)
            {
                SetUiState(AdapterUiState.Reconnecting);
                if (!_isTogglingAdapter)
                {
                    on_off.Enabled = true;
                }
                return;
            }

            _isUpdatingUiFromState = true;
            try
            {
                if (isEnabled == true)
                {
                    SetUiState(AdapterUiState.Enabled);
                    if (!_isTogglingAdapter)
                    {
                        on_off.Enabled = true;
                    }
                    on_off.Checked = true;
                    on_off.Value = true;
                    _bluetoothManager.StartDeviceWatcher();
                }
                else if (isEnabled == false)
                {
                    unchecked { ++_refreshGeneration; }
                    SetUiState(AdapterUiState.Disabled);
                    if (!_isTogglingAdapter)
                    {
                        on_off.Enabled = true;
                    }
                    on_off.Checked = false;
                    on_off.Value = false;
                    _bluetoothManager.StopDeviceWatcher();
                    // Clear device list immediately when adapter is off
                    BuildDeviceCards(new List<DeviceInfo>(), force: true);
                }
                else
                {
                    unchecked { ++_refreshGeneration; }
                    SetUiState(AdapterUiState.NoAdapter);
                    on_off.Enabled = false;
                    on_off.Checked = false;
                    on_off.Value = false;
                    _bluetoothManager.StopDeviceWatcher();
                    BuildDeviceCards(new List<DeviceInfo>(), force: true);
                }
            }
            finally
            {
                _isUpdatingUiFromState = false;
            }
        }

        private void BluetoothManager_OperationProgressChanged(object sender, DeviceOperationProgressEventArgs e)
        {
            SafeBeginInvoke(() =>
            {
                SetUiState(AdapterUiState.Reconnecting);
                _devicePresenter.UpdateInFlightStatus(e.Address, e.StatusMessage, Color.FromArgb(255, 190, 50));
                UpdateOperationFeedback(e.Address, e.StatusMessage, Color.FromArgb(255, 190, 50), null);
            });
        }

        private void BluetoothManager_AdapterStateChanged(object sender, AdapterStateChangedEventArgs e)
        {
            ApplyAdapterStateToUi(e.IsEnabled);
        }

        private async void on_off_CheckedChanged(object sender, Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs e)
        {
            if (_isUpdatingUiFromState) return;

            bool targetState = e.Checked;
            if (!targetState)
            {
                unchecked { ++_refreshGeneration; }
            }

            // Guard against rapid duplicate clicks and premature re-enabling by background state events
            _isTogglingAdapter = true;
            on_off.Enabled = false;

            try
            {
                if (!targetState)
                {
                    // User explicitly requested Bluetooth OFF:
                    // Cancel any in-flight device operations immediately so they do not keep running
                    // or attempt a radio bounce
                    _devicePresenter.CancelAllOperations();
                }

                // Temporary visual feedback (Fix Issue #29: typed UI state)
                SetUiState(targetState ? AdapterUiState.TurningOn : AdapterUiState.TurningOff);

                bool success = await _bluetoothManager.SetAdapterStateAsync(targetState);
                if (!success)
                {
                    // Revert and refresh from actual hardware state if toggle failed
                    await RefreshAdapterStateAsync();
                }
                else
                {
                    // Explicitly apply state to UI so labelStatus is never left stuck on "Turning On..." or "Turning Off..."
                    ApplyAdapterStateToUi(targetState);

                    if (targetState)
                    {
                        // Adapter just turned on — refresh device list
                        await RefreshDevicesAsync(force: true);
                    }
                    else
                    {
                        // Adapter turned off — ensure device list is cleared
                        _devicePresenter.CancelAllOperations();
                        BuildDeviceCards(new List<DeviceInfo>(), force: true);
                    }
                }
            }
            catch (Exception ex)
            {
                BluetoothManager.Log($"on_off_CheckedChanged exception: {ex}");
            }
            finally
            {
                _isTogglingAdapter = false;
                // Re-enable toggle switch unless adapter is absent (Fix Issue #29: typed UI state)
                if (_currentUiState != AdapterUiState.NoAdapter && !_isUpdatingUiFromState)
                {
                    on_off.Enabled = true;
                }
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called by DeviceWatcher (background thread) whenever a Bluetooth device
        /// connects or disconnects. Marshals to UI thread via SafeBeginInvokeAsync to safely
        /// access _isRefreshingDevices and _refreshPending without threading races.
        /// </summary>
        private async void OnDeviceConnectionChanged(object sender, EventArgs e)
        {
            try
            {
                await SafeBeginInvokeAsync(() => RefreshDevicesIfIdleAsync());
            }
            catch (OperationCanceledException)
            {
                // Expected during form close / cancellation
            }
            catch (Exception ex)
            {
                BluetoothManager.Log($"OnDeviceConnectionChanged exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Requests a device refresh if no refresh is currently running; otherwise queues a pending refresh.
        /// THREAD SAFETY: Must ONLY be called on the UI thread to guard _isRefreshingDevices / _refreshPending.
        /// </summary>
        private async Task RefreshDevicesIfIdleAsync(bool force = false)
        {
            if (this.IsDisposed || !this.IsHandleCreated || _cts.IsCancellationRequested) return;

            if (_bluetoothManager != null && _bluetoothManager.IsRadioBounceInProgress)
            {
                // Suppress device list refresh while radio bounce is power-cycling the adapter (Fix Issue #6)
                return;
            }

            if (!IsAppVisible)
            {
                // Pause rebuilds and heavy queries while hidden or minimized to tray (Fix Issue #30)
                _needsRefreshOnRestore = true;
                return;
            }

            if (_isRefreshingDevices)
            {
                _refreshPending = true;
                if (force) _forceRefreshPending = true;
                return;
            }
            await RefreshDevicesAsync(force);
        }

        /// <summary>
        /// Queries paired devices and rebuilds cards in a coalescing loop.
        /// THREAD SAFETY: Must ONLY be called on the UI thread to guard _isRefreshingDevices / _refreshPending.
        /// </summary>
        private async Task RefreshDevicesAsync(bool force = false)
        {
            if (this.IsDisposed || !this.IsHandleCreated || _cts.IsCancellationRequested) return;

            if (_bluetoothManager != null && _bluetoothManager.IsRadioBounceInProgress)
            {
                // Suppress device list refresh while radio bounce is power-cycling the adapter (Fix Issue #6)
                return;
            }

            if (!IsAppVisible)
            {
                // Pause rebuilds and heavy queries while hidden or minimized to tray (Fix Issue #30)
                _needsRefreshOnRestore = true;
                return;
            }

            if (_isRefreshingDevices)
            {
                _refreshPending = true;
                if (force) _forceRefreshPending = true;
                return;
            }

            _isRefreshingDevices = true;
            try
            {
                do
                {
                    int currentGen = unchecked(++_refreshGeneration);
                    bool currentForce = force || _forceRefreshPending;
                    force = false; // Reset method parameter so subsequent coalesced passes only use _forceRefreshPending
                    _refreshPending = false;
                    _forceRefreshPending = false;
                    try
                    {
                        var devices = await _bluetoothManager.GetPairedDevicesAsync(_cts.Token);
                        if (currentGen != _refreshGeneration)
                        {
                            // Stale pass superseded by newer refresh or adapter state change (Fix Issue #21)
                            return;
                        }

                        if (!this.IsDisposed && this.IsHandleCreated && !_cts.IsCancellationRequested)
                        {
                            // If devices is null (e.g. suppressed during radio bounce), preserve existing cards (Fix Issue #6)
                            if (devices != null)
                            {
                                BuildDeviceCards(devices, currentForce);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (currentGen != _refreshGeneration) return;
                        BluetoothManager.Log($"RefreshDevicesAsync exception: {ex.Message}");
                        // Fix Issue #22: Preserve existing cards rather than wiping the list on a transient query error
                    }
                } while (_refreshPending && !_cts.IsCancellationRequested && !this.IsDisposed);
            }
            finally
            {
                _isRefreshingDevices = false;
                _forceRefreshPending = false;
            }
        }

        /// <summary>
        /// Updates operation feedback text and color safely on typeLabel.
        /// </summary>
        private void UpdateOperationFeedback(ulong address, string text, Color color, Label originalLabel = null)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            _devicePresenter.UpdateInFlightStatus(address, text, color);

            if (originalLabel != null && !originalLabel.IsDisposed)
            {
                originalLabel.Text = text;
                originalLabel.ForeColor = color;
                return;
            }

            if (_cardViews.TryGetValue(address, out var view) && view.TypeLabel != null && !view.TypeLabel.IsDisposed)
            {
                view.TypeLabel.Text = text;
                view.TypeLabel.ForeColor = color;
                return;
            }

            // Fallback: If card is tracked via panel controls
            if (panelDeviceList.IsHandleCreated && !panelDeviceList.IsDisposed)
            {
                foreach (Control ctrl in panelDeviceList.Controls)
                {
                    if (ctrl is Panel p && !p.IsDisposed && p.Tag is ulong cardAddr && cardAddr == address)
                    {
                        foreach (Control child in p.Controls)
                        {
                            if (child is Label lbl && lbl.Name == "typeLabel" && !lbl.IsDisposed)
                            {
                                lbl.Text = text;
                                lbl.ForeColor = color;
                                return;
                            }
                        }
                    }
                }
            }
        }

        private sealed class DeviceCardView
        {
            public ulong Address;
            public DeviceInfo BoundDevice;
            public Panel Card;
            public Panel IconPanel;
            public PictureBox IconBox;
            public Label NameLabel;
            public Label TypeLabel;
            public PictureBox BatteryBox;
            public Label BatteryLabel;
            public Color AccentColor;
            public Color BaseBgColor;
            public Color HoverBgColor;
            public bool IsHovered;
        }

        /// <summary>
        /// Reconciles panelDeviceList in-place diffing by address, updating labels, icons,
        /// and positions without destroying controls under cursor (Fix Issues #27, #28, #36).
        /// </summary>
        private void BuildDeviceCards(IReadOnlyList<DeviceInfo> devices, bool force = false)
        {
            if (this.IsDisposed) return;

            if (CheckInvokeRequired())
            {
                SafeBeginInvoke(() => BuildDeviceCards(devices, force));
                return;
            }

            if (this.IsDisposed || !this.IsHandleCreated) return;

            panelDeviceList.SuspendLayout();
            try
            {
                if (devices == null || devices.Count == 0)
                {
                    // Dispose and clear all existing device cards
                    foreach (var view in _cardViews.Values)
                    {
                        try { view.Card.Dispose(); } catch { }
                    }
                    _cardViews.Clear();

                    string emptyMessage = "No paired devices found";
                    if (_currentUiState == AdapterUiState.Disabled || _currentUiState == AdapterUiState.TurningOff || labelStatus.Text == "Disabled" || labelStatus.Text == "Turning Off...")
                    {
                        emptyMessage = "Bluetooth is turned off";
                    }
                    else if (_currentUiState == AdapterUiState.NoAdapter || labelStatus.Text == "No Adapter")
                    {
                        emptyMessage = "No Bluetooth adapter found";
                    }

                    if (_emptyLabel == null || _emptyLabel.IsDisposed)
                    {
                        _emptyLabel = new Label
                        {
                            ForeColor = Color.FromArgb(100, 100, 120),
                            Font = EmptyStateFont,
                            AutoSize = false,
                            TextAlign = ContentAlignment.MiddleCenter,
                            Size = new Size(panelDeviceList.ClientSize.Width, 80),
                            Location = new Point(0, 20),
                            BackColor = Color.Transparent
                        };
                        panelDeviceList.Controls.Add(_emptyLabel);
                    }
                    _emptyLabel.Text = emptyMessage;
                    _emptyLabel.Visible = true;
                    _emptyLabel.Width = panelDeviceList.ClientSize.Width;
                    panelDeviceList.AutoScrollMinSize = new Size(0, 0);
                    return;
                }

                // Non-empty device list: hide empty label
                if (_emptyLabel != null && !_emptyLabel.IsDisposed)
                {
                    _emptyLabel.Visible = false;
                }

                int cardHeight = 72;
                int totalHeight = devices.Count * cardHeight;
                bool needsVerticalScroll = totalHeight > panelDeviceList.ClientSize.Height;
                int availableWidth = panelDeviceList.Width;
                int cardWidth = needsVerticalScroll
                    ? (availableWidth - SystemInformation.VerticalScrollBarWidth)
                    : availableWidth;

                string assetsDir = Path.Combine(Application.StartupPath, "assets");
                const int batteryColWidth = 76;
                const int rightPad = 10;
                int batteryColX = cardWidth - batteryColWidth - rightPad;

                // 1) Remove stale cards for devices no longer paired/present
                var activeAddresses = new HashSet<ulong>();
                for (int i = 0; i < devices.Count; i++)
                {
                    activeAddresses.Add(devices[i].Address);
                }

                var staleAddresses = new List<ulong>();
                foreach (var kvp in _cardViews)
                {
                    if (!activeAddresses.Contains(kvp.Key))
                    {
                        staleAddresses.Add(kvp.Key);
                    }
                }

                foreach (var addr in staleAddresses)
                {
                    if (_cardViews.TryGetValue(addr, out var view))
                    {
                        _cardViews.Remove(addr);
                        panelDeviceList.Controls.Remove(view.Card);
                        try { view.Card.Dispose(); } catch { }
                    }
                }

                // 2) Reconcile each device card in order
                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    bool isOdd = (i % 2 == 0);
                    Color baseBgColor = isOdd ? RowColorOdd : RowColorEven;
                    Color hoverBgColor = Color.FromArgb(baseBgColor.R + 10, baseBgColor.G + 10, baseBgColor.B + 15);
                    int targetY = i * cardHeight;

                    Color accentColor = GetDeviceAccentColor(device.DeviceType);
                    if (!device.IsConnected)
                    {
                        accentColor = Color.FromArgb(70, 70, 80);
                    }

                    if (_cardViews.TryGetValue(device.Address, out var view))
                    {
                        // Update existing card in-place
                        view.BoundDevice = device;
                        view.BaseBgColor = baseBgColor;
                        view.HoverBgColor = hoverBgColor;
                        view.AccentColor = accentColor;

                        if (view.Card.Location.Y != targetY || view.Card.Location.X != 0)
                        {
                            view.Card.Location = new Point(0, targetY);
                        }
                        if (view.Card.Width != cardWidth || view.Card.Height != cardHeight)
                        {
                            view.Card.Size = new Size(cardWidth, cardHeight);
                            view.NameLabel.Width = batteryColX - 74 - 6;
                            view.TypeLabel.Width = batteryColX - 74 - 6;
                            view.BatteryBox.Location = new Point(batteryColX, (cardHeight - 28) / 2);
                            view.BatteryLabel.Location = new Point(batteryColX + 32, (cardHeight - 20) / 2);
                        }

                        // Update background color based on hover state
                        Point cursorInCard = view.Card.PointToClient(Cursor.Position);
                        view.IsHovered = view.Card.ClientRectangle.Contains(cursorInCard);
                        Color currentBg = view.IsHovered ? hoverBgColor : baseBgColor;
                        if (view.Card.BackColor != currentBg) view.Card.BackColor = currentBg;
                        if (view.IconPanel.BackColor != currentBg) view.IconPanel.BackColor = currentBg;

                        // Name label
                        if (view.NameLabel.Text != device.Name) view.NameLabel.Text = device.Name;
                        Color nameColor = device.IsConnected ? Color.FromArgb(220, 220, 235) : Color.FromArgb(140, 140, 150);
                        if (view.NameLabel.ForeColor != nameColor) view.NameLabel.ForeColor = nameColor;

                        // Type & In-flight status
                        if (_devicePresenter.TryGetInFlightStatus(device.Address, out string inFlightText, out Color inFlightColor) && !string.IsNullOrEmpty(inFlightText))
                        {
                            view.TypeLabel.Text = inFlightText;
                            view.TypeLabel.ForeColor = inFlightColor;
                        }
                        else
                        {
                            string typeLabelText = GetDeviceTypeLabel(device.DeviceType);
                            Color typeLabelColor = device.IsConnected
                                ? Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B)
                                : Color.FromArgb(100, 100, 110);
                            if (view.TypeLabel.Text != typeLabelText) view.TypeLabel.Text = typeLabelText;
                            if (view.TypeLabel.ForeColor != typeLabelColor) view.TypeLabel.ForeColor = typeLabelColor;
                        }

                        // Battery
                        string batteryText = device.BatteryPercent.HasValue ? $"{device.BatteryPercent.Value}%" : "";
                        string expectedBatteryText = device.IsConnected ? batteryText : "";
                        if (view.BatteryLabel.Text != expectedBatteryText) view.BatteryLabel.Text = expectedBatteryText;
                        Color batteryColor = GetBatteryColor(device.BatteryPercent);
                        if (view.BatteryLabel.ForeColor != batteryColor) view.BatteryLabel.ForeColor = batteryColor;

                        string batteryIcon = GetBatteryIconFileName(device.BatteryPercent);
                        Image battImg = (device.IsConnected && batteryIcon != null) ? LoadCachedIcon(assetsDir, batteryIcon) : null;
                        if (view.BatteryBox.Image != battImg) view.BatteryBox.Image = battImg;

                        // Icon
                        string iconFile = GetDeviceIconFileName(device.DeviceType);
                        Image iconImg = LoadCachedIcon(assetsDir, iconFile);
                        if (view.IconBox.Image != iconImg) view.IconBox.Image = iconImg;

                        view.IconPanel.Invalidate();
                        panelDeviceList.Controls.SetChildIndex(view.Card, i);
                    }
                    else
                    {
                        // Create brand new card
                        var newView = CreateDeviceCardView(device, targetY, cardWidth, cardHeight, baseBgColor, hoverBgColor, accentColor, batteryColX, assetsDir);
                        _cardViews[device.Address] = newView;
                        panelDeviceList.Controls.Add(newView.Card);
                        panelDeviceList.Controls.SetChildIndex(newView.Card, i);
                    }
                }

                panelDeviceList.AutoScrollMinSize = new Size(0, totalHeight);
            }
            finally
            {
                panelDeviceList.ResumeLayout(true);
            }

            panelDeviceList.HorizontalScroll.Maximum = 0;
            panelDeviceList.HorizontalScroll.Enabled = false;
            panelDeviceList.HorizontalScroll.Visible = false;
        }

        private DeviceCardView CreateDeviceCardView(
            DeviceInfo device,
            int y,
            int cardWidth,
            int cardHeight,
            Color baseBgColor,
            Color hoverBgColor,
            Color accentColor,
            int batteryColX,
            string assetsDir)
        {
            var view = new DeviceCardView
            {
                Address = device.Address,
                BoundDevice = device,
                BaseBgColor = baseBgColor,
                HoverBgColor = hoverBgColor,
                AccentColor = accentColor
            };

            var card = new Panel
            {
                Tag = device.Address,
                BackColor = baseBgColor,
                Location = new Point(0, y),
                Size = new Size(cardWidth, cardHeight),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            SetDoubleBuffered(card);
            view.Card = card;

            var iconPanel = new Panel
            {
                BackColor = baseBgColor,
                Location = new Point(14, 12),
                Size = new Size(48, 48),
                Cursor = Cursors.Hand
            };
            SetDoubleBuffered(iconPanel);
            iconPanel.Paint += (s, pe) =>
            {
                pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color acc = view.AccentColor;
                using (var fill = new SolidBrush(Color.FromArgb(28, acc)))
                    pe.Graphics.FillEllipse(fill, 0, 0, 47, 47);
                using (var border = new Pen(Color.FromArgb(90, acc), 1.5f))
                    pe.Graphics.DrawEllipse(border, 1, 1, 45, 45);
            };
            view.IconPanel = iconPanel;

            var iconBox = new PictureBox
            {
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(26, 26),
                Location = new Point(11, 11),
                Cursor = Cursors.Hand
            };
            string iconFile = GetDeviceIconFileName(device.DeviceType);
            Image iconImg = LoadCachedIcon(assetsDir, iconFile);
            if (iconImg != null) iconBox.Image = iconImg;
            iconPanel.Controls.Add(iconBox);
            view.IconBox = iconBox;

            var nameLabel = new Label
            {
                Text = device.Name,
                ForeColor = device.IsConnected ? Color.FromArgb(220, 220, 235) : Color.FromArgb(140, 140, 150),
                Font = CardNameFont,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.BottomLeft,
                BackColor = Color.Transparent,
                Location = new Point(74, 10),
                Size = new Size(batteryColX - 74 - 6, 26),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            view.NameLabel = nameLabel;

            string typeText = GetDeviceTypeLabel(device.DeviceType);
            Color typeColor = device.IsConnected ? Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B) : Color.FromArgb(100, 100, 110);
            if (_devicePresenter.TryGetInFlightStatus(device.Address, out string inFlightText, out Color inFlightColor) && !string.IsNullOrEmpty(inFlightText))
            {
                typeText = inFlightText;
                typeColor = inFlightColor;
            }

            var typeLabel = new Label
            {
                Name = "typeLabel",
                Text = typeText,
                ForeColor = typeColor,
                Font = CardTypeFont,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                BackColor = Color.Transparent,
                Location = new Point(74, 38),
                Size = new Size(batteryColX - 74 - 6, 20),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            view.TypeLabel = typeLabel;

            string batteryIcon = GetBatteryIconFileName(device.BatteryPercent);
            Color batteryColor = GetBatteryColor(device.BatteryPercent);
            string batteryText = device.BatteryPercent.HasValue ? $"{device.BatteryPercent.Value}%" : "";

            var batteryBox = new PictureBox
            {
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(28, 28),
                Location = new Point(batteryColX, (cardHeight - 28) / 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            if (device.IsConnected && batteryIcon != null)
            {
                Image battImg = LoadCachedIcon(assetsDir, batteryIcon);
                if (battImg != null) batteryBox.Image = battImg;
            }
            view.BatteryBox = batteryBox;

            var batteryLabel = new Label
            {
                Text = device.IsConnected ? batteryText : "",
                ForeColor = batteryColor,
                Font = CardBatteryFont,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                Location = new Point(batteryColX + 32, (cardHeight - 20) / 2),
                Size = new Size(44, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            view.BatteryLabel = batteryLabel;

            EventHandler onEnter = (s, e) =>
            {
                view.IsHovered = true;
                card.BackColor = view.HoverBgColor;
                iconPanel.BackColor = view.HoverBgColor;
            };

            EventHandler onLeave = (s, e) =>
            {
                Point pt = card.PointToClient(Cursor.Position);
                if (!card.ClientRectangle.Contains(pt))
                {
                    view.IsHovered = false;
                    card.BackColor = view.BaseBgColor;
                    iconPanel.BackColor = view.BaseBgColor;
                }
            };

            EventHandler onClick = (s, e) =>
            {
                OnCardClick(view);
            };

            var controls = new Control[] { card, iconPanel, iconBox, nameLabel, typeLabel, batteryBox, batteryLabel };
            foreach (var ctrl in controls)
            {
                ctrl.MouseEnter += onEnter;
                ctrl.MouseLeave += onLeave;
                ctrl.Click += onClick;
            }

            card.Controls.Add(iconPanel);
            card.Controls.Add(nameLabel);
            card.Controls.Add(typeLabel);
            card.Controls.Add(batteryBox);
            card.Controls.Add(batteryLabel);

            return view;
        }

        private async void OnCardClick(DeviceCardView view)
        {
            if (this.IsDisposed || !this.IsHandleCreated || view == null || view.Card.IsDisposed)
                return;

            DeviceInfo device = view.BoundDevice;
            if (device == null) return;

            ulong addr = device.Address;
            bool userIntentConnect = !device.IsConnected;
            BluetoothManager.Log($"User clicked device '{device.Name}' ({addr:X12}), requestedAction={(userIntentConnect ? "Connect" : "Disconnect")}");

            // Immediate visual feedback upon click (Fix Issue #12)
            UpdateOperationFeedback(addr, userIntentConnect ? "Connecting..." : "Disconnecting...", Color.FromArgb(255, 190, 50), view.TypeLabel);

            DeviceOperationResult result = await _devicePresenter.ExecuteDeviceToggleAsync(
                device,
                _cts.Token,
                (statusText, statusColor) => UpdateOperationFeedback(addr, statusText, statusColor, view.TypeLabel)
            );

            if (this.IsDisposed || !this.IsHandleCreated) return;

            if (result.Status == DeviceOperationStatus.Cancelled && result.Message == "Operation already in flight")
            {
                // Duplicate click ignored while operation is in flight
                return;
            }

            if (result.Status == DeviceOperationStatus.Failed)
            {
                UpdateOperationFeedback(addr, userIntentConnect ? "Failed (Try Reset BT)" : "Disconnect failed", Color.FromArgb(230, 80, 80), view.TypeLabel);
                BluetoothManager.Log($"Connection failed for '{device.Name}'. If in range, user can trigger 'Reset Bluetooth' from tray.");
                try { await Task.Delay(1500, _cts.Token); } catch { }
            }
            else if (result.Status == DeviceOperationStatus.AccessDenied)
            {
                UpdateOperationFeedback(addr, "Elevation required", Color.FromArgb(230, 80, 80), view.TypeLabel);
                try { await Task.Delay(1500, _cts.Token); } catch { }
            }
            else if (result.IsSuccess)
            {
                try { await Task.Delay(200, _cts.Token); } catch { }
            }

            if (!this.IsDisposed && this.IsHandleCreated && !_cts.IsCancellationRequested)
            {
                try
                {
                    await RefreshAdapterStateAsync();
                    await RefreshDevicesIfIdleAsync(force: true);
                }
                catch (Exception ex)
                {
                    BluetoothManager.Log($"OnCardClick refresh exception: {ex.Message}");
                }
            }
        }

        // ── Device type helpers ───────────────────────────────────────────────

        private static string GetDeviceIconFileName(BluetoothDeviceType type)
        {
            switch (type)
            {
                case BluetoothDeviceType.Earbuds:     return "earbud_headphones_60px.png";
                case BluetoothDeviceType.Headphones:  return "headset_60px.png";
                case BluetoothDeviceType.Headset:     return "headset_60px.png";
                case BluetoothDeviceType.Speaker:     return "subwoofer_60px.png";
                case BluetoothDeviceType.Microphone:  return "microphone_60px.png";
                case BluetoothDeviceType.Mouse:       return "mouse_60px.png";
                case BluetoothDeviceType.Keyboard:    return "keyboard_60px.png";
                case BluetoothDeviceType.Controller:  return "game_controller_60px.png";
                case BluetoothDeviceType.Phone:       return "Smartphone_60px.png";
                case BluetoothDeviceType.Smartwatch:  return "smart_watch_60px.png";
                case BluetoothDeviceType.Computer:    return "Smartphone_60px.png"; // Fallback
                case BluetoothDeviceType.AudioDevice: return "subwoofer_60px.png"; // Fallback
                default:                              return "Smartphone_60px.png"; // Fallback
            }
        }

        /// <summary>
        /// Maps a nullable battery percentage to the correct battery icon asset.
        /// Returns null when battery is unknown so the UI can hide the icon.
        /// </summary>
        private static string GetBatteryIconFileName(int? battery)
        {
            if (!battery.HasValue) return null;          // unknown — hide icon
            if (battery.Value >= 85)  return "full_battery_480px.png";              // 85-100 %
            if (battery.Value >= 21)  return "Almost Charged Battery_480px.png";   // 21-84  %
            return "empty_battery_480px.png";                                       // 0-20   %
        }

        private static string GetDeviceTypeLabel(BluetoothDeviceType type)
        {
            switch (type)
            {
                case BluetoothDeviceType.Earbuds:     return "Wireless Earbuds";
                case BluetoothDeviceType.Headphones:  return "Wireless Headphones";
                case BluetoothDeviceType.Headset:     return "Wireless Headset";
                case BluetoothDeviceType.Speaker:     return "Bluetooth Speaker";
                case BluetoothDeviceType.Microphone:  return "Bluetooth Microphone";
                case BluetoothDeviceType.Mouse:       return "Bluetooth Mouse";
                case BluetoothDeviceType.Keyboard:    return "Bluetooth Keyboard";
                case BluetoothDeviceType.Controller:  return "Bluetooth Controller";
                case BluetoothDeviceType.Phone:       return "Smartphone";
                case BluetoothDeviceType.Smartwatch:  return "Smartwatch";
                case BluetoothDeviceType.Computer:    return "Computer";
                case BluetoothDeviceType.AudioDevice: return "Audio Device";
                default:                              return "Bluetooth Device";
            }
        }

        /// <summary>
        /// Returns the colour used for the battery percentage label.
        ///   Green  : 85-100 % (full)
        ///   Amber  : 21-84  % (normal)
        ///   Red    : 0-20   % (critical)
        ///   Grey   : null   (unavailable)
        /// </summary>
        private static Color GetBatteryColor(int? battery)
        {
            if (!battery.HasValue) return Color.FromArgb(100, 100, 120);   // grey — unknown
            if (battery.Value >= 85) return Color.FromArgb(80,  200, 120); // green
            if (battery.Value >= 21) return Color.FromArgb(255, 190,  50); // amber
            return Color.FromArgb(220, 70, 70);                             // red
        }

        private static Color GetDeviceAccentColor(BluetoothDeviceType type)
        {
            switch (type)
            {
                case BluetoothDeviceType.Earbuds:
                case BluetoothDeviceType.Headphones:
                case BluetoothDeviceType.Headset:
                case BluetoothDeviceType.AudioDevice: return Color.FromArgb(100, 180, 255); // blue
                case BluetoothDeviceType.Speaker:     return Color.FromArgb(130, 220, 160); // green
                case BluetoothDeviceType.Microphone:  return Color.FromArgb(255, 160, 100); // orange
                case BluetoothDeviceType.Mouse:
                case BluetoothDeviceType.Keyboard:    return Color.FromArgb(190, 140, 255); // purple
                case BluetoothDeviceType.Controller:  return Color.FromArgb(255, 100, 120); // red/pink
                case BluetoothDeviceType.Phone:       return Color.FromArgb(80,  200, 220); // teal
                case BluetoothDeviceType.Smartwatch:  return Color.FromArgb(255, 210,  80); // gold
                case BluetoothDeviceType.Computer:    return Color.FromArgb(160, 200, 255); // light blue
                default:                              return Color.FromArgb(130, 130, 160); // grey
            }
        }

        /// <summary>
        /// Enables DoubleBuffered on a Control (such as Panel) using reflection to eliminate GDI repaint flicker.
        /// </summary>
        private static void SetDoubleBuffered(Control control)
        {
            if (control == null) return;
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.SetValue(control, true, null);
            }
            catch { }
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        /// <summary>
        /// Disposes the BluetoothManager (stops BLE watchers, releases adapter handles)
        /// when the form is closing. Rule B-4: Disposal.
        /// </summary>
        private void main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }

            _cts.Cancel();
            _devicePresenter?.CancelAllOperations();
            _devicePresenter?.Dispose();

            if (_cardViews != null)
            {
                foreach (var view in _cardViews.Values)
                {
                    try { view.Card.Dispose(); } catch { }
                }
                _cardViews.Clear();
            }
            
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }

            if (_deviceChangeDebounceTimer != null)
            {
                _deviceChangeDebounceTimer.Stop();
                _deviceChangeDebounceTimer.Dispose();
                _deviceChangeDebounceTimer = null;
            }

            if (_bluetoothManager != null)
            {
                _bluetoothManager.AdapterStateChanged    -= BluetoothManager_AdapterStateChanged;
                _bluetoothManager.DeviceConnectionChanged -= OnDeviceConnectionChanged;
                _bluetoothManager.OperationProgressChanged -= BluetoothManager_OperationProgressChanged;
                _bluetoothManager.Dispose(); // Dispose also calls StopDeviceWatcher
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_trayMenu != null)
            {
                _trayMenu.Dispose();
                _trayMenu = null;
            }

            foreach (var cached in _iconCache.Values)
            {
                try { cached?.Dispose(); } catch { }
            }
            _iconCache.Clear();
        }
    }
}
