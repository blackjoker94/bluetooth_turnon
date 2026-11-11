using System.Threading;
using System.Threading.Tasks;

namespace bluetooth_turnon.Services
{
    public interface IConnectionStrategy
    {
        bool CanHandle(ConnectionRoute route);
        Task<bool> ConnectAsync(ulong address, string name, CancellationToken ct);
        Task<bool> DisconnectAsync(ulong address, string name, CancellationToken ct);
    }
}
