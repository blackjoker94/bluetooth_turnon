using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon.Services
{
    public interface IBatteryService
    {
        ConcurrentDictionary<ulong, BatteryCacheEntry> BatteryCache { get; }
        HashSet<ulong> InFlightBatteryQueries { get; }

        event Action<ulong, int?> BatteryUpdated;

        int? GetEffectiveBattery(ulong address);
        void HandleDeviceDisconnected(ulong address);
        void HandleAdapterTurnedOff();
        void ClearAll();
        void ScheduleDeferredBatteryQuery(ulong address, string deviceName, bool isClassicOnly = false, bool immediate = false);
        Task<BatteryQueryResult> QueryBatteryViaGattAsync(ulong bluetoothAddress, string deviceName, CancellationToken ct);
        int? QueryBatteryViaPnp(string bluetoothAddressHex);
    }
}
