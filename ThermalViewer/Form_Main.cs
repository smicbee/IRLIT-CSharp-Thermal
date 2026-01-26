using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ThermalCamLib;
using ThermalCamLib.LockIn;
using ThermalCamLib.Temperature;
using System.Diagnostics;
using System.IO;
using ShellyNet;

namespace ThermalViewer
{
    public partial class Form_Main : Form
    {
        public Form_Main()
        {
            InitializeComponent();
        
        }

        static LinearTemperatureConverter temp = new LinearTemperatureConverter(rawMin: 8000, rawMax: 16000, tMinC: -20, tMaxC: 120);

        public ThermalCamera cam = new ThermalCamera(temp);

        static SerialPort GPIOController;

        static int _mode = 0;
        static int mode
        {
            get => _mode;
            set
            {
                _mode = value;
            }
        }
        DarkFieldCorrector darkCorr = new DarkFieldCorrector();


        static int lockinviewmode = -1;
        static LockInResult lockinResult;

        private void loadHotPixels()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\IRThermal";

            if (File.Exists(appdata + "\\hotpixels.json"))
            {
                var hotpixelsstr = File.ReadAllText(appdata + "\\hotpixels.json");
                cam.Hotpixels = Newtonsoft.Json.JsonConvert.DeserializeObject<List<int>>(hotpixelsstr);
            }
        }

        public Dictionary<string, string> Settings = new Dictionary<string, string>();

        public void writeSettings()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\IRThermal";
       
            if (!Directory.Exists(appdata))
            {
                Directory.CreateDirectory(appdata);
            }

