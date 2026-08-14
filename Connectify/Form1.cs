using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        // BluetoothManager instance — created here, disposed on FormClosing.
        // Placeholder until Bluetooth logic is wired up.
        private BluetoothManager _bluetoothManager;

        public main()
        {
            InitializeComponent();
            _bluetoothManager = new BluetoothManager();
            this.FormClosing += main_FormClosing;
        }

        private void bunifuLabel1_Click(object sender, EventArgs e)
        {

        }

        private void main_Load(object sender, EventArgs e)
        {
            // The icon and logo are now loaded directly through the Designer (Form1.resx),
            // so we no longer need to load them manually at runtime!

            // Force center the title — labels live inside panelHeader, so use its width
            labelBluetooth.Left = 0;
            labelBluetooth.Width = panelHeader.Width;
            labelBluetooth.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;

            labelStatus.Left = 0;
            labelStatus.Width = panelHeader.Width;
            labelStatus.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;

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
            labelFooter.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
        }

        private void on_off_CheckedChanged(object sender, Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs e)
        {
            // TODO: call _bluetoothManager.SetAdapterStateAsync(on_off.Checked)
            // Use async void carefully — this is the top-level UI entry point.
        }

        /// <summary>
        /// Disposes the BluetoothManager (stops BLE watchers, releases adapter handles)
        /// when the form is closing. Rule B-4: Disposal.
        /// </summary>
        private void main_FormClosing(object sender, FormClosingEventArgs e)
        {
            _bluetoothManager?.Dispose();
        }




    }
}
