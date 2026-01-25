using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShellyNet
{
    public sealed class ShellyClient : IShellyClient, IDisposable
    {
        private readonly HttpClient _http;
        private readonly ShellyDiscovery _discovery = new ShellyDiscovery();

        public ShellyClient(TimeSpan? timeout = null, NetworkCredential basicAuth = null)
        {
            var handler = new HttpClientHandler();

            if (basicAuth != null)
                handler.Credentials = basicAuth;

            _http = new HttpClient(handler);
            _http.Timeout = timeout ?? TimeSpan.FromSeconds(2);
        }

        public Task<IReadOnlyList<ShellyDevice>> DiscoverAsync(CancellationToken ct = default)
            => _discovery.DiscoverAsync(ct);

        public async Task<bool> SetSwitchAsync(ShellyDevice device, bool on, int switchId = 0, CancellationToken ct = default)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var baseUrl = GetBaseUrl(device);

            HttpResponseMessage resp;

            if (device.Generation == ShellyGeneration.Gen2)
            {
                // Gen2+: RPC
                // /rpc/Switch.Set?id=0&on=true
                var url = $"{baseUrl}/rpc/Switch.Set?id={switchId}&on={(on ? "true" : "false")}";
                resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            }
            else
            {
                // Gen1: REST
                // /relay/0?turn=on|off
                var url = $"{baseUrl}/relay/{switchId}?turn={(on ? "on" : "off")}";
                resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            }

            return resp.IsSuccessStatusCode;
        }

        public async Task<bool?> GetSwitchStateAsync(ShellyDevice device, int switchId = 0, CancellationToken ct = default)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var baseUrl = GetBaseUrl(device);

            try
            {
                if (device.Generation == ShellyGeneration.Gen2)
                {
                    // /rpc/Switch.GetStatus?id=0 -> JSON mit "output": true/false (je nach FW)
                    var url = $"{baseUrl}/rpc/Switch.GetStatus?id={switchId}";
                    var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                    var jo = JObject.Parse(json);

                    // Gen2 Feldname ist typischerweise "output"
                    if (jo.TryGetValue("output", out var outputToken))
                        return outputToken.Value<bool>();

                    // Fallback, falls andere Feldnamen:
                    if (jo.TryGetValue("ison", out var isOnToken))
                        return isOnToken.Value<bool>();

                    return null;
                }
                else
                {
                    // /relay/0 -> JSON mit "ison": true/false
                    var url = $"{baseUrl}/relay/{switchId}";
                    var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                    var jo = JObject.Parse(json);

                    if (jo.TryGetValue("ison", out var isOnToken))
                        return isOnToken.Value<bool>();

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Optional: Gerät verifizieren + Generation/Model/ID befüllen.
        /// Für Gen2 klappt i.d.R. GET /shelly (liefert Infos).
        /// Für Gen1 ist /shelly oft nicht da; dann kannst du z.B. /settings oder /status probieren.
        /// </summary>
        public async Task<bool> EnrichDeviceInfoAsync(ShellyDevice device, CancellationToken ct = default)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var baseUrl = GetBaseUrl(device);

            // Versuche zuerst Gen2-typisch:
            try
            {
                var json = await _http.GetStringAsync($"{baseUrl}/shelly").ConfigureAwait(false);
                var jo = JObject.Parse(json);

                device.DeviceId = jo.Value<string>("id") ?? device.DeviceId;
                device.Model = jo.Value<string>("model") ?? device.Model;

                // Gen-Erkennung: manche liefern "gen"
                var gen = jo.Value<int?>("gen");
                if (gen == 2) device.Generation = ShellyGeneration.Gen2;
                else if (gen == 1) device.Generation = ShellyGeneration.Gen1;

                return true;
            }
            catch
            {
                // Fallback: Gen1 endpoints
            }

            try
            {
                // Gen1: /settings enthält oft device info
                var json = await _http.GetStringAsync($"{baseUrl}/settings").ConfigureAwait(false);
                var jo = JObject.Parse(json);

                device.DeviceId = jo.SelectToken("device.hostname")?.Value<string>() ?? device.DeviceId;
                device.Model = jo.SelectToken("device.type")?.Value<string>() ?? device.Model;
                device.Generation = ShellyGeneration.Gen1;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetBaseUrl(ShellyDevice d)
        {
            // Port 80 weglassen wäre optional – wir lassen ihn drin, ist robust.
            return $"http://{d.IpAddress}:{d.Port}";
        }



        public async Task<ShellyDevice> GetDeviceByIpAsync(
            string ipAddress,
            int port = 80,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                throw new ArgumentException("IP darf nicht leer sein.", nameof(ipAddress));

            // minimale IP-Validierung (optional)
            if (!System.Net.IPAddress.TryParse(ipAddress, out _))
                throw new ArgumentException("Ungültige IP-Adresse.", nameof(ipAddress));

            var device = new ShellyDevice
            {
                Name = ipAddress,
                Id = ipAddress,
                IpAddress = ipAddress,
                Port = port,
                Generation = ShellyGeneration.Unknown
            };

            var ok = await EnrichDeviceInfoAsync(device, ct).ConfigureAwait(false);
            return ok ? device : null;
        }



        public void Dispose() => _http?.Dispose();
    }




}
