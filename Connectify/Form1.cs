using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    public partial class main : Form
    {
        public main()
        {
            InitializeComponent();
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

            // Center footer label
            labelFooter.Left = 0;
            labelFooter.Width = this.ClientSize.Width;
            labelFooter.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
        }

        private void on_off_CheckedChanged(object sender, Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs e)
        {
           

        }




    }
}
