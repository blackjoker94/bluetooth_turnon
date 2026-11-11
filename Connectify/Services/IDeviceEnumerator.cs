using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon.Services
{
    public interface IDeviceEnumerator : IDisposable
    {
        event EventHandler DeviceConnectionChanged;

        Task<IReadOnlyList<DeviceInfo>> GetPairedDevicesAsync(CancellationToken ct = default);
        Task<bool?> IsDeviceConnectedAsync(ulong address, CancellationToken ct = default);
        void StartDeviceWatcher();
        void StopDeviceWatcher();
        bool HasConnectedPeripherals(ulong currentTargetAddress, out string peripheralName, out BluetoothDeviceType peripheralType);
    }
}
