using System;
using System.Collections.Generic;

namespace bluetooth_turnon
{
    public enum DeviceTransportCapability
    {
        Unknown,
        ClassicCapable,
        BleOnly
    }

    public enum ConnectionRoute
    {
        ClassicWithAudio,
        ClassicWithHid,
        BleDirect
    }

    /// <summary>
    /// Standard Bluetooth SIG Service UUIDs used for profile and capability routing.
    /// Pure constants without dependency on 32feet or WinRT.
    /// </summary>
    public static class BluetoothServiceGuids
    {
        public static readonly Guid AudioSink = new Guid("0000110b-0000-1000-8000-00805f9b34fb");
        public static readonly Guid Handsfree = new Guid("0000111e-0000-1000-8000-00805f9b34fb");
        public static readonly Guid Headset = new Guid("00001108-0000-1000-8000-00805f9b34fb");
        public static readonly Guid HumanInterfaceDevice = new Guid("00001124-0000-1000-8000-00805f9b34fb");
        public static readonly Guid AudioSource = new Guid("0000110a-0000-1000-8000-00805f9b34fb");
        public static readonly Guid AdvancedAudioDistribution = new Guid("0000110d-0000-1000-8000-00805f9b34fb");
        public static readonly Guid GenericAudio = new Guid("00001203-0000-1000-8000-00805f9b34fb");
    }

    /// <summary>
    /// Pure routing policy governing Bluetooth transport capability detection and connection path selection.
    /// </summary>
    public static class ConnectRoutingPolicy
    {
        /// <summary>
        /// Three-state transport capability classification for Bluetooth devices.
        /// Classic-capable: Audio or HID profiles are present.
        /// BLE-only: Requires positive evidence (BLE paired, successful read with no classic profiles, not Classic CoD).
        /// Unknown: Empty or exception.
        /// Pure function for unit-testing without hardware.
        /// </summary>
        public static DeviceTransportCapability ClassifyTransportCapability(
            bool isBlePaired,
            bool installedServicesReadSuccess,
            IReadOnlyCollection<Guid> installedServices,
            int codMajor)
        {
            // Empty or exception means Unknown
            if (!installedServicesReadSuccess || installedServices == null)
            {
                return DeviceTransportCapability.Unknown;
            }

            bool hasClassicAudio = false;
            bool hasClassicHid = false;

            foreach (var profile in installedServices)
            {
                if (profile == BluetoothServiceGuids.AudioSink ||
                    profile == BluetoothServiceGuids.Handsfree ||
                    profile == BluetoothServiceGuids.Headset ||
                    profile == BluetoothServiceGuids.AudioSource ||
                    profile == BluetoothServiceGuids.AdvancedAudioDistribution)
                {
                    hasClassicAudio = true;
                }
                else if (profile == BluetoothServiceGuids.HumanInterfaceDevice)
                {
                    hasClassicHid = true;
                }
            }

            // 1. Classic-capable: Audio or HID profiles are present
            if (hasClassicAudio || hasClassicHid)
            {
                return DeviceTransportCapability.ClassicCapable;
            }

            // 2. Empty installed services: if read succeeded but count is 0,
            // if CoD indicates Classic Audio (0x04) or Peripheral (0x05),
            // empty profiles may just mean profiles are disabled (SetServiceState(false)) or unpopulated.
            // Therefore, do NOT declare it BleOnly.
            if (installedServices.Count == 0)
            {
                if (codMajor == 0x04 || codMajor == 0x05)
                {
                    return DeviceTransportCapability.Unknown;
                }
            }

            // 3. BLE-only requires positive evidence:
            // BLE paired, successful read with NO classic profiles, and not a Classic CoD.
            if (isBlePaired && installedServices.Count == 0 && codMajor != 0x04 && codMajor != 0x05)
            {
                return DeviceTransportCapability.BleOnly;
            }

            return DeviceTransportCapability.Unknown;
        }

        /// <summary>
        /// Maps transport capability, CoD major class, and peripheral flags to a connection route.
        /// Pure function with a truth table.
        /// </summary>
        public static ConnectionRoute DetermineConnectionRoute(
            DeviceTransportCapability capability,
            int codMajor,
            bool isPeripheral)
        {
            // 1. BLE-only routes directly to BLE
            if (capability == DeviceTransportCapability.BleOnly)
            {
                return ConnectionRoute.BleDirect;
            }

            // 2. Classic-capable: route to Classic HID if peripheral, else Classic Audio
            if (capability == DeviceTransportCapability.ClassicCapable)
            {
                if (codMajor == 0x05 || isPeripheral)
                {
                    return ConnectionRoute.ClassicWithHid;
                }
                return ConnectionRoute.ClassicWithAudio;
            }

            // 3. Unknown: Fall back to Class of Device (audio or peripheral major class),
            // then to the classic path with the existing fallback profile list.
            if (codMajor == 0x05 || isPeripheral)
            {
                return ConnectionRoute.ClassicWithHid;
            }

            // Audio (0x04) or unclassified -> ClassicWithAudio fallback
            return ConnectionRoute.ClassicWithAudio;
        }
    }
}
