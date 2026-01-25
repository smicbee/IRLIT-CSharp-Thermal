using DirectShowLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ThermalCamLib.Temperature;

namespace ThermalCamLib
{

    public sealed class ThermalCamera : IDisposable
    {
        // Bekannte "Zoom" Command-Patterns aus der Community:
        // - 0x8004: RAW-Datenstream anfordern (OpenCV cap.set(ZOOM, 0x8004)) :contentReference[oaicite:4]{index=4}
        // - 0x8000: Shutter schließen / FFC-Hold (durch wiederholtes Senden bleibt er zu) :contentReference[oaicite:5]{index=5}
        // - 0x0080: wird teils als "FFC trigger" erwähnt (modellabhängig) :contentReference[oaicite:6]{index=6}
        public const int ZOOM_CMD_RAW_ON = 0x8004;
        public const int ZOOM_CMD_SHUTTER_CLOSE = 0x8000;
        public const int ZOOM_CMD_FFC_TRIGGER = 0x0080;

        private readonly object _sync = new object();
  

        private IFilterGraph2 _graph;
        private ICaptureGraphBuilder2 _builder;
        private IBaseFilter _sourceFilter;
        private ISampleGrabber _grabber;
        private IBaseFilter _grabberFilter;
        private IMediaControl _mediaControl;

        private IAMCameraControl _cameraControl;

        private int _width;
        private int _height;
        private volatile bool _running;

        private readonly ITemperatureConverter _temp;

        public ThermalCamera(ITemperatureConverter temperatureConverter)
        {
            _temp = temperatureConverter ?? throw new ArgumentNullException(nameof(temperatureConverter));
        }

        public int Width => _width;
        public int Height => _height;

        public List<int> Hotpixels { get; set; } = new List<int>();

        public event EventHandler<ThermalFrame> FrameReceived;
        public int MetaRows { get; } = 4;            // Default: 4
        public int ImageHeight => Math.Max(0, Height - MetaRows);

