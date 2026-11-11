using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon.Services
{
    public interface IConnectionStrategy
    {
        bool CanHandle(ConnectionRoute route);
        Task<DeviceOperationResult> ConnectAsync(ulong address, string name, CancellationToken ct);
        Task<DeviceOperationResult> DisconnectAsync(ulong address, string name, CancellationToken ct);
    }
}

