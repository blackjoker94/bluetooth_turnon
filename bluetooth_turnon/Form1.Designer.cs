
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
            this.deviceName1 = new Bunifu.UI.WinForms.BunifuLabel();
            this.devicePer1 = new Bunifu.UI.WinForms.BunifuLabel();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.pictureBox2 = new System.Windows.Forms.PictureBox();
            this.devicePer2 = new Bunifu.UI.WinForms.BunifuLabel();
            this.deviceName2 = new Bunifu.UI.WinForms.BunifuLabel();
            this.pictureBox3 = new System.Windows.Forms.PictureBox();
            this.devicePer3 = new Bunifu.UI.WinForms.BunifuLabel();
            this.deviceName3 = new Bunifu.UI.WinForms.BunifuLabel();
            this.pictureBox4 = new System.Windows.Forms.PictureBox();
            this.devicePer4 = new Bunifu.UI.WinForms.BunifuLabel();
            this.deviceName4 = new Bunifu.UI.WinForms.BunifuLabel();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).BeginInit();
            this.SuspendLayout();
            // 
            // on_off
            // 
            this.on_off.Animation = 5;
            this.on_off.BackColor = System.Drawing.Color.Transparent;
            this.on_off.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("on_off.BackgroundImage")));
            this.on_off.Checked = true;
            this.on_off.Cursor = System.Windows.Forms.Cursors.Hand;
            this.on_off.InnerCirclePadding = 3;
            this.on_off.Location = new System.Drawing.Point(75, 79);
            this.on_off.Name = "on_off";
            this.on_off.Size = new System.Drawing.Size(76, 41);
            this.on_off.TabIndex = 0;
            this.on_off.ThumbMargin = 3;
            toggleState1.BackColor = System.Drawing.Color.Gray;
            toggleState1.BackColorInner = System.Drawing.Color.White;
            toggleState1.BorderColor = System.Drawing.Color.DarkGray;
            toggleState1.BorderColorInner = System.Drawing.Color.White;
            toggleState1.BorderRadius = 17;
            toggleState1.BorderRadiusInner = 11;
            toggleState1.BorderThickness = 1;
            toggleState1.BorderThicknessInner = 1;
            this.on_off.ToggleStateDisabled = toggleState1;
            toggleState2.BackColor = System.Drawing.Color.Red;
            toggleState2.BackColorInner = System.Drawing.Color.White;
            toggleState2.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(191)))), ((int)(((byte)(191)))), ((int)(((byte)(191)))));
            toggleState2.BorderColorInner = System.Drawing.Color.White;
            toggleState2.BorderRadius = 17;
            toggleState2.BorderRadiusInner = 11;
            toggleState2.BorderThickness = 1;
            toggleState2.BorderThicknessInner = 1;
            this.on_off.ToggleStateOff = toggleState2;
            toggleState3.BackColor = System.Drawing.Color.Green;
            toggleState3.BackColorInner = System.Drawing.Color.White;
            toggleState3.BorderColor = System.Drawing.Color.Green;
            toggleState3.BorderColorInner = System.Drawing.Color.White;
            toggleState3.BorderRadius = 17;
            toggleState3.BorderRadiusInner = 11;
            toggleState3.BorderThickness = 1;
            toggleState3.BorderThicknessInner = 1;
            this.on_off.ToggleStateOn = toggleState3;
            this.on_off.Value = true;
            this.on_off.CheckedChanged += new System.EventHandler<Bunifu.UI.WinForms.BunifuToggleSwitch.CheckedChangedEventArgs>(this.on_off_CheckedChanged);
            // 
            // bunifuLabel1
            // 
            this.bunifuLabel1.AllowParentOverrides = false;
            this.bunifuLabel1.AutoEllipsis = false;
            this.bunifuLabel1.Cursor = System.Windows.Forms.Cursors.Default;
            this.bunifuLabel1.CursorType = System.Windows.Forms.Cursors.Default;
            this.bunifuLabel1.Font = new System.Drawing.Font("Segoe UI Black", 13.8F, System.Drawing.FontStyle.Bold);
            this.bunifuLabel1.ForeColor = System.Drawing.Color.Black;
            this.bunifuLabel1.Location = new System.Drawing.Point(23, 38);
            this.bunifuLabel1.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
            this.bunifuLabel1.Name = "bunifuLabel1";
            this.bunifuLabel1.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.bunifuLabel1.Size = new System.Drawing.Size(197, 25);
            this.bunifuLabel1.TabIndex = 1;
            this.bunifuLabel1.Text = "Turn on/off Bluetooth";
            this.bunifuLabel1.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.bunifuLabel1.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            this.bunifuLabel1.Click += new System.EventHandler(this.bunifuLabel1_Click);
            // 
            // deviceName1
            // 
            this.deviceName1.AllowParentOverrides = false;
            this.deviceName1.AutoEllipsis = false;
            this.deviceName1.CursorType = System.Windows.Forms.Cursors.Default;
            this.deviceName1.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.deviceName1.Location = new System.Drawing.Point(12, 182);
            this.deviceName1.Name = "deviceName1";
            this.deviceName1.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.deviceName1.Size = new System.Drawing.Size(145, 20);
            this.deviceName1.TabIndex = 2;
            this.deviceName1.Text = "No device connected";
            this.deviceName1.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.deviceName1.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // devicePer1
            // 
            this.devicePer1.AllowParentOverrides = false;
            this.devicePer1.AutoEllipsis = false;
            this.devicePer1.CursorType = null;
            this.devicePer1.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.devicePer1.Location = new System.Drawing.Point(183, 182);
            this.devicePer1.Name = "devicePer1";
            this.devicePer1.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.devicePer1.Size = new System.Drawing.Size(22, 20);
            this.devicePer1.TabIndex = 3;
            this.devicePer1.Text = "0%";
            this.devicePer1.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.devicePer1.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox1.Image")));
            this.pictureBox1.Location = new System.Drawing.Point(206, 179);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(23, 27);
            this.pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox1.TabIndex = 4;
            this.pictureBox1.TabStop = false;
            // 
            // pictureBox2
            // 
            this.pictureBox2.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox2.Image")));
            this.pictureBox2.Location = new System.Drawing.Point(206, 223);
            this.pictureBox2.Name = "pictureBox2";
            this.pictureBox2.Size = new System.Drawing.Size(23, 27);
            this.pictureBox2.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox2.TabIndex = 7;
            this.pictureBox2.TabStop = false;
            // 
            // devicePer2
            // 
            this.devicePer2.AllowParentOverrides = false;
            this.devicePer2.AutoEllipsis = false;
            this.devicePer2.CursorType = null;
            this.devicePer2.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.devicePer2.Location = new System.Drawing.Point(183, 226);
            this.devicePer2.Name = "devicePer2";
            this.devicePer2.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.devicePer2.Size = new System.Drawing.Size(22, 20);
            this.devicePer2.TabIndex = 6;
            this.devicePer2.Text = "0%";
            this.devicePer2.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.devicePer2.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // deviceName2
            // 
            this.deviceName2.AllowParentOverrides = false;
            this.deviceName2.AutoEllipsis = false;
            this.deviceName2.CursorType = null;
            this.deviceName2.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.deviceName2.Location = new System.Drawing.Point(12, 226);
            this.deviceName2.Name = "deviceName2";
            this.deviceName2.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.deviceName2.Size = new System.Drawing.Size(145, 20);
            this.deviceName2.TabIndex = 5;
            this.deviceName2.Text = "No device connected";
            this.deviceName2.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.deviceName2.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // pictureBox3
            // 
            this.pictureBox3.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox3.Image")));
            this.pictureBox3.Location = new System.Drawing.Point(206, 264);
            this.pictureBox3.Name = "pictureBox3";
            this.pictureBox3.Size = new System.Drawing.Size(23, 27);
            this.pictureBox3.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox3.TabIndex = 10;
            this.pictureBox3.TabStop = false;
            // 
            // devicePer3
            // 
            this.devicePer3.AllowParentOverrides = false;
            this.devicePer3.AutoEllipsis = false;
            this.devicePer3.CursorType = null;
            this.devicePer3.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.devicePer3.Location = new System.Drawing.Point(183, 267);
            this.devicePer3.Name = "devicePer3";
            this.devicePer3.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.devicePer3.Size = new System.Drawing.Size(22, 20);
            this.devicePer3.TabIndex = 9;
            this.devicePer3.Text = "0%";
            this.devicePer3.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.devicePer3.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // deviceName3
            // 
            this.deviceName3.AllowParentOverrides = false;
            this.deviceName3.AutoEllipsis = false;
            this.deviceName3.CursorType = null;
            this.deviceName3.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.deviceName3.Location = new System.Drawing.Point(12, 267);
            this.deviceName3.Name = "deviceName3";
            this.deviceName3.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.deviceName3.Size = new System.Drawing.Size(145, 20);
            this.deviceName3.TabIndex = 8;
            this.deviceName3.Text = "No device connected";
            this.deviceName3.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.deviceName3.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // pictureBox4
            // 
            this.pictureBox4.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox4.Image")));
            this.pictureBox4.Location = new System.Drawing.Point(206, 306);
            this.pictureBox4.Name = "pictureBox4";
            this.pictureBox4.Size = new System.Drawing.Size(23, 27);
            this.pictureBox4.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox4.TabIndex = 13;
            this.pictureBox4.TabStop = false;
            // 
            // devicePer4
            // 
            this.devicePer4.AllowParentOverrides = false;
            this.devicePer4.AutoEllipsis = false;
            this.devicePer4.CursorType = null;
            this.devicePer4.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.devicePer4.Location = new System.Drawing.Point(183, 309);
            this.devicePer4.Name = "devicePer4";
            this.devicePer4.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.devicePer4.Size = new System.Drawing.Size(22, 20);
            this.devicePer4.TabIndex = 12;
            this.devicePer4.Text = "0%";
            this.devicePer4.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.devicePer4.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // deviceName4
            // 
            this.deviceName4.AllowParentOverrides = false;
            this.deviceName4.AutoEllipsis = false;
            this.deviceName4.CursorType = null;
            this.deviceName4.Font = new System.Drawing.Font("Segoe UI", 11.25F, System.Drawing.FontStyle.Bold);
            this.deviceName4.Location = new System.Drawing.Point(12, 309);
            this.deviceName4.Name = "deviceName4";
            this.deviceName4.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.deviceName4.Size = new System.Drawing.Size(145, 20);
            this.deviceName4.TabIndex = 11;
            this.deviceName4.Text = "No device connected";
            this.deviceName4.TextAlignment = System.Drawing.ContentAlignment.TopLeft;
            this.deviceName4.TextFormat = Bunifu.UI.WinForms.BunifuLabel.TextFormattingOptions.Default;
            // 
            // main
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(231, 366);
            this.Controls.Add(this.pictureBox4);
            this.Controls.Add(this.devicePer4);
            this.Controls.Add(this.deviceName4);
            this.Controls.Add(this.pictureBox3);
            this.Controls.Add(this.devicePer3);
            this.Controls.Add(this.deviceName3);
            this.Controls.Add(this.pictureBox2);
            this.Controls.Add(this.devicePer2);
            this.Controls.Add(this.deviceName2);
            this.Controls.Add(this.pictureBox1);
            this.Controls.Add(this.devicePer1);
            this.Controls.Add(this.deviceName1);
            this.Controls.Add(this.bunifuLabel1);
            this.Controls.Add(this.on_off);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(2, 2, 2, 2);
            this.MaximizeBox = false;
            this.Name = "main";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Bluetooth";
            this.Load += new System.EventHandler(this.main_Load);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private Bunifu.UI.WinForms.BunifuToggleSwitch on_off;
        private Bunifu.UI.WinForms.BunifuLabel bunifuLabel1;
        private Bunifu.UI.WinForms.BunifuLabel deviceName1;
        private Bunifu.UI.WinForms.BunifuLabel devicePer1;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.PictureBox pictureBox2;
        private Bunifu.UI.WinForms.BunifuLabel devicePer2;
        private Bunifu.UI.WinForms.BunifuLabel deviceName2;
        private System.Windows.Forms.PictureBox pictureBox3;
        private Bunifu.UI.WinForms.BunifuLabel devicePer3;
        private Bunifu.UI.WinForms.BunifuLabel deviceName3;
        private System.Windows.Forms.PictureBox pictureBox4;
        private Bunifu.UI.WinForms.BunifuLabel devicePer4;
        private Bunifu.UI.WinForms.BunifuLabel deviceName4;
    }
}

