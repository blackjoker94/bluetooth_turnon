using System;
using System.Drawing;

namespace bluetooth_turnon
{
    /// <summary>
    /// View model representing the presentation state of a device card in the UI.
    /// Decouples raw hardware DeviceInfo and in-flight operation state from WinForms card controls.
    /// </summary>
    public sealed class DeviceViewModel
    {
        public ulong Address { get; }
        public string Name { get; }
        public BluetoothDeviceType DeviceType { get; }
        public bool IsConnected { get; }
        public int? BatteryPercent { get; }

        public Color BaseBgColor { get; }
        public Color HoverBgColor { get; }
        public Color AccentColor { get; }
        public Color NameColor { get; }
        public string TypeLabelText { get; }
        public Color TypeLabelColor { get; }
        public string BatteryText { get; }
        public Color BatteryColor { get; }
        public string BatteryIconFileName { get; }
        public string DeviceIconFileName { get; }

        public DeviceViewModel(
            DeviceInfo device,
            int index,
            string inFlightText,
            Color inFlightColor,
            Color baseBgColor,
            Color hoverBgColor,
            Color accentColor,
            Color nameColor,
            string typeLabelText,
            Color typeLabelColor,
            string batteryText,
            Color batteryColor,
            string batteryIconFileName,
            string deviceIconFileName)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            Address = device.Address;
            Name = device.Name ?? "Unknown Device";
            DeviceType = device.DeviceType;
            IsConnected = device.IsConnected;
            BatteryPercent = device.BatteryPercent;

            BaseBgColor = baseBgColor;
            HoverBgColor = hoverBgColor;
            AccentColor = accentColor;
            NameColor = nameColor;

            if (!string.IsNullOrEmpty(inFlightText))
            {
                TypeLabelText = inFlightText;
                TypeLabelColor = inFlightColor;
            }
            else
            {
                TypeLabelText = typeLabelText;
                TypeLabelColor = typeLabelColor;
            }

            BatteryText = batteryText;
            BatteryColor = batteryColor;
            BatteryIconFileName = batteryIconFileName;
            DeviceIconFileName = deviceIconFileName;
        }
    }
}
