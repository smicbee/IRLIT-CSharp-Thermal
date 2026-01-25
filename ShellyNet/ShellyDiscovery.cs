using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace ShellyNet
{
    public sealed class ShellyDiscovery
    {
        private static readonly string ShellyService = "_shelly._tcp.local.";
        private static readonly string HttpService = "_http._tcp.local.";

        public async Task<IReadOnlyList<ShellyDevice>> DiscoverAsync(CancellationToken ct = default)
        {
            // Zeroconf hat kein CancellationToken in ResolveAsync – wir können aber Timeout via Task.WhenAny lösen,
            // oder du lässt es simpel. Hier: simpel (in der Praxis ist es schnell im LAN).
            var services = new[] { ShellyService, HttpService };
            var results = await ZeroconfResolver.ResolveAsync(services).ConfigureAwait(false);

            var devices = new List<ShellyDevice>();

            foreach (var host in results)
            {
                // bevorzugt IPv4, falls vorhanden
                var ip = host.IPAddresses?.FirstOrDefault(x => x.Contains(".")) ?? host.IPAddresses?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(ip))
                    continue;

                // Gen2+ über _shelly._tcp
                if (host.Services.TryGetValue(ShellyService, out var shellySvc))
                {
                    devices.Add(new ShellyDevice
                    {
                        Name = host.DisplayName,
                        Id = host.Id,
                        IpAddress = ip,
                        Port = shellySvc.Port,
                        Generation = ShellyGeneration.Gen2
                    });
                    continue;
                }

                // Gen1-Kandidaten: _http._tcp + Name/Id beginnt mit shelly
                if (host.Services.TryGetValue(HttpService, out var httpSvc))
                {
                    bool looksLikeShelly =
                        (!string.IsNullOrWhiteSpace(host.DisplayName) &&
                         host.DisplayName.StartsWith("shelly", StringComparison.OrdinalIgnoreCase))
                        ||
                        (!string.IsNullOrWhiteSpace(host.Id) &&
                         host.Id.StartsWith("shelly", StringComparison.OrdinalIgnoreCase));

                    if (!looksLikeShelly)
                        continue;

                    devices.Add(new ShellyDevice
                    {
                        Name = host.DisplayName,
                        Id = host.Id,
                        IpAddress = ip,
                        Port = httpSvc.Port,
                        Generation = ShellyGeneration.Gen1
                    });
                }

            }

            // Dedupe nach IP (falls doppelt gefunden)
            return devices
                .GroupBy(d => d.IpAddress)
                .Select(g => g.First())
                .ToList();
        }

    }
}
