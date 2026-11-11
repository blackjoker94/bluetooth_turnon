using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace bluetooth_turnon.Services
{
    public interface IRadioService : IDisposable
    {
        bool IsRadioBounceInProgress { get; }
        bool UserRequestedAdapterOff { get; set; }
        Radio BluetoothRadio { get; }

        event EventHandler<AdapterStateChangedEventArgs> AdapterStateChanged;
        event EventHandler<DeviceOperationProgressEventArgs> OperationProgressChanged;
        event Action<string> RadioTurnedOff;

        Task<Radio> GetBluetoothRadioAsync(CancellationToken ct = default, bool forceRefresh = false);
        Task<bool?> GetAdapterStateAsync(CancellationToken ct = default);
        Task<bool> SetAdapterStateAsync(bool enable, CancellationToken ct = default, bool isUserInitiated = true);
        Task<bool> ManualResetBluetoothAsync(CancellationToken ct = default);
    }
}
