using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private bool _isUpdatingUiFromState;

        public main()
        {
            InitializeComponent();
            _bluetoothManager = new BluetoothManager();
            _bluetoothManager.AdapterStateChanged += BluetoothManager_AdapterStateChanged;
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

            // Fix device name label widths so text isn't clipped at any DPI
            // Icon sits at X=341, so labels can go from X=27 to about X=335
            int nameWidth = pictureBox1.Left - 27 - 10; // 10px gap before icon
            int formWidth = this.ClientSize.Width;

            labelDevicesHeader.Width = formWidth - 40;

            deviceName1.Width = nameWidth;
            deviceName2.Width = nameWidth;
            deviceName3.Width = nameWidth;
            deviceName4.Width = nameWidth;

            devicePer1.Width = nameWidth;
            devicePer2.Width = nameWidth;
            devicePer3.Width = nameWidth;
            devicePer4.Width = nameWidth;

            // Battery labels: show N/A at startup — never show a stale "0%".
            // Rule B-2: battery is optional/nullable per device.
            devicePer1.Text = "N/A";
            devicePer2.Text = "N/A";
            devicePer3.Text = "N/A";
            devicePer4.Text = "N/A";

            // Center footer label
            labelFooter.Left = 0;
            labelFooter.Width = this.ClientSize.Width;
            labelFooter.TextAlignment = ContentAlignment.MiddleCenter;

            // Detect actual Bluetooth adapter state on form load (Rule B-2)
            await RefreshAdapterStateAsync();
        }

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
                }
                else if (isEnabled == false)
                {
                    labelStatus.Text = "Disabled";
                    labelStatus.ForeColor = Color.FromArgb(200, 80, 80);
                    on_off.Enabled = true;
                    on_off.Value = false;
                }
                else
                {
                    labelStatus.Text = "No Adapter";
                    labelStatus.ForeColor = Color.FromArgb(140, 140, 150);
                    on_off.Enabled = false;
                    on_off.Value = false;
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
        }

        /// <summary>
        /// Disposes the BluetoothManager (stops BLE watchers, releases adapter handles)
        /// when the form is closing. Rule B-4: Disposal.
        /// </summary>
        private void main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_bluetoothManager != null)
            {
                _bluetoothManager.AdapterStateChanged -= BluetoothManager_AdapterStateChanged;
                _bluetoothManager.Dispose();
            }
        }
    }
}
