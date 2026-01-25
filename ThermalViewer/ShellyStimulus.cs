using ShellyNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThermalCamLib.LockIn;

namespace ThermalViewer
{
    internal class ShellyStimulus
    {

        public sealed class ShellyStimulusController : IStimulusController
        {
            ShellyDevice device;
            ShellyClient client = new ShellyClient();
            public ShellyStimulusController(ShellyDevice device) { this.device = device; }

            public Task TurnOnAsync(CancellationToken ct)
            {

                client.SetSwitchAsync(device, true);           
                Console.WriteLine("Stimulus ON");
                return Task.CompletedTask;
            }

            public Task TurnOffAsync(CancellationToken ct)
            {
                client.SetSwitchAsync(device, false);
                Console.WriteLine("Stimulus OFF");
                return Task.CompletedTask;
            }
        }
    }
}
