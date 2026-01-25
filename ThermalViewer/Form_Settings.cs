using ShellyNet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ThermalCamLib.LockIn;

namespace ThermalViewer
{
    public partial class Form_Settings : Form
    {
        Form1 mainForm;

        public Form_Settings(Form1 originalForm)
        {
            mainForm = originalForm;
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {

            mainForm.startLiveView();
            var darkFrame = await mainForm.cam.CaptureDarkFrameAsync();

      

            var maxIndex = (darkFrame.Height - 4) * darkFrame.Width;
            int[] croppedArray = new int[maxIndex];
      
            Array.Copy(darkFrame.RawU16, croppedArray, maxIndex);
            var darkMax = croppedArray.Max();

            var hotpixels = croppedArray.ToList().FindAll((q) => q > 0.99 * darkMax);
                        
            mainForm.cam.Hotpixels = hotpixels;

            mainForm.doDarkCorrection();

            UpdateHotPixelListView();

        }

        private void Form_Settings_Load(object sender, EventArgs e)
        {
            UpdateHotPixelListView();
            UpdateCOMPorts();
            UpdateShelly();
            setIOMode(mainForm.IODevice);
                comboBox1.SelectedIndex = mainForm.IODevice;

        }


        private void UpdateHotPixelListView()
        {

            if (mainForm.cam.Hotpixels == null) { return; }

            listView1.Items.Clear();
            foreach (var hp in mainForm.cam.Hotpixels)
            {
                listView1.Items.Add(hp.ToString());
            }

            var hotpixelstr = Newtonsoft.Json.JsonConvert.SerializeObject(mainForm.cam.Hotpixels);

            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\IRThermal";

            if (!Directory.Exists(appdata))
            {
                Directory.CreateDirectory(appdata);
            }

            File.WriteAllText(appdata + "\\hotpixels.json", hotpixelstr);

        }

        private void button2_Click(object sender, EventArgs e)
        {
            mainForm.cam.Hotpixels.Clear();
            mainForm.doDarkCorrection();
            UpdateHotPixelListView();
        }

        private void button3_Click(object sender, EventArgs e)
        {

            foreach (ListViewItem items in listView1.SelectedItems)
            {

                ushort index = Convert.ToUInt16(items.Text);
                mainForm.cam.Hotpixels.Remove(index);

            }

            mainForm.doDarkCorrection();
            UpdateHotPixelListView();

        }

        private void button4_Click(object sender, EventArgs e)
        {

            int x = Convert.ToInt32(textBox1.Text);
            int y = Convert.ToInt32(textBox2.Text);

            int index = y * mainForm.cam.Height + x;

            mainForm.cam.Hotpixels.Add(index);
            mainForm.doDarkCorrection();
            UpdateHotPixelListView();

        }

        private void button5_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox2.Text == "") { return; }

            if (!GPIOCOMController.ConnectCOM(comboBox2.Text))
            {
                MessageBox.Show("Could not connect to GPIOController at " + comboBox2.Text);
            }
            else
            {               
                var pins = GPIOCOMController.GetAvailableGPIOPins();
                comboBox3.Items.Clear();
                foreach(var pin in pins)
                {
                    comboBox3.Items.Add(pin.ToString());
                }

                comboBox3.Text = GPIOCOMController.GPIOPin.ToString();
            }

            mainForm.Settings["COM"] = comboBox2.Text;
            mainForm.writeSettings();
        }

        private void setIOMode(int mode)
        {
            label7.Visible = false;
            label6.Visible = false;
            comboBox2.Visible = false;
            comboBox3.Visible = false;
            textBox3.Visible = false;
            label8.Visible = false;

            if (mode == 0) { comboBox1.Text = "GPIOController ESP32"; }
            if (mode == 1) { comboBox1.Text = "Shelly"; }

            if (comboBox1.Text.Contains("GPIOController"))
            {
                label6.Visible = true;
                label7.Visible = true;
                comboBox2.Visible = true;
                comboBox3.Visible = true;
                UpdateCOMPorts();

            }

            if (comboBox1.Text.Contains("Shelly"))
            {
                textBox3.Visible = true;
                label8.Visible = true;

            }

            mainForm.IODevice = mode;
            mainForm.Settings["IODevice"] = mode.ToString();
            mainForm.writeSettings();
        }



        private void UpdateShelly()
        {
            if (mainForm.Settings.ContainsKey("ShellyIP"))
            {
                textBox3.Text = mainForm.Settings["ShellyIP"];
            }
        }
        private void UpdateCOMPorts()
        {
            comboBox2.Items.Clear();
            comboBox2.Items.Add("Auto");

            var ports = GPIOCOMController.GetAvailableCOMPorts();
            foreach (var port in ports)
            {
                comboBox2.Items.Add(port);
            }

            comboBox3.Text = GPIOCOMController.GPIOPin.ToString();
 
        }


        private void textBox3_TextChanged(object sender, EventArgs e)
        {
            
        }

        private void comboBox1_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            setIOMode(comboBox1.SelectedIndex);
        }

        private void textBox3_TextChanged_1(object sender, EventArgs e)
        {
            string ipaddress = textBox3.Text;
            IPAddress ip;
            bool ValidateIP = IPAddress.TryParse(ipaddress, out ip);
            if (ValidateIP)
                mainForm.Settings["ShellyIP"] = textBox3.Text;
                mainForm.writeSettings();
        }

        private async void button6_Click(object sender, EventArgs e)
        {
            IStimulusController stimulus = null;
        if (mainForm.IODevice == 0)
            {
                stimulus = new GPIOCOMController.GPIOStimulusController();
            }else if (mainForm.IODevice == 1)
            {
                var shellycontroller = new ShellyClient();
                var device = await shellycontroller.GetDeviceByIpAsync(mainForm.Settings["ShellyIP"]);
                stimulus = new ShellyStimulus.ShellyStimulusController(device);
                       
            }

        if (debugStimulus)
            {
                await stimulus.TurnOffAsync(_Cts.Token);
            }
            else
            {
                await stimulus.TurnOnAsync(_Cts.Token);
            }

            debugStimulus = !debugStimulus;

            if (debugStimulus)
            {

                button6.BackColor = Color.Yellow;            
                button6.Text = "Turn Stimulus Off";
            }
            else { button6.Text = "Turn Stimulus On"; button6.BackColor = Color.White; };

        }

        private CancellationTokenSource _Cts = new CancellationTokenSource();
        bool debugStimulus = false;
        bool debugShutter = false;

        private void comboBox3_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            GPIOCOMController.GPIOPin = int.Parse(comboBox3.Text);
            mainForm.Settings["GPIO"] = GPIOCOMController.GPIOPin.ToString();
            mainForm.writeSettings();
        }

        private void button5_Click_1(object sender, EventArgs e)
        {
            mainForm.cam.CloseShutter(1000);

        }
    }
}
    

