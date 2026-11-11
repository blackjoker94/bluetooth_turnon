using System;
using System.Text.RegularExpressions;

namespace bluetooth_turnon
{
    /// <summary>
    /// Categorizes a connected Bluetooth device so the UI can show the right icon.
    /// </summary>
    public enum BluetoothDeviceType
    {
        Unknown = 0,
        Earbuds,      // Wireless earbuds — AirPods, Galaxy Buds, Nothing Ear, Soundcore
        Headphones,   // Over-ear / on-ear — Sony WH, Bose QC, SteelSeries
        Headset,      // Headset with mic (hands-free)
        Speaker,      // Bluetooth speaker — JBL, Sony, Anker
        Microphone,   // Standalone BT mic
        Mouse,        // Bluetooth mouse
        Keyboard,     // Bluetooth keyboard
        Controller,   // Game controller — PS5 DualSense, Xbox, 8BitDo
        Phone,        // Smartphone
        Smartwatch,   // Wearable / smartwatch
        Computer,     // PC, laptop, tablet
        AudioDevice,  // Generic audio device
    }

    /// <summary>
    /// Pure classification rules based on BLE Appearance, Classic Class of Device (CoD),
    /// and device name patterns.
    /// </summary>
    public static class DeviceClassifier
    {
        public static BluetoothDeviceType ClassifyByAppearance(ushort category, ushort subCategory)
        {
            switch (category)
            {
                case 0x03C0: // Human Interface Device
                    if (subCategory == 1) return BluetoothDeviceType.Keyboard;
                    if (subCategory == 2) return BluetoothDeviceType.Mouse;
                    if (subCategory == 3 || subCategory == 4) return BluetoothDeviceType.Controller;
                    return BluetoothDeviceType.Mouse;
                case 0x0040: // Phone
                    return BluetoothDeviceType.Phone;
                case 0x0080: // Computer
                    return BluetoothDeviceType.Computer;
                case 0x00C0: // Watch / Wearable
                    return BluetoothDeviceType.Smartwatch;
                case 0x0840: // Media Player / Audio (subcategories, Fix Issue #32)
                    switch (subCategory)
                    {
                        case 1: return BluetoothDeviceType.Earbuds;     // Wearable Audio / In-ear
                        case 2: return BluetoothDeviceType.Headphones;  // Over-ear headphones
                        case 3: return BluetoothDeviceType.Speaker;     // Loudspeaker
                        case 4: return BluetoothDeviceType.Speaker;     // Soundbar
                        default: return BluetoothDeviceType.AudioDevice;
                    }
                default:
                    return BluetoothDeviceType.Unknown;
            }
        }

        public static BluetoothDeviceType ClassifyByCod(int major, int minor)
        {
            switch (major)
            {
                case 0x04: // Audio / Video
                    switch (minor)
                    {
                        case 0x01: return BluetoothDeviceType.Earbuds;     // Wearable headset (earbuds / AirPod style)
                        case 0x02: return BluetoothDeviceType.Headset;     // Hands-free device
                        case 0x04: return BluetoothDeviceType.Microphone;  // Microphone
                        case 0x05: return BluetoothDeviceType.Speaker;     // Loudspeaker
                        case 0x06: return BluetoothDeviceType.Headphones;  // Headphones (over-ear)
                        case 0x07: return BluetoothDeviceType.AudioDevice; // Portable audio
                        case 0x08: return BluetoothDeviceType.AudioDevice; // Car audio
                        case 0x0A: return BluetoothDeviceType.AudioDevice; // HiFi audio
                        default:   return BluetoothDeviceType.AudioDevice;
                    }

                case 0x05: // Peripheral (mouse / keyboard / controller)
                    {
                        int inputKind = (minor >> 4) & 0x03; // bits 5-4
                        int subType   =  minor       & 0x0F; // bits 3-0
                        if (inputKind == 0x01) return BluetoothDeviceType.Keyboard;
                        if (inputKind == 0x02) return BluetoothDeviceType.Mouse;
                        if (subType == 0x01 || subType == 0x02)
                            return BluetoothDeviceType.Controller; // Joystick / Gamepad
                        return BluetoothDeviceType.Mouse; // generic peripheral
                    }

                case 0x02: return BluetoothDeviceType.Phone;
                case 0x01: return BluetoothDeviceType.Computer;
                case 0x07: return BluetoothDeviceType.Smartwatch; // Wearable
                case 0x08: return BluetoothDeviceType.Controller; // Toy (8BitDo etc. may use this)
                default:   return BluetoothDeviceType.Unknown;
            }
        }

        /// <summary>
        /// Checks whether a specific word or token exists in text matching word boundaries (\b).
        /// Prevents false positives where short substrings match unrelated models (e.g. "mp3" or "mk2").
        /// </summary>
        public static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
                return false;

