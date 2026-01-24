using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
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

namespace ThermalViewer
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        
        }

        static LinearTemperatureConverter temp = new LinearTemperatureConverter(rawMin: 8000, rawMax: 16000, tMinC: -20, tMaxC: 120);

        static ThermalCamera cam = new ThermalCamera(temp);

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

        private async void Form1_Load(object sender, EventArgs e)
        {
                InitColorMapCombo();
                this.DoubleBuffered = true;
                cam.Open();          // optionaler Name-Filter
                cam.EnableRawMode();     // ZOOM=0x8004 :contentReference[oaicite:11]{index=11}
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

        private async void doDarkCorrection()
        {
            var dark = await cam.CaptureDarkFrameAsync();
            darkCorr.SetDarkFrame(dark, 4);
        }
        public async Task<LockInResult> startLockInRecord(double frequency, TimeSpan duration)
        {
            stopLiveView();
            button2.BackColor = Color.Yellow;

            cam.Start();
            var runner = new LockInMeasurementRunner(cam);
            var stimulus = new ConsoleStimulusController();

            // optional: preprocess (darkfield)
            Func<ThermalFrame, ThermalFrame> preprocess = f => darkCorr.Apply(f);

            // xSelector: Raw verwenden (oder raw -> °C wenn dein converter performant ist)
            Func<ushort, double> xsel = raw => raw;

            var res = await runner.RunAsync(
                stimulus: stimulus,
                frequencyHz: frequency,
                duration: duration,
                metaRows: 4,
                dutyCycle: 0.5,
                settleBeforeStart: TimeSpan.FromSeconds(1),
                framePreprocess: preprocess,
                xSelector: xsel,
                ct: CancellationToken.None
            );

            button2.BackColor = Color.White;
                           
            return res;
        }

        static SerialPort comIO = new SerialPort("COM16");

        public sealed class ConsoleStimulusController : IStimulusController
        { 
            public Task TurnOnAsync(CancellationToken ct)
            {
                if (!comIO.IsOpen)
                {
                    comIO.Open();
                }
                comIO.WriteLine("SET,0,1");
                Console.WriteLine("Stimulus ON");
                return Task.CompletedTask;
            }

            public Task TurnOffAsync(CancellationToken ct)
            {
                if (!comIO.IsOpen)
                {
                    comIO.Open();
                }

                comIO.WriteLine("SET,0,0");
                Console.WriteLine("Stimulus OFF");
                return Task.CompletedTask;
            }
        }


        public async Task DoLockInAsync(ThermalCamera cam, DarkFieldCorrector darkCorr)
        {
            var runner = new LockInMeasurementRunner(cam);
            var stimulus = new ConsoleStimulusController();

            // optional: preprocess (darkfield)
            Func<ThermalFrame, ThermalFrame> preprocess = f => darkCorr.Apply(f);

            // xSelector: Raw verwenden (oder raw -> °C wenn dein converter performant ist)
            Func<ushort, double> xsel = raw => raw;

            var res = await runner.RunAsync(
                stimulus: stimulus,
                frequencyHz: 1.0,
                duration: TimeSpan.FromSeconds(10),
                metaRows: 4,
                dutyCycle: 0.5,
                settleBeforeStart: TimeSpan.FromSeconds(1),
                framePreprocess: preprocess,
                xSelector: xsel,
                ct: CancellationToken.None
            );

            // Auswertung:
            var amp = res.Accumulator.GetAmplitude(normalize: true);
            var phase = res.Accumulator.GetPhase();

            // -> daraus kannst du jetzt wieder ein 8-bit Bild machen (amp auto-contrast etc.)
        }

        private void hScrollBar1_Scroll(object sender, ScrollEventArgs e)
        {

            updateLockinResultView();


        }

        private void updateLockinResultView()
        {
            stopLiveView();

            double degree = hScrollBar1.Value;

            label1.Text = degree.ToString() + "°";

            if (lockinResult != null)
            {
                try
                {
                    pictureBox1.BackgroundImage = lockinResult.Accumulator.GetFrameAtAngle_OnMinusOffShifted(degree).ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem);
                    pictureBox1.Invalidate();
                }
                catch { }
            }
        }
        private ThermalFrame.ColorMapType _selectedMap = ThermalFrame.ColorMapType.Iron;
        private ThermalFrame _lastFrame;
        private readonly object _frameLock = new object();

        private void startLiveView()
        {
            mode = 0;
            pictureBox1.BackgroundImageLayout = ImageLayout.Zoom;
            button1.BackColor = Color.Yellow;
            button2.BackColor = Color.White;

            cam.Start();

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

        private async void button2_Click(object sender, EventArgs e)
        {
            stopLiveView();


            LockInSettings settings = new LockInSettings();
            settings.ShowDialog();
            if (settings.DialogResult == DialogResult.OK)
            {
                try
                {
                    double freq = Convert.ToDouble(settings.textBox1.Text);
                    double duration = Convert.ToDouble(settings.textBox2.Text);

                    var res =await startLockInRecord(freq, TimeSpan.FromMilliseconds(duration));

                    lockinResult = res;

                    ConfigureLockInSlider();
                    hScrollBar1.Enabled = true;
                    button3.Enabled = true;
                    button4.Enabled = true;
                    updateLockinResultView();


                }
                catch { }
            }

        }

        private void button4_Click(object sender, EventArgs e)
        {
            if (lockinResult == null)
            {
                return;
            }

            try
            {
                showImage(lockinResult.Accumulator.GetPhaseFrame(maskLowAmplitude: true, amplitudeThreshold: 50).ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem));
            }
            catch
            {
                return;
            }

        }

        private void button3_Click(object sender, EventArgs e)
        {
            if (lockinResult == null)
            {
                return;
            }

            var amp = lockinResult.Accumulator.GetAmplitudeFrame();
            showImage(amp.ToColorMappedImage((ThermalFrame.ColorMapType)comboBoxColorMap.SelectedItem));
        }

        private void button5_Click(object sender, EventArgs e)
        {
            doDarkCorrection();
        }

        private void ConfigureLockInSlider()
        {
            hScrollBar1.Minimum = 0;
            hScrollBar1.Maximum = 360;
            hScrollBar1.LargeChange = 1;
            hScrollBar1.SmallChange = 1;
            hScrollBar1.Value = 0;
            label1.Text = "0°";
        }


        private void showImage(Image img)
        {
            pictureBox1.BackgroundImage?.Dispose();
            pictureBox1.BackgroundImage = img;
        }

    }

  
}
