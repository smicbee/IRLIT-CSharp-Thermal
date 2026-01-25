using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThermalCamLib.LockIn;

namespace ThermalViewer
{
    public static class GPIOCOMController
    {

        public static SerialPort comPort;
        public static int GPIOPin = 0;

        public static int[] GetAvailableGPIOPins()
        {
            comPort.WriteLine("PINS");
            string[] resp = comPort.ReadLine().Split(',');

            List<int> pins = new List<int>();

            for (int i = 2; i < resp.Length; i++)
            {
                pins.Add(int.Parse(resp[i]));
            }

            return pins.ToArray();
        }
        public static string[] GetAvailableCOMPorts()
        {
            return SerialPort.GetPortNames();
        }


        static void AutoConnect()
        {
            ConnectCOM("Auto");
        }

        public static bool ConnectCOM(string name)
        {
            if (name == "Auto")
            {
                foreach(string s in SerialPort.GetPortNames())
                {

                    comPort = new SerialPort(s, 115200);
                    try
                    {
                        if (!comPort.IsOpen)
                        {
                            comPort.Open();
                        }
                        System.Threading.Thread.Sleep(50);
                        comPort.WriteTimeout = 100;
                        comPort.ReadTimeout = 100;
                 

                        if (checkIfGPIOController())
                        {
                            name = s;
                        }

                    }
                    catch { }
                    finally {
                        comPort.Close();
                    }

                }
            }

            if (!SerialPort.GetPortNames().Contains(name))
            {
                return false;
            }
            System.Threading.Thread.Sleep(50);
     
            comPort = new SerialPort(name.ToUpper(),115200);
            System.Threading.Thread.Sleep(100);
            if (!comPort.IsOpen) {
                try
                {
                    comPort.Open();
                }
                catch { }
            }

            comPort.WriteTimeout = 200;
            comPort.ReadTimeout = 200;

            if (checkIfGPIOController())
            {
                return true;
            }

            return false;
        }


        private static bool checkIfGPIOController()
        {
            if (!comPort.IsOpen)
            {
                comPort.Open();
            }

            comPort.WriteLine("HELLO");
            var answer = comPort.ReadLine();

            if (answer.Contains("GPIOController"))
            {
                return true;
            }

            return false;
        }


        public sealed class GPIOStimulusController : IStimulusController
        {
            public Task TurnOnAsync(CancellationToken ct)
            {
                if (!comPort.IsOpen)
                {
                    comPort.Open();
                }
                comPort.WriteLine($"SET,{GPIOPin.ToString()},1");
                Console.WriteLine("Stimulus ON");
                return Task.CompletedTask;
            }

            public Task TurnOffAsync(CancellationToken ct)
            {
                if (!comPort.IsOpen)
                {
                    comPort.Open();
                }

                comPort.WriteLine($"SET,{GPIOPin.ToString()},0");
                Console.WriteLine("Stimulus OFF");
                return Task.CompletedTask;
            }
        }
    }
}
