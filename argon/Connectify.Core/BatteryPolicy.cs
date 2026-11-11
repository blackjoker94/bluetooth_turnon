using System;

namespace bluetooth_turnon
{
    /// <summary>
    /// Outcome of a battery query attempt.
    /// </summary>
    public enum BatteryQueryOutcome
    {
        Success = 0,
        Unsupported = 1,
        TransientFailure = 2,
        Unknown = 3
    }

    /// <summary>
    /// Thread-safe cache entry tracking battery level, last-good value, query outcome,
    /// and backoff retry timings.
    /// </summary>
    public sealed class BatteryCacheEntry
    {
        private readonly object _lock = new object();

        public ulong Address { get; set; }
        public string DeviceName { get; set; }
        public int? BatteryPercent { get; set; }
        public int? LastGoodBattery { get; set; }
        public DateTime? LastGoodUtc { get; set; }
        public BatteryQueryOutcome Outcome { get; set; }
        public DateTime LastCheckedUtc { get; set; }
        public int FailureCount { get; set; }
        public DateTime NextAllowedRetryUtc { get; set; }
        public bool IsClassicOnly { get; set; }

        public int? GetEffectiveBattery()
        {
            lock (_lock)
            {
                if (Outcome == BatteryQueryOutcome.Unsupported) return null;
                if (BatteryPercent.HasValue) return BatteryPercent;
                if (LastGoodBattery.HasValue && LastGoodUtc.HasValue &&
                    (DateTime.UtcNow - LastGoodUtc.Value) < BatteryPolicy.BatteryLastGoodRetentionDuration)
                {
                    return LastGoodBattery;
                }
                return null;
            }
        }

        public bool ShouldRetry()
        {
            lock (_lock)
            {
                if (Outcome == BatteryQueryOutcome.Success)
                {
                    return DateTime.UtcNow >= NextAllowedRetryUtc;
                }
                if (Outcome == BatteryQueryOutcome.Unsupported)
                {
                    return DateTime.UtcNow >= NextAllowedRetryUtc;
                }
                if (Outcome == BatteryQueryOutcome.TransientFailure)
                {
                    if (FailureCount >= BatteryPolicy.MaxBatteryRetries)
                    {
                        return false;
                    }
                    return DateTime.UtcNow >= NextAllowedRetryUtc;
                }
                return false;
            }
        }

        public void RecordSuccess(int level, string deviceName)
        {
            lock (_lock)
            {
                DeviceName = deviceName;
                BatteryPercent = level;
                LastGoodBattery = level;
                LastGoodUtc = DateTime.UtcNow;
                Outcome = BatteryQueryOutcome.Success;
                LastCheckedUtc = DateTime.UtcNow;
                FailureCount = 0;
                NextAllowedRetryUtc = DateTime.UtcNow.AddMinutes(5);
            }
        }

        public void RecordUnsupported(string deviceName, bool isClassicOnly = false)
        {
            lock (_lock)
            {
                DeviceName = deviceName;
                BatteryPercent = null;
                Outcome = BatteryQueryOutcome.Unsupported;
                LastCheckedUtc = DateTime.UtcNow;
                IsClassicOnly = isClassicOnly;
                NextAllowedRetryUtc = DateTime.UtcNow.Add(BatteryPolicy.BatteryUnsupportedCacheDuration);
            }
        }

        public void RecordTransientFailure(string deviceName)
        {
            lock (_lock)
            {
                DeviceName = deviceName;
                Outcome = BatteryQueryOutcome.TransientFailure;
                LastCheckedUtc = DateTime.UtcNow;
                FailureCount++;
                NextAllowedRetryUtc = DateTime.UtcNow.Add(BatteryPolicy.GetTransientBackoff(FailureCount));

                if (LastGoodBattery.HasValue && LastGoodUtc.HasValue &&
                    (DateTime.UtcNow - LastGoodUtc.Value) < BatteryPolicy.BatteryLastGoodRetentionDuration)
                {
                    BatteryPercent = LastGoodBattery;
                }
                else
                {
                    BatteryPercent = null;
                }
            }
        }
    }

    public sealed class BatteryQueryResult
    {
        public BatteryQueryOutcome Outcome { get; set; }
        public int? BatteryLevel { get; set; }
        public long ElapsedMs { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Pure policy and configuration governing battery caching, retry backoffs, and budgets.
    /// </summary>
    public static class BatteryPolicy
    {
        /// <summary>
        /// Timeout budget for fast cached GATT reading (OS-level cache / BLE descriptor).
        /// Default: 2000ms (2 seconds).
        /// </summary>
        public static int BatteryCachedQueryBudgetMs { get; set; } = 2000;

        /// <summary>
        /// Timeout budget for slow uncached background GATT discovery (over-the-air with peripheral).
        /// Default: 10000ms (10 seconds) chosen from slowest measured results.
        /// </summary>
        public static int BatteryUncachedQueryBudgetMs { get; set; } = 10000;

        /// <summary>
        /// Maximum bounded retries while connected before capping at the periodic 5-minute interval.
        /// </summary>
        public static int MaxBatteryRetries { get; set; } = 5;

        /// <summary>
        /// Duration to cache devices confirmed to NOT support GATT Battery Service.
        /// Default: 24 hours (queried once and then left alone).
        /// </summary>
        public static TimeSpan BatteryUnsupportedCacheDuration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Duration to retain the last-good battery reading across transient packet drops or timeouts.
        /// Default: 30 minutes (preserves value instead of blanking out to N/A).
        /// </summary>
        public static TimeSpan BatteryLastGoodRetentionDuration { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Exponential backoff schedule for transient battery query failures.
        /// Schedule: 30s -> 2m -> 5m (capped at 5m).
        /// </summary>
        public static TimeSpan GetTransientBackoff(int failureCount)
        {
            switch (failureCount)
            {
                case 1: return TimeSpan.FromSeconds(30);
                case 2: return TimeSpan.FromMinutes(2);
                default: return TimeSpan.FromMinutes(5);
            }
        }

        /// <summary>
        /// Extracts Bluetooth MAC address (ulong) from Windows WinRT BLE DeviceInformation.Id string.
        /// </summary>
        public static ulong TryExtractAddressFromBleDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return 0;
            try
            {
                int idx = deviceId.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string sub = deviceId.Substring(idx + "BluetoothLE".Length);
                    if (sub.StartsWith("#BluetoothLE", StringComparison.OrdinalIgnoreCase))
                    {
                        sub = sub.Substring("#BluetoothLE".Length);
                    }

                    // Check for standard Windows WinRT format: <adapterMAC>-<deviceMAC>
                    int hyphenIdx = sub.IndexOf('-');
                    if (hyphenIdx > 0)
                    {
                        string afterHyphen = sub.Substring(hyphenIdx + 1);
                        int endAfter = afterHyphen.IndexOfAny(new[] { '_', '#' });
                        if (endAfter > 0) afterHyphen = afterHyphen.Substring(0, endAfter);
                        string cleanAfter = afterHyphen.Replace(":", "").Replace("-", "").Trim();
                        if (cleanAfter.Length == 12 && ulong.TryParse(cleanAfter, System.Globalization.NumberStyles.HexNumber, null, out ulong devAddr) && devAddr != 0)
                        {
                            return devAddr;
                        }
                    }

                    int endIdx = sub.IndexOfAny(new[] { '-', '_', '#' });
                    if (endIdx > 0) sub = sub.Substring(0, endIdx);
                    sub = sub.Replace(":", "").Replace("-", "").Trim();
                    if (ulong.TryParse(sub, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                    {
                        return addr;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}
