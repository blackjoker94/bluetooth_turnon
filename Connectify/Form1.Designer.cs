
namespace bluetooth_turnon
{
    partial class main
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(main));
            Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState toggleState1 = new Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState();
            Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState toggleState2 = new Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState();
            Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState toggleState3 = new Bunifu.UI.WinForms.BunifuToggleSwitch.ToggleState();
            this.on_off = new Bunifu.UI.WinForms.BunifuToggleSwitch();
            this.bunifuLabel1 = new Bunifu.UI.WinForms.BunifuLabel();
            this.labelBluetooth = new Bunifu.UI.WinForms.BunifuLabel();
            this.labelStatus = new Bunifu.UI.WinForms.BunifuLabel();
            this.labelDevicesHeader = new Bunifu.UI.WinForms.BunifuLabel();
            this.panelHeader = new System.Windows.Forms.Panel();
            this.pictureBoxBT = new System.Windows.Forms.PictureBox();
            this.panelDivider = new System.Windows.Forms.Panel();
            this.panelDeviceList = new System.Windows.Forms.Panel();
            this.labelFooter = new Bunifu.UI.WinForms.BunifuLabel();
            this.panelHeader.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxBT)).BeginInit();
            this.SuspendLayout();
            // 
            // on_off
            // 
            this.on_off.Animation = 25;
            this.on_off.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(24)))), ((int)(((byte)(24)))), ((int)(((byte)(34)))));
            this.on_off.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("on_off.BackgroundImage")));
            this.on_off.Checked = false;
            this.on_off.Cursor = System.Windows.Forms.Cursors.Hand;
            this.on_off.InnerCirclePadding = 4;
            this.on_off.Location = new System.Drawing.Point(143, 199);
            this.on_off.Margin = new System.Windows.Forms.Padding(5);
            this.on_off.Name = "on_off";
            this.on_off.Size = new System.Drawing.Size(115, 44);
            this.on_off.TabIndex = 0;
            this.on_off.ThumbMargin = 4;
            toggleState1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(40)))), ((int)(((byte)(40)))), ((int)(((byte)(50)))));
            toggleState1.BackColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(80)))), ((int)(((byte)(90)))));
            toggleState1.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(50)))), ((int)(((byte)(50)))), ((int)(((byte)(60)))));
            toggleState1.BorderColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(80)))), ((int)(((byte)(90)))));
            toggleState1.BorderRadius = 18;
            toggleState1.BorderRadiusInner = 12;
            toggleState1.BorderThickness = 1;
            toggleState1.BorderThicknessInner = 1;
            this.on_off.ToggleStateDisabled = toggleState1;
            toggleState2.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(60)))), ((int)(((byte)(30)))), ((int)(((byte)(30)))));
            toggleState2.BackColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(200)))), ((int)(((byte)(80)))), ((int)(((byte)(80)))));
            toggleState2.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(40)))), ((int)(((byte)(40)))));
            toggleState2.BorderColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(200)))), ((int)(((byte)(80)))), ((int)(((byte)(80)))));
            toggleState2.BorderRadius = 18;
            toggleState2.BorderRadiusInner = 12;
            toggleState2.BorderThickness = 1;
            toggleState2.BorderThicknessInner = 1;
            this.on_off.ToggleStateOff = toggleState2;
            toggleState3.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(80)))), ((int)(((byte)(160)))));
            toggleState3.BackColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(100)))), ((int)(((byte)(180)))), ((int)(((byte)(255)))));
            toggleState3.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(50)))), ((int)(((byte)(110)))), ((int)(((byte)(200)))));
            toggleState3.BorderColorInner = System.Drawing.Color.FromArgb(((int)(((byte)(100)))), ((int)(((byte)(180)))), ((int)(((byte)(255)))));
            toggleState3.BorderRadius = 18;
            toggleState3.BorderRadiusInner = 12;
            toggleState3.BorderThickness = 1;
            toggleState3.BorderThicknessInner = 1;
            this.on_off.ToggleStateOn = toggleState3;
            this.on_off.Value = false;
            this.on_off.CheckedChanged += new System.EventHandler<Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs>(this.on_off_CheckedChanged);
            // 
            // bunifuLabel1
            // 
            this.bunifuLabel1.AllowParentOverrides = false;
            this.bunifuLabel1.AutoEllipsis = false;
            this.bunifuLabel1.Cursor = System.Windows.Forms.Cursors.Default;
            this.bunifuLabel1.CursorType = System.Windows.Forms.Cursors.Default;
            this.bunifuLabel1.Font = new System.Drawing.Font("Segoe UI", 1F);
            this.bunifuLabel1.ForeColor = System.Drawing.Color.Transparent;
            this.bunifuLabel1.Location = new System.Drawing.Point(0, 0);
            this.bunifuLabel1.Margin = new System.Windows.Forms.Padding(4);
            this.bunifuLabel1.Name = "bunifuLabel1";
            this.bunifuLabel1.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.bunifuLabel1.Size = new System.Drawing.Size(0, 0);
            this.bunifuLabel1.TabIndex = 99;
            this.bunifuLabel1.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.bunifuLabel1.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            this.bunifuLabel1.Click += new System.EventHandler(this.bunifuLabel1_Click);
            // 
            // labelBluetooth
            // 
            this.labelBluetooth.AllowParentOverrides = false;
            this.labelBluetooth.AutoEllipsis = false;
            this.labelBluetooth.AutoSize = false;
            this.labelBluetooth.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(24)))), ((int)(((byte)(24)))), ((int)(((byte)(34)))));
            this.labelBluetooth.Cursor = System.Windows.Forms.Cursors.Default;
            this.labelBluetooth.CursorType = System.Windows.Forms.Cursors.Default;
            this.labelBluetooth.Font = new System.Drawing.Font("Segoe UI", 18F, System.Drawing.FontStyle.Bold);
            this.labelBluetooth.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(240)))), ((int)(((byte)(240)))), ((int)(((byte)(255)))));
            this.labelBluetooth.Location = new System.Drawing.Point(126, 116);
            this.labelBluetooth.Margin = new System.Windows.Forms.Padding(4);
            this.labelBluetooth.Name = "labelBluetooth";
            this.labelBluetooth.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.labelBluetooth.Size = new System.Drawing.Size(152, 41);
            this.labelBluetooth.TabIndex = 102;
            this.labelBluetooth.Text = "Connectify";
            this.labelBluetooth.TextAlignment = System.Drawing.ContentAlignment.TopCenter;
            this.labelBluetooth.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // labelStatus
            // 
            this.labelStatus.AllowParentOverrides = false;
            this.labelStatus.AutoEllipsis = false;
            this.labelStatus.AutoSize = false;
            this.labelStatus.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(24)))), ((int)(((byte)(24)))), ((int)(((byte)(34)))));
            this.labelStatus.Cursor = System.Windows.Forms.Cursors.Default;
            this.labelStatus.CursorType = System.Windows.Forms.Cursors.Default;
            this.labelStatus.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.labelStatus.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(100)))), ((int)(((byte)(180)))), ((int)(((byte)(255)))));
            this.labelStatus.Location = new System.Drawing.Point(174, 170);
            this.labelStatus.Margin = new System.Windows.Forms.Padding(4);
            this.labelStatus.Name = "labelStatus";
            this.labelStatus.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.labelStatus.Size = new System.Drawing.Size(54, 20);
            this.labelStatus.TabIndex = 103;
            this.labelStatus.Text = "Enabled";
            this.labelStatus.TextAlignment = System.Drawing.ContentAlignment.TopCenter;
            this.labelStatus.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // labelDevicesHeader
            // 
            this.labelDevicesHeader.AllowParentOverrides = false;
            this.labelDevicesHeader.AutoEllipsis = false;
            this.labelDevicesHeader.Cursor = System.Windows.Forms.Cursors.Default;
            this.labelDevicesHeader.CursorType = System.Windows.Forms.Cursors.Default;
            this.labelDevicesHeader.Font = new System.Drawing.Font("Segoe UI", 7.5F, System.Drawing.FontStyle.Bold);
            this.labelDevicesHeader.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(100)))), ((int)(((byte)(180)))), ((int)(((byte)(255)))));
            this.labelDevicesHeader.Location = new System.Drawing.Point(27, 263);
            this.labelDevicesHeader.Margin = new System.Windows.Forms.Padding(4);
            this.labelDevicesHeader.Name = "labelDevicesHeader";
            this.labelDevicesHeader.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.labelDevicesHeader.Size = new System.Drawing.Size(92, 14);
            this.labelDevicesHeader.TabIndex = 104;
            this.labelDevicesHeader.Text = "CONNECTED DEVICES";
            this.labelDevicesHeader.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            this.labelDevicesHeader.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // panelHeader
            // 
            this.panelHeader.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(24)))), ((int)(((byte)(24)))), ((int)(((byte)(34)))));
            this.panelHeader.Controls.Add(this.pictureBoxBT);
            this.panelHeader.Controls.Add(this.labelBluetooth);
            this.panelHeader.Controls.Add(this.labelStatus);
            this.panelHeader.Controls.Add(this.on_off);
            this.panelHeader.Location = new System.Drawing.Point(0, 0);
            this.panelHeader.Margin = new System.Windows.Forms.Padding(4);
            this.panelHeader.Name = "panelHeader";
            this.panelHeader.Size = new System.Drawing.Size(400, 258);
            this.panelHeader.TabIndex = 100;
            // 
            // pictureBoxBT
            // 
            this.pictureBoxBT.BackColor = System.Drawing.Color.Transparent;
            this.pictureBoxBT.Image = ((System.Drawing.Image)(resources.GetObject("pictureBoxBT.Image")));
            this.pictureBoxBT.Location = new System.Drawing.Point(159, 32);
            this.pictureBoxBT.Margin = new System.Windows.Forms.Padding(4);
            this.pictureBoxBT.Name = "pictureBoxBT";
            this.pictureBoxBT.Size = new System.Drawing.Size(83, 76);
            this.pictureBoxBT.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBoxBT.TabIndex = 101;
            this.pictureBoxBT.TabStop = false;
            // 
            // panelDivider
            // 
            this.panelDivider.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(40)))), ((int)(((byte)(40)))), ((int)(((byte)(55)))));
            this.panelDivider.Location = new System.Drawing.Point(0, 258);
            this.panelDivider.Margin = new System.Windows.Forms.Padding(4);
            this.panelDivider.Name = "panelDivider";
            this.panelDivider.Size = new System.Drawing.Size(400, 1);
            this.panelDivider.TabIndex = 105;
            // 
            // panelDeviceList
            // 
            this.panelDeviceList.AutoScroll = true;
            this.panelDeviceList.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(18)))), ((int)(((byte)(18)))), ((int)(((byte)(24)))));
            this.panelDeviceList.Location = new System.Drawing.Point(0, 284);
            this.panelDeviceList.Margin = new System.Windows.Forms.Padding(0);
            this.panelDeviceList.Name = "panelDeviceList";
            this.panelDeviceList.Size = new System.Drawing.Size(400, 288);
            this.panelDeviceList.TabIndex = 110;
            // 
            // labelFooter
            // 
            this.labelFooter.AllowParentOverrides = false;
            this.labelFooter.AutoEllipsis = false;
            this.labelFooter.AutoSize = false;
            this.labelFooter.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(18)))), ((int)(((byte)(18)))), ((int)(((byte)(24)))));
            this.labelFooter.Font = new System.Drawing.Font("Segoe UI", 7.5F);
            this.labelFooter.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(60)))), ((int)(((byte)(80)))), ((int)(((byte)(110)))));
            this.labelFooter.Location = new System.Drawing.Point(0, 572);
            this.labelFooter.Name = "labelFooter";
            this.labelFooter.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.labelFooter.Size = new System.Drawing.Size(400, 26);
            this.labelFooter.TabIndex = 200;
            this.labelFooter.Text = "Connectify  v1.0";
            this.labelFooter.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            this.labelFooter.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // main
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(18)))), ((int)(((byte)(18)))), ((int)(((byte)(24)))));
            this.ClientSize = new System.Drawing.Size(400, 604);
            this.Controls.Add(this.panelHeader);
            this.Controls.Add(this.panelDivider);
            this.Controls.Add(this.labelDevicesHeader);
            this.Controls.Add(this.panelDeviceList);
            this.Controls.Add(this.labelFooter);
            this.Controls.Add(this.bunifuLabel1);
            this.DoubleBuffered = true;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(0);
            this.MaximizeBox = false;
            this.Name = "main";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Connectify";
            this.Load += new System.EventHandler(this.main_Load);
            this.panelHeader.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxBT)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private Bunifu.UI.WinForms.BunifuToggleSwitch on_off;
        private Bunifu.UI.WinForms.BunifuLabel bunifuLabel1;
        private Bunifu.UI.WinForms.BunifuLabel labelBluetooth;
        private Bunifu.UI.WinForms.BunifuLabel labelStatus;
        private Bunifu.UI.WinForms.BunifuLabel labelDevicesHeader;
        private System.Windows.Forms.Panel panelHeader;
        private System.Windows.Forms.Panel panelDivider;
        private System.Windows.Forms.Panel panelDeviceList;
        private System.Windows.Forms.PictureBox pictureBoxBT;
        private Bunifu.UI.WinForms.BunifuLabel labelFooter;
    }
}