        /// <summary>
        /// Öffnet die Kamera (UVC) via DirectShow. deviceMonikerName ist optional (Teilstring).
        /// </summary>
        public void Open(string deviceMonikerNameContains = null)
        {
            lock (_sync)
            {
                if (_graph != null) throw new InvalidOperationException("Already open.");

                _graph = (IFilterGraph2)new FilterGraph();
                _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
                _builder.SetFiltergraph(_graph);

                // Kamera auswählen
                var devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                var dev = string.IsNullOrWhiteSpace(deviceMonikerNameContains)
                    ? devs.FirstOrDefault()
                    : devs.FirstOrDefault(d => d.Name.IndexOf(deviceMonikerNameContains, StringComparison.OrdinalIgnoreCase) >= 0);

                if (dev == null) throw new InvalidOperationException("Keine VideoInputDevice-Kamera gefunden.");

                _graph.AddSourceFilterForMoniker(dev.Mon, null, dev.Name, out _sourceFilter);

                // IAMCameraControl (für Zoom writes)
                _cameraControl = _sourceFilter as IAMCameraControl;

                // SampleGrabber
                _grabber = (ISampleGrabber)new SampleGrabber();
                _grabberFilter = (IBaseFilter)_grabber;

                var mt = new AMMediaType
                {
                    majorType = MediaType.Video,
                    subType = MediaSubType.YUY2, // häufig bei Thermal-Raw (16-bit). Falls nicht klappt: YUY2/MJPG probieren.
                    formatType = FormatType.VideoInfo
                };
                _grabber.SetMediaType(mt);

                _graph.AddFilter(_grabberFilter, "SampleGrabber");

                // NullRenderer
                var nullRenderer = (IBaseFilter)new NullRenderer();
                _graph.AddFilter(nullRenderer, "NullRenderer");

                // Verbinden
                int hr = _builder.RenderStream(PinCategory.Capture, MediaType.Video, _sourceFilter, _grabberFilter, nullRenderer);
                DsError.ThrowExceptionForHR(hr);

                // Format auslesen
                var connectedMt = new AMMediaType();
                hr = _grabber.GetConnectedMediaType(connectedMt);
                DsError.ThrowExceptionForHR(hr);

                var vih = (VideoInfoHeader)Marshal.PtrToStructure(connectedMt.formatPtr, typeof(VideoInfoHeader));
                _width = vih.BmiHeader.Width;
                _height = vih.BmiHeader.Height;

                // Callback aktivieren
                _grabber.SetBufferSamples(false);
                _grabber.SetOneShot(false);

                var cb = new SampleGrabberCallback(this);
                hr = _grabber.SetCallback(cb, 1); // 1 = BufferCB
                DsError.ThrowExceptionForHR(hr);

                _mediaControl = (IMediaControl)_graph;
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_graph == null) throw new InvalidOperationException("Open first.");
                if (_running) return;
                int hr = _mediaControl.Run();
                DsError.ThrowExceptionForHR(hr);
                _running = true;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_graph == null) return;
                _mediaControl.Stop();
                _running = false;
            }
        }

        /// <summary>
        /// Schaltet den RAW-Mode ein (so wie bei OpenCV via ZOOM=0x8004). :contentReference[oaicite:7]{index=7}
        /// </summary>
        public void EnableRawMode()
        {
            SetZoomCommand(ZOOM_CMD_RAW_ON);
        }

        /// <summary>
        /// Shutter schließen / FFC-„flat field“-Basis: ZOOM=0x8000.
        /// Bei manchen Geräten bleibt der Shutter geschlossen, wenn man schneller als 1 Hz wiederholt. :contentReference[oaicite:8]{index=8}
        /// </summary>
        public void CloseShutter(int keepClosedMs = 0)
        {
            SetZoomCommand(ZOOM_CMD_SHUTTER_CLOSE);

            if (keepClosedMs > 0)
            {
                var sw = Environment.TickCount;
                while (unchecked(Environment.TickCount - sw) < keepClosedMs)
                {
                    Thread.Sleep(300);
                    SetZoomCommand(ZOOM_CMD_SHUTTER_CLOSE);
                }
            }
        }

        /// <summary>
        /// FFC auslösen (modellabhängig). Häufig wird ein kleinerer Wert (z.B. 0x80) erwähnt. :contentReference[oaicite:9]{index=9}
        /// </summary>
        public void TriggerFfc()
        {
            SetZoomCommand(ZOOM_CMD_FFC_TRIGGER);
        }

        /// <summary>
        /// Beispiel: Temperatur eines Pixels (x,y) aus letztem Frame müsstest du im Consumer speichern.
        /// (Hier nur Helper für Raw->°C)
        /// </summary>
        public double RawToCelsius(ushort raw) => _temp.RawToCelsius(raw);

        private void SetZoomCommand(int value)
        {
            lock (_sync)
            {
                if (_cameraControl == null)
                    throw new NotSupportedException("IAMCameraControl nicht verfügbar (Zoom write geht nicht).");

                // Flags: Manual (damit der Treiber den Wert übernimmt)
                int hr = _cameraControl.Set(CameraControlProperty.Zoom, value, CameraControlFlags.Manual);
                DsError.ThrowExceptionForHR(hr);
            }
        }

        private void OnFrame(byte[] buffer, int len)
        {
            // Der SampleGrabber liefert „Top-Down“ oder „Bottom-Up“ je nach Format;
            // Thermal RAW in Y16 ist i.d.R. 16-bit little endian pro Pixel.
            // Wir parsen in ushort[].
            int pixelCount = _width * _height;
            int neededBytes = pixelCount * 2;
            if (len < neededBytes) return;

            var raw = new ushort[pixelCount];
            Buffer.BlockCopy(buffer, 0, raw, 0, neededBytes);


            if (Hotpixels != null) { 
                raw = RemoveHotPixels(raw, Hotpixels);
            }

            // Optional: 8-bit Preview (simple min/max stretch)
            var gray8 = new byte[pixelCount];
            ushort min = ushort.MaxValue, max = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                var v = raw[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            var range = Math.Max(1, max - min);
            for (int i = 0; i < raw.Length; i++)
            {
                gray8[i] = (byte)(((raw[i] - min) * 255) / range);
            }

            // Meta: falls deine Kamera „untere Zusatzzeilen“ sendet, würdest du hier trennen:
            // Viele Setups nutzen „4 line data“ am Ende des Frames. :contentReference[oaicite:10]{index=10}
            // -> Ohne exaktes Format lassen wir MetaU16 erstmal null.

            var frame = new ThermalFrame(_width, _height, raw, gray8, metaU16: null);
            FrameReceived?.Invoke(this, frame);
        }


        private ushort[] RemoveHotPixels(ushort[] raw, List<int> hotpixelIndices)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (hotpixelIndices == null || hotpixelIndices.Count == 0) return raw;

            int w = _width;
            int h = _height;
            int n = w * h;
            if (raw.Length != n) return raw;

            // Optional: Kopie (damit Nachbarwerte nicht durch vorherige Korrekturen verfälscht werden)
            var src = raw;
            var dst = (ushort[])raw.Clone();

            ushort[] nb = new ushort[8];

            foreach (int i in hotpixelIndices)
            {
                if (i < 0 || i >= n) continue;

                int x = i % w;
                int y = i / w;

                // Rand: nimm einen einfachen Ersatz (z.B. rechts oder unten, falls vorhanden)
                if (x == 0 || x == w - 1 || y == 0 || y == h - 1)
                {
                    int j = (x + 1 < w) ? i + 1 : (x - 1 >= 0 ? i - 1 : i);
                    dst[i] = src[j];
                    continue;
                }

                // 8 Nachbarn sammeln (3x3 ohne Zentrum)
                int idx = 0;
                int row = y * w;

                nb[idx++] = src[row - w + (x - 1)];
                nb[idx++] = src[row - w + x];
                nb[idx++] = src[row - w + (x + 1)];

                nb[idx++] = src[row + (x - 1)];
                nb[idx++] = src[row + (x + 1)];

                nb[idx++] = src[row + w + (x - 1)];
                nb[idx++] = src[row + w + x];
                nb[idx++] = src[row + w + (x + 1)];

                Array.Sort(nb);
                dst[i] = nb[4]; // Median der 8 Werte (oben ist das "obere Median" – passt hier gut)
            }

            return dst;
        }


        public void Dispose()
        {
            Stop();

            lock (_sync)
            {
                if (_mediaControl != null) Marshal.ReleaseComObject(_mediaControl); _mediaControl = null;
                if (_grabber != null) Marshal.ReleaseComObject(_grabber); _grabber = null;
                if (_grabberFilter != null) Marshal.ReleaseComObject(_grabberFilter); _grabberFilter = null;
                if (_sourceFilter != null) Marshal.ReleaseComObject(_sourceFilter); _sourceFilter = null;
                if (_builder != null) Marshal.ReleaseComObject(_builder); _builder = null;
                if (_graph != null) Marshal.ReleaseComObject(_graph); _graph = null;
            }
        }

        private sealed class SampleGrabberCallback : ISampleGrabberCB
        {
            private readonly ThermalCamera _owner;

            public SampleGrabberCallback(ThermalCamera owner) => _owner = owner;

            public int SampleCB(double SampleTime, IMediaSample pSample) => 0;

            public int BufferCB(double SampleTime, IntPtr pBuffer, int BufferLen)
            {
                if (!_owner._running) return 0;

                var managed = new byte[BufferLen];
                Marshal.Copy(pBuffer, managed, 0, BufferLen);
                _owner.OnFrame(managed, BufferLen);
                return 0;
            }
        }


        public Task<ThermalFrame> CaptureSingleFrameAsync(int timeoutMs = 1000)
        {
            var tcs = new TaskCompletionSource<ThermalFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<ThermalFrame> handler = null;
            handler = (s, f) =>
            {
                FrameReceived -= handler;
                tcs.TrySetResult(f);
            };

            FrameReceived += handler;

            if (timeoutMs > 0)
            {
                var cts = new CancellationTokenSource(timeoutMs);
                cts.Token.Register(() =>
                {
                    FrameReceived -= handler;
                    tcs.TrySetException(new TimeoutException($"Kein Frame innerhalb {timeoutMs} ms erhalten."));
                });
            }

            return tcs.Task;
        }

        public async Task<ThermalFrame> CaptureDarkFrameAsync(int keepClosedMs = 800, int timeoutMs = 1500)
        {
            Start();
            // Shutter schließen und kurz stabilisieren
            CloseShutter(keepClosedMs: keepClosedMs);

            // Dark-Frame aufnehmen
            var dark = await CaptureSingleFrameAsync(timeoutMs);

            // Shutter wieder "auf": bei vielen Geräten reicht es, NICHT weiter 0x8000 zu senden.
            // Optional: TriggerFfc(); (falls das bei deinem Modell den Normalbetrieb sauber macht)
            
            
            
            return dark;

        }


        public async Task<ThermalFrame> AcquireSumAsync(
    TimeSpan duration,
    int metaRows,
    Func<ThermalFrame, ThermalFrame> preprocess = null,
    bool normalizeToU16 = false,
    CancellationToken ct = default)
        {
            
            if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

            var acc = new FrameAccumulator(Width, Height, metaRows);

            EventHandler<ThermalFrame> handler = null;
            handler = (s, f) =>
            {
                try
                {
                    if (preprocess != null) f = preprocess(f);
                    acc.Add(f);
                }
                catch { }
            };

            FrameReceived += handler;

            try
            {
                Start();
                await Task.Delay(duration, ct);

                if (acc.Count == 0)
                    throw new TimeoutException("Während Acquire wurden keine Frames empfangen.");

                return acc.BuildFrame(normalizeToU16);
            }
            finally
            {
                Stop();
                FrameReceived -= handler;
            }
        }

    }

    public sealed class DarkFieldCorrector
    {
        private float[] _dark;        // reference frame as float
        private float _offsetMean;    // mean of reference
        private int _w, _h, _metaRows;

        public bool IsCalibrated => _dark != null;

        public void SetDarkFrame(ThermalFrame darkFrame, int metaRows)
        {
            if (darkFrame == null) throw new ArgumentNullException(nameof(darkFrame));
            if (darkFrame.RawU16 == null) throw new InvalidOperationException("DarkFrame.RawU16 fehlt.");
            if (darkFrame.RawU16.Length != darkFrame.Width * darkFrame.Height)
                throw new InvalidOperationException("DarkFrame.RawU16 hat falsche Länge.");

            _w = darkFrame.Width;
            _h = darkFrame.Height;
            _metaRows = Math.Max(0, metaRows);

            int total = _w * _h;
            int visibleH = Math.Max(0, _h - _metaRows);
            int visibleCount = _w * visibleH;

            _dark = new float[total];

            double sum = 0.0;
            for (int i = 0; i < visibleCount; i++)
            {
                float v = darkFrame.RawU16[i];
                _dark[i] = v;
                sum += v;
            }

            // Meta-Zeilen einfach übernehmen (werden später auch nicht korrigiert)
            for (int i = visibleCount; i < total; i++)
                _dark[i] = darkFrame.RawU16[i];

            _offsetMean = (visibleCount > 0) ? (float)(sum / visibleCount) : 0f;
        }

        public ThermalFrame Apply(ThermalFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (_dark == null) return frame;

            if (frame.Width != _w || frame.Height != _h)
                throw new InvalidOperationException("Frame-Größe passt nicht zum Dark-Frame.");

            if (frame.RawU16 == null || frame.RawU16.Length != _w * _h)
                throw new InvalidOperationException("Frame.RawU16 fehlt oder hat falsche Länge.");

            int total = _w * _h;
            int visibleH = Math.Max(0, _h - _metaRows);
            int visibleCount = _w * visibleH;

            var corrected = new ushort[total];

            // Sichtbaren Bereich korrigieren: raw - dark + mean(dark)
            for (int i = 0; i < visibleCount; i++)
            {
                float v = frame.RawU16[i] - _dark[i] + _offsetMean;

                if (v < 0f) v = 0f;
                else if (v > 65535f) v = 65535f;

                corrected[i] = (ushort)(v + 0.5f);
            }

            // Meta-Bereich unverändert
            for (int i = visibleCount; i < total; i++)
                corrected[i] = frame.RawU16[i];

            return new ThermalFrame(_w, _h, corrected, gray8: null, metaU16: frame.MetaU16, metaRows: frame.MetaRows);
        }
    }
}

