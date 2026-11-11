using System;
using System.Drawing;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    /// <summary>
    /// Holds the control references for an individual device card in the list.
    /// Preserved across refreshes to eliminate handle churn and flicker.
    /// </summary>
    public sealed class DeviceCardView
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
    /// Factory for creating and updating device card views with consistent styling and double buffering.
    /// </summary>
    public static class DeviceCardFactory
    {
        public static DeviceCardView CreateCardView(
            DeviceViewModel vm,
            DeviceInfo boundDevice,
            int y,
            int cardWidth,
            int cardHeight,
            int batteryColX,
            string assetsDir,
            Font nameFont,
            Font typeFont,
            Font batteryFont,
            Func<string, string, Image> iconLoader,
            Action<DeviceCardView> onCardClick)
        {
            var view = new DeviceCardView
            {
                Address = vm.Address,
                BoundDevice = boundDevice,
                BaseBgColor = vm.BaseBgColor,
                HoverBgColor = vm.HoverBgColor,
                AccentColor = vm.AccentColor
            };

            var card = new Panel
            {
                Tag = vm.Address,
                BackColor = vm.BaseBgColor,
                Location = new Point(0, y),
                Size = new Size(cardWidth, cardHeight),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            SetDoubleBuffered(card);
            view.Card = card;

            var iconPanel = new Panel
            {
                BackColor = vm.BaseBgColor,
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
            Image iconImg = iconLoader(assetsDir, vm.DeviceIconFileName);
            if (iconImg != null) iconBox.Image = iconImg;
            iconPanel.Controls.Add(iconBox);
            view.IconBox = iconBox;

            var nameLabel = new Label
            {
                Text = vm.Name,
                ForeColor = vm.NameColor,
                Font = nameFont,
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

            var typeLabel = new Label
            {
                Name = "typeLabel",
                Text = vm.TypeLabelText,
                ForeColor = vm.TypeLabelColor,
                Font = typeFont,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                BackColor = Color.Transparent,
                Location = new Point(74, 38),
                Size = new Size(batteryColX - 74 - 6, 20),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            view.TypeLabel = typeLabel;

            var batteryBox = new PictureBox
            {
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(28, 28),
                Location = new Point(batteryColX, (cardHeight - 28) / 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            if (vm.IsConnected && vm.BatteryIconFileName != null)
            {
                Image battImg = iconLoader(assetsDir, vm.BatteryIconFileName);
                if (battImg != null) batteryBox.Image = battImg;
            }
            view.BatteryBox = batteryBox;

            var batteryLabel = new Label
            {
                Text = vm.IsConnected ? vm.BatteryText : "",
                ForeColor = vm.BatteryColor,
                Font = batteryFont,
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
                onCardClick?.Invoke(view);
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

        public static void UpdateCardView(
            DeviceCardView view,
            DeviceViewModel vm,
            DeviceInfo boundDevice,
            int targetY,
            int cardWidth,
            int cardHeight,
            int batteryColX,
            string assetsDir,
            Func<string, string, Image> iconLoader)
        {
            // Update background color based on hover state
            Point cursorInCard = view.Card.PointToClient(Cursor.Position);
            view.IsHovered = view.Card.ClientRectangle.Contains(cursorInCard);
            Color currentBg = view.IsHovered ? vm.HoverBgColor : vm.BaseBgColor;

            bool needIconRepaint = (view.AccentColor != vm.AccentColor || view.IconPanel.BackColor != currentBg);

            view.BoundDevice = boundDevice;
            view.BaseBgColor = vm.BaseBgColor;
            view.HoverBgColor = vm.HoverBgColor;
            view.AccentColor = vm.AccentColor;

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

            if (view.Card.BackColor != currentBg) view.Card.BackColor = currentBg;
            if (view.IconPanel.BackColor != currentBg) view.IconPanel.BackColor = currentBg;

            // Name label
            if (view.NameLabel.Text != vm.Name) view.NameLabel.Text = vm.Name;
            if (view.NameLabel.ForeColor != vm.NameColor) view.NameLabel.ForeColor = vm.NameColor;

            // Type & In-flight status
            if (view.TypeLabel.Text != vm.TypeLabelText) view.TypeLabel.Text = vm.TypeLabelText;
            if (view.TypeLabel.ForeColor != vm.TypeLabelColor) view.TypeLabel.ForeColor = vm.TypeLabelColor;

            // Battery
            string expectedBatteryText = vm.IsConnected ? vm.BatteryText : "";
            if (view.BatteryLabel.Text != expectedBatteryText) view.BatteryLabel.Text = expectedBatteryText;
            if (view.BatteryLabel.ForeColor != vm.BatteryColor) view.BatteryLabel.ForeColor = vm.BatteryColor;

            string batteryIcon = vm.BatteryIconFileName;
            Image battImg = (vm.IsConnected && batteryIcon != null) ? iconLoader(assetsDir, batteryIcon) : null;
            if (view.BatteryBox.Image != battImg) view.BatteryBox.Image = battImg;

            // Icon
            string iconFile = vm.DeviceIconFileName;
            Image iconImg = iconLoader(assetsDir, iconFile);
            if (view.IconBox.Image != iconImg) view.IconBox.Image = iconImg;

            if (needIconRepaint)
            {
                view.IconPanel.Invalidate();
            }
        }

        public static void SetDoubleBuffered(Control c)
        {
            if (c == null) return;
            try
            {
                var prop = typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                prop?.SetValue(c, true, null);
            }
            catch { }
        }
    }
}