            File.WriteAllText(appdata + "\\settings.json", Newtonsoft.Json.JsonConvert.SerializeObject(Settings));

        }
        private void loadSettings()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\IRThermal";

            if (File.Exists(appdata + "\\settings.json"))
            {
                var settingsstr = File.ReadAllText(appdata + "\\settings.json");
                Settings = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string,string>>(settingsstr);
            }

            if (Settings.ContainsKey("IODevice"))
            {
                IODevice = int.Parse(Settings["IODevice"]);
            }
            else { IODevice = 0; }


            if (Settings.ContainsKey("COM"))
            {
                if (GPIOCOMController.ConnectCOM(Settings["COM"]))
                {
                    //OK
                }
                else
                {
                    GPIOCOMController.ConnectCOM("Auto");
                }
            }
            else
            {
                GPIOCOMController.ConnectCOM("Auto");
            }

            if (Settings.ContainsKey("GPIO"))
            {
                GPIOCOMController.GPIOPin = int.Parse(Settings["GPIO"]);
            }


            if (IODevice == 0)
            {
                //GPIOController


            }else if (IODevice == 1){
                //Shelly
            }

        }

        public  int IODevice = 0;

        private async void Form1_Load(object sender, EventArgs e)
        {
            loadSettings();
            button2.Paint += ButtonLockIn_Paint;
            InitColorMapCombo();
            loadHotPixels();

            this.DoubleBuffered = true;
            try { 
                cam.Open("T2S+");          // optionaler Name-Filter          
            }
            catch
            {
                MessageBox.Show("Error opening the Camera!");
                return;
            }


            doDarkCorrection();
            
            
            startLiveView();
          

        }

        private void InitColorMapCombo()
        {
            comboBoxColorMap.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxColorMap.DataSource = Enum.GetValues(typeof(ThermalFrame.ColorMapType));
            comboBoxColorMap.SelectedItem = ThermalFrame.ColorMapType.Turbo;

            _selectedMap = (ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem;

            comboBoxColorMap.SelectedIndexChanged += (s, e) =>
            {
                _selectedMap = (ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem; // ✅ UI thread
                RenderCurrentFrame(); // optional
            };
        }

        private async void doCurrentCorrection()
        {
            startLiveView();
            var light = await cam.CaptureSingleFrameAsync();
            darkCorr.SetDarkFrame(light, 4);
        }
        public async void doDarkCorrection()
        {
            button5.BackColor = Color.Yellow;
            button5.Update();

            startLiveView();
            var dark = await cam.CaptureDarkFrameAsync();
            darkCorr.SetDarkFrame(dark, 4);

            button5.BackColor = Color.White;
            button5.Update();   
        }

        private double _lockInProgressPercent = 0.0;
        private bool _lockInStimulusOn = false;

        private void ButtonLockIn_Paint(object sender, PaintEventArgs e)
        {
            var btn = (Button)sender;
            var g = e.Graphics;

            g.Clear(btn.BackColor);

            // Fortschritt 0..1
            double p = Math.Max(0.0, Math.Min(1.0, _lockInProgressPercent / 100.0));
 
            Color fillColor = _lockInStimulusOn ? Color.Gold : Color.Orange;

            // Fortschritts-Rechteck
            int fillWidth = (int)(btn.Width * p);
            if (fillWidth > 0)
            {
                using (var brush = new SolidBrush(fillColor))
                {
                    g.FillRectangle(brush, 0, 0, fillWidth, btn.Height);
                }
            }

            // Button-Rahmen
            using (var pen = new Pen(Color.DarkGray))
            {
                g.DrawRectangle(pen, 0, 0, btn.Width - 1, btn.Height - 1);
            }

            // Text zentriert
            TextRenderer.DrawText(
                g,
                btn.Text,
                btn.Font,
                btn.ClientRectangle,
                Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }


        public async Task<LockInResult> startLockInRecord(double frequency, TimeSpan duration, double integrationtime, CancellationToken ct)
        {
            
            stopLiveView();
      
            cam.Start();
            var runner = new LockInMeasurementRunner(cam);

            IStimulusController stimulus = null;
 
            if (IODevice == 0)
            {
                stimulus = new GPIOCOMController.GPIOStimulusController();
            }
            else if (IODevice == 1)
            {
                ShellyClient client = new ShellyClient();

                if (!Settings.ContainsKey("ShellyIP")){
                    MessageBox.Show("No ShellyIP defined. Please go to Settings and assign a Shelly first!");
                    return null;
                }
                var device = await client.GetDeviceByIpAsync(Settings["ShellyIP"]);
                stimulus = new ShellyStimulus.ShellyStimulusController(device);
            }

            Func<ThermalFrame, ThermalFrame> preprocess = f => darkCorr.Apply(f);
            Func<ushort, double> xsel = raw => raw;

            // Progress läuft automatisch im UI thread (weil hier erstellt)
            var prog = new Progress<ThermalCamLib.LockIn.LockInProgress>(p =>
            {
                _lockInProgressPercent = p.Percent;
                _lockInStimulusOn = p.StimulusOn;
                button2.Invalidate(); // triggert Repaint
            });


            var res = await runner.RunAsync(
                stimulus: stimulus,
                frequencyHz: frequency,
                duration: duration,
                metaRows: 4,
                dutyCycle: 0.5,
                settleBeforeStart: TimeSpan.FromSeconds(1),
                framePreprocess: preprocess,
                xSelector: xsel,
                progress: prog,
                progressIntervalMs: 100,
                ct: ct
            );

            _lockInProgressPercent = 0;
            button2.Invalidate();
            return res;
        }



        public sealed class ConsoleStimulusController : IStimulusController
        { 
            public Task TurnOnAsync(CancellationToken ct)
            {          
                Console.WriteLine("Stimulus ON");
                return Task.CompletedTask;
            }

            public Task TurnOffAsync(CancellationToken ct)
            {
                Console.WriteLine("Stimulus OFF");
                return Task.CompletedTask;
            }
        }




        private void hScrollBar1_Scroll(object sender, ScrollEventArgs e)
        {

            updateLockinResultView();


        }

        private void updateLockinResultView()
        {
            stopLiveView();

            double degree = hScrollBar1.Value;

            label1.Text = degree.ToString() + "° Phase";

            if (lockinResult != null)
            {
                try
                {
                    if (!_lockInSeriesMin.HasValue || !_lockInSeriesMax.HasValue)
                    {
                        var r = lockinResult.Accumulator.GetSeriesSignalRange();
                        _lockInSeriesMin = r.Min;
                        _lockInSeriesMax = r.Max;
                    }

                    double mn = _lockInSeriesMin.Value;
                    double mx = _lockInSeriesMax.Value;

                    double offset = -mn;
                    double scale = 65535.0 / (mx - mn);

                    var frame = lockinResult.Accumulator.GetFrameAtAngle(
                        degree,
                        useMean: true,
                        offset: offset,
                        scale: scale);

                    pictureBox1.Image = frame.ToColorMappedImage(
                        (ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem,
                        autoContrast: false,
                        min: 0,
                        max: 65535);

                    _lastFrame = frame;
                }
                catch { }
            }
        }
        private ThermalFrame.ColorMapType _selectedMap = ThermalFrame.ColorMapType.Iron;
        private ThermalFrame _lastFrame;
        private readonly object _frameLock = new object();
        private double? _lockInSeriesMin;
        private double? _lockInSeriesMax;

        public void startLiveView()
        {
            mode = 0;
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            button1.BackColor = Color.Yellow;
            button2.BackColor = Color.White;
            button1.Update();
            button2.Update();
            cam.Start();

            cam.FrameReceived -= Cam_FrameReceived;
            cam.FrameReceived += Cam_FrameReceived;
        }


        private void Cam_FrameReceived(object sender, ThermalFrame f)
        {
            var corrected = darkCorr.Apply(f);

            lock (_frameLock)
                _lastFrame = corrected;
            
 
            // UI-thread rendern
            if (IsHandleCreated)
                BeginInvoke(new Action(RenderCurrentFrame));
        }

        private void RenderCurrentFrame()
        {
            ThermalFrame frame;
            lock (_frameLock)
                frame = _lastFrame;

            if (frame == null) return;

            var score = frame.focusScore;
            if (score < 10000) { 
            if (score > progressBar1.Maximum)
            {
                progressBar1.Maximum = Convert.ToInt16(score) + 100;
            }
            progressBar1.Value = Convert.ToInt16(score);
            }

            var map = _selectedMap; // kein ComboBox Zugriff nötig

            // Auto-Contrast ist in ToColorMappedImage eingebaut
            var bmp = frame.ToColorMappedImage(map, autoContrast: true);

            var old = pictureBox1.Image;
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox1.Image = bmp;
            old?.Dispose();
        }


        private void stopLiveView()
        {
            cam.Stop();
            mode = -1;
            button1.BackColor = Color.White;
            button2.BackColor = Color.White;
        }



        private static double ParseDoubleAnyDecimal(string text)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            return double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }


        private async void button1_Click(object sender, EventArgs e)
        {
            if (mode != 0)
            {
                startLiveView();
            }
            else
            {
                stopLiveView();
            }
        }

        private CancellationTokenSource _lockInCts;
        private Task _lockInTask;
        LockInSettings lockInSettings = new LockInSettings();

        private async void button2_Click(object sender, EventArgs e)
        {
            // Wenn schon läuft -> abbrechen
            if (_lockInCts != null)
            {
                _lockInCts.Cancel();
                return;
            }

           lockInSettings.ShowDialog();
            if (lockInSettings.DialogResult != DialogResult.OK) return;

            double freq, durationMs, integrationtime;
            try
            {
                freq = ParseDoubleAnyDecimal(lockInSettings.textBox1.Text);
                durationMs = ParseDoubleAnyDecimal(lockInSettings.textBox2.Text);
                integrationtime = ParseDoubleAnyDecimal(lockInSettings.textBox3.Text);
            }
            catch
            {
                MessageBox.Show("Ungültige Eingaben.");
                return;
            }

            _lockInCts = new CancellationTokenSource();
            var ct = _lockInCts.Token;

            button2.Text = "Stop";
            button2.Enabled = true; // bleibt klickbar zum Abbrechen

            try
            {
                // Starten (und Task merken, falls du später awaiten willst)
                _lockInTask = Task.Run(async () =>
                {
                    // bewusst nicht im UI-Thread rechnen lassen
                    var res = await startLockInRecord(freq, TimeSpan.FromMilliseconds(durationMs), integrationtime, ct);
                    return res;
                });

                // Ergebnis zurück in UI
                var result = await ((Task<LockInResult>)_lockInTask);

                lockinResult = result;
                _lockInSeriesMin = null;
                _lockInSeriesMax = null;

                ConfigureLockInSlider();
                hScrollBar1.Enabled = true;
                button3.Enabled = true;
                button4.Enabled = true;
                button10.Enabled = true;
                updateLockinResultView();
            }
            catch (OperationCanceledException)
            {
                // Abbruch ist OK
                // optional: MessageBox.Show("Abgebrochen");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                _lockInCts.Dispose();
                _lockInCts = null;
                _lockInTask = null;

                button2.Text = "Lock-In";
                _lockInProgressPercent = 0;
                button2.Invalidate();
            }
        }


        private void button4_Click(object sender, EventArgs e)
        {
            stopLiveView();

            if (lockinResult == null)
            {
                return;
            }

            try
            {
                var phaseimg = lockinResult.Accumulator.GetPhaseFrame();
                _lastFrame = phaseimg;                
               showImage(phaseimg.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem));
            }
            catch
            {
                return;
            }

        }

        private void button3_Click(object sender, EventArgs e)
        {
            stopLiveView();
            
            if (lockinResult == null)
            {
                return;
            }

            var amp = lockinResult.Accumulator.GetAmplitudeFrame();
            _lastFrame = amp;
            showImage(amp.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem));
        }

        private void button5_Click(object sender, EventArgs e)
        {
            doDarkCorrection();
        }

        private void ConfigureLockInSlider()
        {
            hScrollBar1.Minimum = 0;
            hScrollBar1.Maximum = 180;
            hScrollBar1.LargeChange = 5;
            hScrollBar1.SmallChange = 1;
            hScrollBar1.Value = 0;
            label1.Text = "0°";
        }


        private void showImage(Image img)
        {
            pictureBox1.Image?.Dispose();
            pictureBox1.Image= img;
            pictureBox1.Invalidate();
        }

        private void button6_Click(object sender, EventArgs e)
        {
            doCurrentCorrection();
        }

        private async void button7_Click(object sender, EventArgs e)
        {
            try
            {
                button7.BackColor = Color.Yellow;
                button7.Enabled = false;
                stopLiveView();
                // z.B. 2 Sekunden integrieren
                var frame = await cam.AcquireSumAsync(
                    duration: TimeSpan.FromMilliseconds(Convert.ToDouble(numericUpDown1.Value)),
                    metaRows: 4,
                    preprocess: f => darkCorr.Apply(f),
                    normalizeToU16: false, // Mittelwert-Frame
                    ct: CancellationToken.None);

                _lastFrame = frame;
                // Anzeige mit Colormap + AutoContrast
                var bmp = frame.ToColorMappedImage(
                    (ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem,
                    autoContrast: true);

                var old = pictureBox1.Image;
                pictureBox1.Image = bmp;
                old?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                button7.Enabled = true;
            }
            button7.BackColor = Color.White;
        }

        private void button8_Click(object sender, EventArgs e)
        {
            var savefiledialog = new SaveFileDialog();
            savefiledialog.Filter = "PNG | .png";
            if (savefiledialog.ShowDialog() == DialogResult.OK) {
                if (exportWithColormap.Checked)
                {
                    _lastFrame.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem).Save(savefiledialog.FileName);
                }
                else
                {
                    _lastFrame.ToGray8Image().Save(savefiledialog.FileName);
                }

            }
        }

        private void button9_Click(object sender, EventArgs e)
        {
            string temp = System.IO.Path.GetTempPath() + "/thermalexport.png";
            if (exportWithColormap.Checked)
            {
                _lastFrame.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem).Save(temp);
            }
            else
            {
                _lastFrame.ToGray8Image().Save(temp);
            }

            Process p = new Process();
            p.StartInfo.FileName = "ifacet://" + temp;
            p.Start();
        }

        private void button10_Click(object sender, EventArgs e)
        {

            var ofd = new FolderBrowserDialog();
            if (ofd.ShowDialog() == DialogResult.OK) {
                var path = ofd.SelectedPath;
                var frames = lockinResult.Accumulator.GetAllAngleFrames();

                foreach (var key in frames.Keys)
                {
                    var frame = frames[key];
                    if (exportWithColormap.Checked)
                    {
                        frame.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem).Save(path + "\\Φ" + key + ".png");
                    }
                    else
                    {
                        frame.ToGray8Image().Save(path + "\\Φ" + key + ".png");
                    }
                }
                MessageBox.Show($"Export to {path} successful!", "OK");
            }
        }

        private void button11_Click(object sender, EventArgs e)
        {
            Form_Settings settings = new Form_Settings(this);
            settings.Show();
        }

    }

  
}
