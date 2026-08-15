using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private bool _isUpdatingUiFromState;
        private bool _isRefreshingDevices; // guard against overlapping watcher-triggered refreshes

        // Alternating row background colors, matching the old hardcoded style
        private static readonly Color RowColorOdd  = Color.FromArgb(18, 18, 24);
        private static readonly Color RowColorEven = Color.FromArgb(22, 22, 30);

        public main()
        {
            InitializeComponent();
            _bluetoothManager = new BluetoothManager();
            _bluetoothManager.AdapterStateChanged    += BluetoothManager_AdapterStateChanged;
            _bluetoothManager.DeviceConnectionChanged += OnDeviceConnectionChanged;
            this.FormClosing += main_FormClosing;
        }

        private void bunifuLabel1_Click(object sender, EventArgs e)
        {

        }

        private async void main_Load(object sender, EventArgs e)
        {
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

            // Load devices initially, then start the live watcher
            await RefreshDevicesAsync();
            _bluetoothManager.StartDeviceWatcher();
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
                BeginInvoke(new Action(() => ApplyAdapterStateToUi(isEnabled)));
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
                // Adapter just turned on — refresh device list and restart watcher
                await RefreshDevicesAsync();
                _bluetoothManager.StartDeviceWatcher();
            }
        }

        // ── Devices ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called by DeviceWatcher (background thread) whenever a Bluetooth device
        /// connects or disconnects. Guards against overlapping concurrent refreshes.
        /// </summary>
        private async void OnDeviceConnectionChanged(object sender, EventArgs e)
        {
            if (_isRefreshingDevices) return;
            await RefreshDevicesAsync();
        }

        private async Task RefreshDevicesAsync()
        {
            _isRefreshingDevices = true;
            try
            {
                var devices = await _bluetoothManager.GetPairedDevicesAsync();
                BuildDeviceCards(devices);
            }
            catch
            {
                BuildDeviceCards(new List<DeviceInfo>());
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
                BeginInvoke(new Action(() => BuildDeviceCards(devices)));
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
                    Font = new Font("Segoe UI", 10F),
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
                Color bgColor = isOdd ? RowColorOdd : RowColorEven;

                // Card panel
                var card = new Panel
                {
                    BackColor = bgColor,
                    Location = new Point(0, y),
                    Size = new Size(panelDeviceList.Width, cardHeight),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                };

                // ── Type-specific icon ────────────────────────────────────
                string iconFile = GetDeviceIconFileName(device.DeviceType);
                Color  accentColor = GetDeviceAccentColor(device.DeviceType);

                // Outer circle (drawn on a Panel via Paint)
                var iconPanel = new Panel
                {
                    BackColor = bgColor,
                    Location  = new Point(14, 12),
                    Size      = new Size(48, 48)
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
                    Location  = new Point(11, 11) // Centered inside the 48x48 circle
                };

                // Load the image dynamically from the assets folder
                string assetsDir = Path.Combine(Application.StartupPath, @"..\..\assets");
                string iconPath = Path.Combine(assetsDir, iconFile);
                if (System.IO.File.Exists(iconPath))
                {
                    iconBox.Image = Image.FromFile(iconPath);
                }

                iconPanel.Controls.Add(iconBox);

                // ── Device name ────────────────────────────────────────────────
                var nameLabel = new Label
                {
                    Text      = device.Name,
                    ForeColor = Color.FromArgb(220, 220, 235),
                    Font      = new Font("Segoe UI", 10F, FontStyle.Regular),
                    AutoSize  = false,
                    TextAlign = ContentAlignment.BottomLeft,
                    BackColor = Color.Transparent,
                    Location  = new Point(74, 10),
                    Size      = new Size(card.Width - 80, 26)
                };

                // ── Device type subtitle ────────────────────────────────────────
                string typeText = GetDeviceTypeLabel(device.DeviceType);
                var typeLabel = new Label
                {
                    Text      = typeText,
                    ForeColor = Color.FromArgb(80, capturedAccent.R, capturedAccent.G, capturedAccent.B),
                    Font      = new Font("Segoe UI", 8F),
                    AutoSize  = false,
                    TextAlign = ContentAlignment.TopLeft,
                    BackColor = Color.Transparent,
                    Location  = new Point(74, 38),
                    Size      = new Size(card.Width - 80, 20)
                };

                card.Controls.Add(iconPanel);
                card.Controls.Add(nameLabel);
                card.Controls.Add(typeLabel);
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
            if (_bluetoothManager != null)
            {
                _bluetoothManager.AdapterStateChanged    -= BluetoothManager_AdapterStateChanged;
                _bluetoothManager.DeviceConnectionChanged -= OnDeviceConnectionChanged;
                _bluetoothManager.Dispose(); // Dispose also calls StopDeviceWatcher
            }
        }
    }
}
