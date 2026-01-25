using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShellyNet
{
    public interface IShellyClient
    {
        Task<IReadOnlyList<ShellyDevice>> DiscoverAsync(
            CancellationToken ct = default);

        Task<bool> SetSwitchAsync(
            ShellyDevice device,
            bool on,
            int switchId = 0,
            CancellationToken ct = default);

        Task<bool?> GetSwitchStateAsync(
            ShellyDevice device,
            int switchId = 0,
            CancellationToken ct = default);
    }
}