            return Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Name-pattern fallback for devices whose CoD is Uncategorized or 0.
        /// Matches common brand/model keywords case-insensitively.
        /// Uses word boundaries for short tokens to prevent false positives.
        /// Evaluates phone before keyboard (prevents "Joanne's iPhone" -> Keyboard, Fix Issue #32).
        /// </summary>
        public static BluetoothDeviceType ClassifyByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BluetoothDeviceType.Unknown;

            string n = name.ToLowerInvariant();

            // ── Phones (evaluated early to avoid name collisions) ────────────
            if (n.Contains("iphone")   || n.Contains("galaxy s") || n.Contains("galaxy a") ||
                n.Contains("galaxy z") || n.Contains("pixel")    || n.Contains("oneplus")  ||
                n.Contains("redmi")    || n.Contains("poco")     || n.Contains("realme")   ||
                n.Contains("huawei")   || n.Contains("honor")    || n.Contains("xperia")   ||
                n.Contains("smartphone"))
                return BluetoothDeviceType.Phone;

            // ── Controllers ───────────────────────────────────────────────────
            if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("xbox") ||
                n.Contains("8bitdo")    || n.Contains("controller") || n.Contains("gamepad") ||
                n.Contains("joycon")    || n.Contains("pro con"))
                return BluetoothDeviceType.Controller;

            // ── Mouse ─────────────────────────────────────────────────────────
            if (n.Contains("mouse")   || n.Contains("mx master") || n.Contains("mx any") ||
                n.Contains("pro click")|| n.Contains("basilisk") || n.Contains("deathadder") ||
                n.Contains("pebble")  || n.Contains("trackball"))
                return BluetoothDeviceType.Mouse;

            // ── Keyboard ─────────────────────────────────────────────────────
            if (n.Contains("keyboard") || n.Contains("keychron") || n.Contains("mx keys") ||
                ContainsWord(n, "anne pro") || n.Contains("ducky")    ||
                ContainsWord(n, "k2")  || ContainsWord(n, "k4")   ||
                ContainsWord(n, "k8")  || ContainsWord(n, "k10"))
                return BluetoothDeviceType.Keyboard;

            // ── Over-ear headphones / headset (Pulse 3D is a headset, not a speaker)
            if (n.Contains("pulse 3d") || n.Contains("wh-")     || n.Contains("xm4")    ||
                n.Contains("xm5")      || n.Contains("qc45")    || n.Contains("qc35")   ||
                n.Contains("qc700")    || n.Contains("momentum")|| n.Contains("arctis") ||
                n.Contains("nova")     || n.Contains("void")    || n.Contains("cloud")  ||
                n.Contains("stealth")  || n.Contains("headphone") || n.Contains("headset"))
                return BluetoothDeviceType.Headphones;

            // ── Earbuds ──────────────────────────────────────────────────────
            if (n.Contains("airpod")   || n.Contains("galaxy bud") || n.Contains(" buds") ||
                n.Contains("freebuds") || n.Contains("nothing ear")|| n.Contains("liberty") ||
                n.Contains("soundcore")|| n.Contains("jabra")      || n.Contains("earbud") ||
                n.Contains("wf-")      || n.Contains("tws"))
                return BluetoothDeviceType.Earbuds;

            // ── Speakers (no bare pulse/charge/flip false-positives) ─────────
            if (n.Contains("speaker")     || n.Contains("soundbar")   || n.Contains("echo") ||
                n.Contains("homepod")     || n.Contains("boombox")    || n.Contains("ue boom") ||
                n.Contains("wonderboom")  || n.Contains("jbl flip")   || n.Contains("jbl charge") ||
                n.Contains("jbl pulse")   || n.Contains("charge 3")   || n.Contains("charge 4") ||
                n.Contains("charge 5")    || n.Contains("flip 4")     || n.Contains("flip 5") ||
                n.Contains("flip 6")      || n.Contains("xtreme"))
                return BluetoothDeviceType.Speaker;

            // ── Computers ────────────────────────────────────────────────────
            if (n.Contains("desktop") || n.Contains("laptop") || n.Contains("macbook") ||
                n.Contains("surface") || n.Contains("pc "))
                return BluetoothDeviceType.Computer;

            // ── Smartwatch ────────────────────────────────────────────────────
            if (n.Contains("watch")    || n.Contains("galaxy watch") || n.Contains("gear") ||
                n.Contains("mi band")  || n.Contains("smart band")   || n.Contains("fitbit") ||
                n.Contains("garmin")   || n.Contains("vivosmart")    || ContainsWord(n, "band"))
                return BluetoothDeviceType.Smartwatch;

            return BluetoothDeviceType.Unknown;
        }
    }
}
