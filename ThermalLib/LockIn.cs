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
        private static bool _stimulusOn;
        private int _frameCount;


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
            Func<ThermalFrame, ThermalFrame> framePreprocess = null,
            Func<ushort, double> xSelector = null,
            TimeSpan? integrationTime = null, 
            IProgress<LockInProgress> progress = null,
            int progressIntervalMs = 100,
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

            var progressTask = Task.Run(async () =>
            {
                if (progress == null) return;

                while (!ct.IsCancellationRequested && sw.Elapsed < duration)
                {
                    progress.Report(new LockInProgress
                    {
                        Elapsed = sw.Elapsed,
                        Total = duration,
                        Frames = Volatile.Read(ref _frameCount),
                        StimulusOn = _stimulusOn,
                        FrequencyHz = frequencyHz
                    });

                    await Task.Delay(progressIntervalMs, ct).ConfigureAwait(false);
                }

                progress.Report(new LockInProgress
                {
                    Elapsed = duration,
                    Total = duration,
                    Frames = Volatile.Read(ref _frameCount),
                    StimulusOn = _stimulusOn,
                    FrequencyHz = frequencyHz
                });
            }, ct);


            // Optional settling (z.B. Kamera stabilisieren, Shutter öffnen etc.)
            if (settleBeforeStart.HasValue && settleBeforeStart.Value > TimeSpan.Zero)
                await Task.Delay(settleBeforeStart.Value, ct);

            // Frame-Handler
            _frameCount = 0;

            int nPix = acc.PixelCount;
            IntegrationBuffer ib = integrationTime.HasValue ? new IntegrationBuffer(nPix) : null;

            EventHandler<ThermalFrame> handler = null;
            handler = (s, frame) =>
            {
                try
                {
                    if (framePreprocess != null)
                        frame = framePreprocess(frame);

                    if (ib == null)
                    {
                        // 🔹 bisheriges Verhalten
                        double t = sw.Elapsed.TotalSeconds;
                        acc.AddFrame(frame, t, _stimulusOn, xSelector);
                        Interlocked.Increment(ref _frameCount);
                        return;
                    }

                    // 🔸 Integration aktiv
                    int n = acc.PixelCount;
                    for (int i = 0; i < n; i++)
                        ib.Sum[i] += frame.RawU16[i];

                    ib.Count++;

                    var now = DateTime.UtcNow;
                    if ((now - ib.T0) >= integrationTime.Value)
                    {
                        // Mittelwert-Frame bauen
                        var raw = new ushort[n];
                        for (int i = 0; i < n; i++)
                            raw[i] = (ushort)(ib.Sum[i] / (ulong)ib.Count);

                        var integrated = new ThermalFrame(
                            acc.Width,
                            acc.VisibleHeight,
                            raw,
                            gray8: null,
                            metaU16: null,
                            metaRows: 0);

                        double tMid =
                            sw.Elapsed.TotalSeconds -
                            integrationTime.Value.TotalSeconds * 0.5;

                        acc.AddFrame(integrated, tMid, _stimulusOn, xSelector);
                        Interlocked.Increment(ref _frameCount);

                        ib.Reset();
                    }
                }
                catch { }
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
                await progressTask.ConfigureAwait(false);
                return new LockInResult(acc, duration, frequencyHz);
            }
            finally
            {
                _camera.FrameReceived -= handler;
                try { await stimulus.TurnOffAsync(CancellationToken.None); } catch { /* ignore */ }
            }
        }

        private sealed class IntegrationBuffer
        {
            public ulong[] Sum;
            public int Count;
            public DateTime T0;

            public IntegrationBuffer(int nPix)
            {
                Sum = new ulong[nPix];
                Reset();
            }

            public void Reset()
            {
                Array.Clear(Sum, 0, Sum.Length);
                Count = 0;
                T0 = DateTime.UtcNow;
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
