using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace bluetooth_turnon.Services
{
    public interface IConnectionService : IDisposable
    {
        event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;

        ConcurrentDictionary<ulong, BluetoothLEDevice> ConnectedBleDevices { get; }
        ConcurrentDictionary<ulong, GattSession> ConnectedBleSessions { get; }

        Task<DeviceOperationResult> ConnectDeviceWithResultAsync(ulong address, CancellationToken ct = default);
        Task<DeviceOperationResult> DisconnectDeviceWithResultAsync(ulong address, CancellationToken ct = default);
        Task<bool> ConnectDeviceAsync(ulong address, CancellationToken ct = default);
        Task<bool> DisconnectDeviceAsync(ulong address, CancellationToken ct = default);
        void CancelDeviceOperations(bool userRequestedAdapterOff = false);

        void ReleaseConnectedBleHandle(ulong address);
        void ReleaseAllConnectedBleHandles(string reason);
    }
}
