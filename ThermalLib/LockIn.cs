using System.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThermalCamLib.LockIn
{
    public interface IStimulusController
    {
        /// <summary>Stimulus EIN (z.B. Netzteil an).</summary>
        Task TurnOnAsync(CancellationToken ct);

        /// <summary>Stimulus AUS (z.B. Netzteil aus).</summary>
        Task TurnOffAsync(CancellationToken ct);
    }


    public sealed class LockInMeasurementRunner
    {
        private readonly ThermalCamera _camera;
        private static volatile bool _stimulusOn;

        public LockInMeasurementRunner(ThermalCamera camera)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Führt eine Lock-In Messung aus.
        /// - toggelt stimulus als Rechtecksignal mit frequencyHz und dutyCycle
        /// - sammelt Frames und macht synchrone Demodulation (sin/cos) bei frequencyHz
        /// </summary>
        public async Task<LockInResult> RunAsync(
            IStimulusController stimulus,
            double frequencyHz,
            TimeSpan duration,
            int metaRows,
            double dutyCycle = 0.5,
            TimeSpan? settleBeforeStart = null,
            Func<ThermalFrame, ThermalFrame> framePreprocess = null,     // z.B. DarkFieldCorrector.Apply
            Func<ushort, double> xSelector = null,                       // z.B. raw=>raw oder raw->temp
            CancellationToken ct = default)
        {
            if (stimulus == null) throw new ArgumentNullException(nameof(stimulus));
            if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
            if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            if (dutyCycle <= 0 || dutyCycle >= 1) throw new ArgumentOutOfRangeException(nameof(dutyCycle));

            int w = _camera.Width;
            int h = _camera.Height;

            var acc = new LockInAccumulator(w, h, frequencyHz, metaRows: metaRows);

            // Zeitbasis
            var sw = Stopwatch.StartNew();

            // Optional settling (z.B. Kamera stabilisieren, Shutter öffnen etc.)
            if (settleBeforeStart.HasValue && settleBeforeStart.Value > TimeSpan.Zero)
                await Task.Delay(settleBeforeStart.Value, ct);

            // Frame-Handler
            EventHandler<ThermalFrame> handler = null;
            handler = (s, frame) =>
            {
                try
                {
                    if (framePreprocess != null)
                        frame = framePreprocess(frame);

                    // Zeit in Sekunden
                    double t = sw.Elapsed.TotalSeconds;

                    acc.AddFrame(frame, t, _stimulusOn, xSelector);
                }
                catch
                {
                    // keine Exceptions aus Event-Thread rauswerfen
                }
            };

            _camera.FrameReceived += handler;

            // Stimulus toggling Task
            var toggleTask = ToggleStimulusAsync(stimulus, frequencyHz, dutyCycle, sw, duration, ct);

            try
            {
                // Warten bis Dauer um ist (oder Cancel)
                await Task.Delay(duration, ct);

                // sicher AUS schalten
                await stimulus.TurnOffAsync(ct);

                // toggler sauber enden lassen
                await toggleTask;

                // Ergebnis
                return new LockInResult(acc, duration, frequencyHz);
            }
            finally
            {
                _camera.FrameReceived -= handler;
                try { await stimulus.TurnOffAsync(CancellationToken.None); } catch { /* ignore */ }
            }
        }

        private static async Task ToggleStimulusAsync(
            IStimulusController stimulus,
            double frequencyHz,
            double dutyCycle,
            Stopwatch sw,
            TimeSpan totalDuration,
            CancellationToken ct)
        {
            // Periodendauer
            double period = 1.0 / frequencyHz;
            double onTime = period * dutyCycle;
            double offTime = period * (1.0 - dutyCycle);

            // Start mit ON (kannst du leicht ändern)
            bool isOn = false;

            while (sw.Elapsed < totalDuration && !ct.IsCancellationRequested)
            {
                // ON
                if (!isOn)
                {
                    await stimulus.TurnOnAsync(ct);
                    _stimulusOn = true;
                    isOn = true;
                }
                await DelaySeconds(onTime, ct);

                if (sw.Elapsed >= totalDuration || ct.IsCancellationRequested) break;

                // OFF
                if (isOn)
                {
                    await stimulus.TurnOffAsync(ct);
                    _stimulusOn = false;
                    isOn = false;
                }
                await DelaySeconds(offTime, ct);
            }

            // Am Ende: sicher AUS
            await stimulus.TurnOffAsync(ct);
        }

        private static Task DelaySeconds(double seconds, CancellationToken ct)
        {
            if (seconds <= 0) return Task.CompletedTask;
            return Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
    }

    public sealed class LockInResult
    {
        public LockInResult(LockInAccumulator accumulator, TimeSpan duration, double frequencyHz)
        {
            Accumulator = accumulator;
            Duration = duration;
            FrequencyHz = frequencyHz;
        }

        public LockInAccumulator Accumulator { get; }
        public TimeSpan Duration { get; }
        public double FrequencyHz { get; }
    }
}
