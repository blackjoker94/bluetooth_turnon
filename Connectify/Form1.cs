using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private bool _isUpdatingUiFromState;
        private bool _isRefreshingDevices; // guard against overlapping watcher-triggered refreshes
        private bool _refreshPending;
        private readonly HashSet<ulong> _connectingAddresses = new HashSet<ulong>();
        private readonly System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();

        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private Timer _refreshTimer;

        public bool StartMinimized { get; set; }

        protected override void SetVisibleCore(bool value)
        {
            if (StartMinimized && !this.IsHandleCreated)
            {
                value = false;
                CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        /// <summary>
        /// Marshals an action to the UI thread safely.
        /// Verifies IsHandleCreated and !IsDisposed before invoking to avoid handle exceptions.
        /// </summary>
        private void SafeBeginInvoke(Action action)
        {
            if (action == null || IsDisposed) return;

            if (!IsHandleCreated) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
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

        private bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (rk != null)
                    {
                        return rk.GetValue("Connectify") != null;
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
                    img = Image.FromStream(new MemoryStream(System.IO.File.ReadAllBytes(path)));
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
            _bluetoothManager = new BluetoothManager();
            _bluetoothManager.AdapterStateChanged    += BluetoothManager_AdapterStateChanged;
            _bluetoothManager.DeviceConnectionChanged += OnDeviceConnectionChanged;
            this.FormClosing += main_FormClosing;
        }



        /// <summary>
        /// Restores and activates the form if hidden or minimized in tray.
        /// Thread-safe via SafeBeginInvoke.
        /// </summary>
        public void ShowAndActivate()
        {
            if (InvokeRequired)
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
        }

        private async void main_Load(object sender, EventArgs e)
        {
            // Setup NotifyIcon
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show", null, (s, ev) => ShowAndActivate());
            
            var startupMenuItem = new ToolStripMenuItem("Start with Windows");
            startupMenuItem.CheckOnClick = true;
            startupMenuItem.Checked = IsStartupEnabled();
            startupMenuItem.CheckedChanged += (s, ev) => SetStartup(startupMenuItem.Checked);
            _trayMenu.Items.Add(startupMenuItem);
            
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
            _refreshTimer.Tick += async (s, ev) => await RefreshDevicesIfIdleAsync();
            _refreshTimer.Start();

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

            labelDevicesHeader.Width = this.ClientSize.Width - 40;

            // Center footer label
            labelFooter.Left = 0;
            labelFooter.Width = this.ClientSize.Width;
            labelFooter.TextAlignment = ContentAlignment.MiddleCenter;

            // Show empty state immediately so something is visible while BT queries run
            BuildDeviceCards(new List<DeviceInfo>());

            // Detect actual Bluetooth adapter state on form load (Rule B-2)
            await RefreshAdapterStateAsync();

            // Load devices initially
            await RefreshDevicesAsync();
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

        private void ApplyAdapterStateToUi(bool? isEnabled)
        {
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => ApplyAdapterStateToUi(isEnabled));
                return;
            }

            _isUpdatingUiFromState = true;
            try
            {
                if (isEnabled == true)
                {
                    labelStatus.Text = "Enabled";
                    labelStatus.ForeColor = Color.FromArgb(100, 180, 255);
                    on_off.Enabled = true;
                    on_off.Value = true;
                    _bluetoothManager.StartDeviceWatcher();
                }
                else if (isEnabled == false)
                {
                    labelStatus.Text = "Disabled";
                    labelStatus.ForeColor = Color.FromArgb(200, 80, 80);
                    on_off.Enabled = true;
                    on_off.Value = false;
                    _bluetoothManager.StopDeviceWatcher();
                    // Clear device list immediately when adapter is off
                    BuildDeviceCards(new List<DeviceInfo>());
                }
                else
                {
                    labelStatus.Text = "No Adapter";
                    labelStatus.ForeColor = Color.FromArgb(140, 140, 150);
                    on_off.Enabled = false;
                    on_off.Value = false;
                    _bluetoothManager.StopDeviceWatcher();
                    BuildDeviceCards(new List<DeviceInfo>());
                }
            }
            finally
            {
                _isUpdatingUiFromState = false;
            }
        }

        private void BluetoothManager_AdapterStateChanged(object sender, AdapterStateChangedEventArgs e)
        {
            ApplyAdapterStateToUi(e.IsEnabled);
        }

        private async void on_off_CheckedChanged(object sender, Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs e)
        {
            if (_isUpdatingUiFromState) return;

            bool targetState = e.Checked;

            // Temporary visual feedback
            labelStatus.Text = targetState ? "Turning On..." : "Turning Off...";
            labelStatus.ForeColor = Color.FromArgb(255, 200, 100);

            bool success = await _bluetoothManager.SetAdapterStateAsync(targetState);
            if (!success)
            {
                // Revert and refresh from actual hardware state if toggle failed
                await RefreshAdapterStateAsync();
            }
            else if (targetState)
            {
                // Adapter just turned on — refresh device list
                await RefreshDevicesAsync();
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called by DeviceWatcher (background thread) whenever a Bluetooth device
        /// connects or disconnects. Guards against overlapping concurrent refreshes.
        /// </summary>
        private void OnDeviceConnectionChanged(object sender, EventArgs e)
        {
            SafeBeginInvoke(async () =>
            {
                try
                {
                    await RefreshDevicesIfIdleAsync();
                }
                catch { }
            });
        }

        private async Task RefreshDevicesIfIdleAsync()
        {
            if (_isRefreshingDevices)
            {
                _refreshPending = true;
                return;
            }
            await RefreshDevicesAsync();
        }

        private async Task RefreshDevicesAsync()
        {
            if (_isRefreshingDevices)
            {
                _refreshPending = true;
                return;
            }

            _isRefreshingDevices = true;
            try
            {
                do
                {
                    _refreshPending = false;
                    try
                    {
                        var devices = await _bluetoothManager.GetPairedDevicesAsync(_cts.Token);
                        BuildDeviceCards(devices);
                    }
                    catch
                    {
                        BuildDeviceCards(new List<DeviceInfo>());
                    }
                } while (_refreshPending && !_cts.IsCancellationRequested);
            }
            finally
            {
                _isRefreshingDevices = false;
            }
        }

        /// <summary>
        /// Clears panelDeviceList and rebuilds one card per device.
        /// Must be called from the UI thread (or will marshal via InvokeRequired).
        /// </summary>
        private void BuildDeviceCards(IReadOnlyList<DeviceInfo> devices)
        {
            if (InvokeRequired)
            {
                SafeBeginInvoke(() => BuildDeviceCards(devices));
                return;
            }

            // Dispose existing child controls and clear
            foreach (Control ctrl in panelDeviceList.Controls)
                ctrl.Dispose();
            panelDeviceList.Controls.Clear();

            if (devices == null || devices.Count == 0)
            {
                var emptyLabel = new Label
                {
                    Text = "No devices connected",
                    ForeColor = Color.FromArgb(100, 100, 120),
                    Font = EmptyStateFont,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(panelDeviceList.Width, 80),
                    Location = new Point(0, 20),
                    BackColor = Color.Transparent
                };
                panelDeviceList.Controls.Add(emptyLabel);
                return;
            }

            int cardHeight = 72;
            int y = 0;

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                bool isOdd = (i % 2 == 0);
                Color baseBgColor = isOdd ? RowColorOdd : RowColorEven;
                Color hoverBgColor = Color.FromArgb(baseBgColor.R + 10, baseBgColor.G + 10, baseBgColor.B + 15);

                // Card panel
                var card = new Panel
                {
                    BackColor = baseBgColor,
                    Location = new Point(0, y),
                    Size = new Size(panelDeviceList.Width, cardHeight),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                    Cursor = Cursors.Hand
                };

                // ── Type-specific icon ────────────────────────────────────
                string iconFile = GetDeviceIconFileName(device.DeviceType);
                Color  accentColor = GetDeviceAccentColor(device.DeviceType);
                if (!device.IsConnected)
                {
                    accentColor = Color.FromArgb(70, 70, 80); // Dimmer gray for disconnected
                }

                // Outer circle (drawn on a Panel via Paint)
                var iconPanel = new Panel
                {
                    BackColor = baseBgColor,
                    Location  = new Point(14, 12),
                    Size      = new Size(48, 48),
                    Cursor    = Cursors.Hand
                };
                var capturedAccent = accentColor; // capture for lambda
                iconPanel.Paint += (s, pe) =>
                {
                    pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var fill = new SolidBrush(Color.FromArgb(28, capturedAccent)))
                        pe.Graphics.FillEllipse(fill, 0, 0, 47, 47);
                    using (var border = new Pen(Color.FromArgb(90, capturedAccent), 1.5f))
                        pe.Graphics.DrawEllipse(border, 1, 1, 45, 45);
                };

                // PictureBox for the custom PNG icon
                var iconBox = new PictureBox
                {
                    BackColor = Color.Transparent,
                    SizeMode  = PictureBoxSizeMode.Zoom,
                    Size      = new Size(26, 26),
                    Location  = new Point(11, 11), // Centered inside the 48x48 circle
                    Cursor    = Cursors.Hand
                };

                // Load the image dynamically from the assets folder
                string assetsDir = Path.Combine(Application.StartupPath, "assets");
                
                Image iconImg = LoadCachedIcon(assetsDir, iconFile);
                if (iconImg != null)
                {
                    // Basic grayscale effect could go here, but opacity/dimming via accentColor is fine
                    iconBox.Image = iconImg;
                }

                iconPanel.Controls.Add(iconBox);

                // ── Battery block constants ─────────────────────────────────────
                // Reserve a 76px column on the right for: [icon 28px] [gap 4px] [label 44px]
                const int batteryColWidth = 76;
                const int rightPad        = 10;
                int batteryColX = card.Width - batteryColWidth - rightPad;

                // ── Device name ────────────────────────────────────────────────
                var nameLabel = new Label
                {
                    Text      = device.Name,
                    ForeColor = device.IsConnected ? Color.FromArgb(220, 220, 235) : Color.FromArgb(140, 140, 150),
                    Font      = CardNameFont,
                    AutoSize  = false,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.BottomLeft,
                    BackColor = Color.Transparent,
                    Location  = new Point(74, 10),
                    // Stop well before the battery column
                    Size      = new Size(batteryColX - 74 - 6, 26),
                    Anchor    = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                    Cursor    = Cursors.Hand
                };

                // ── Device type subtitle ────────────────────────────────────────
                string typeText = GetDeviceTypeLabel(device.DeviceType);
                Color  typeColor = device.IsConnected ? Color.FromArgb(120, capturedAccent.R, capturedAccent.G, capturedAccent.B) : Color.FromArgb(100, 100, 110);
                if (_connectingAddresses.Contains(device.Address))
                {
                    typeText = device.IsConnected ? "Disconnecting..." : "Connecting...";
                    typeColor = Color.FromArgb(255, 190, 50); // Amber
                }

                var typeLabel = new Label
                {
                    Text      = typeText,
                    ForeColor = typeColor,
                    Font      = CardTypeFont,
                    AutoSize  = false,
                    TextAlign = ContentAlignment.TopLeft,
                    BackColor = Color.Transparent,
                    Location  = new Point(74, 38),
                    Size      = new Size(batteryColX - 74 - 6, 20),
                    Anchor    = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                    Cursor    = Cursors.Hand
                };

                // ── Battery icon + percentage ───────────────────────────────────
                string batteryIcon  = GetBatteryIconFileName(device.BatteryPercent);
                Color  batteryColor = GetBatteryColor(device.BatteryPercent);
                string batteryText  = device.BatteryPercent.HasValue ? $"{device.BatteryPercent.Value}%" : "";

                // Battery icon: 28×28, left of the label
                var batteryBox = new PictureBox
                {
                    BackColor = Color.Transparent,
                    SizeMode  = PictureBoxSizeMode.Zoom,
                    Size      = new Size(28, 28),
                    Location  = new Point(batteryColX, (cardHeight - 28) / 2),
                    Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                    Cursor    = Cursors.Hand
                };

                if (device.IsConnected && batteryIcon != null)
                {
                    Image battImg = LoadCachedIcon(assetsDir, batteryIcon);
                    if (battImg != null)
                        batteryBox.Image = battImg;
                }

                // Battery percentage label: 44px wide, right next to the icon
                var batteryLabel = new Label
                {
                    Text      = device.IsConnected ? batteryText : "",
                    ForeColor = batteryColor,
                    Font      = CardBatteryFont,
                    AutoSize  = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Color.Transparent,
                    Location  = new Point(batteryColX + 32, (cardHeight - 20) / 2),
                    Size      = new Size(44, 20),
                    Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                    Cursor    = Cursors.Hand
                };

                // ── Interactivity (Hover & Click) ──────────────────────────────
                EventHandler onEnter = (s, e) => { card.BackColor = hoverBgColor; iconPanel.BackColor = hoverBgColor; };
                EventHandler onLeave = (s, e) => { card.BackColor = baseBgColor; iconPanel.BackColor = baseBgColor; };

                EventHandler onClick = async (s, e) => 
                {
                    ulong addr = device.Address;
                    if (!_connectingAddresses.Add(addr))
                    {
                        // Ignore duplicate clicks while operation is already in flight
                        return;
                    }

                    try
                    {
                        bool isConnecting = !device.IsConnected;

                        // Visual feedback
                        if (!typeLabel.IsDisposed)
                        {
                            typeLabel.Text = isConnecting ? "Connecting..." : "Disconnecting...";
                            typeLabel.ForeColor = Color.FromArgb(255, 190, 50); // Amber
                        }

                        bool success;
                        if (isConnecting)
                        {
                            success = await _bluetoothManager.ConnectDeviceAsync(addr);
                        }
                        else
                        {
                            success = await _bluetoothManager.DisconnectDeviceAsync(addr);
                        }

                        if (!success && isConnecting)
                        {
                            if (!typeLabel.IsDisposed)
                            {
                                typeLabel.Text = "Connection failed";
                                typeLabel.ForeColor = Color.FromArgb(230, 80, 80); // Red
                            }
                            await Task.Delay(1500);
                        }
                        else
                        {
                            // Allow UI to show feedback briefly before refreshing
                            await Task.Delay(1000);
                        }
                    }
                    catch
                    {
                        if (!typeLabel.IsDisposed)
                        {
                            typeLabel.Text = "Connection failed";
                            typeLabel.ForeColor = Color.FromArgb(230, 80, 80); // Red
                        }
                        await Task.Delay(1500);
                    }
                    finally
                    {
                        _connectingAddresses.Remove(addr);
                        await RefreshDevicesIfIdleAsync();
                    }
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

                panelDeviceList.Controls.Add(card);

                y += cardHeight;
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
            
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }

            if (_bluetoothManager != null)
            {
                _bluetoothManager.AdapterStateChanged    -= BluetoothManager_AdapterStateChanged;
                _bluetoothManager.DeviceConnectionChanged -= OnDeviceConnectionChanged;
                _bluetoothManager.Dispose(); // Dispose also calls StopDeviceWatcher
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
    }
}
