using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Devices;

namespace Soundboard.Poc;

/// <summary>
/// engine-selftest: проверка движка через шину VB-CABLE. Наушники и настоящий микрофон выключены;
/// CABLE Output пишется в память (никуда не выводится). Не запускается, если CABLE Output сейчас
/// кто-то слушает (Discord, OBS) — иначе собеседники услышат тестовые тоны (ревью M5).
/// </summary>
internal static class EngineSelfTest
{
    private const float Threshold = 0.02f;

    private sealed record CaseResult(string Name, bool Ok, string Details);

    public static int Run(CliOptions options)
    {
        string cableCaptureId;
        using (var catalog = new MmDeviceCatalog())
        {
            var snapshot = catalog.Snapshot();
            var capture = VbCable.SelectCapture(snapshot.Inputs);
            if (VbCable.SelectRender(snapshot.Outputs) is null || capture is null)
                throw new VbCableNotFoundException("VB-CABLE не найден: самопроверке движка нужны CABLE Input и CABLE Output.");
            cableCaptureId = capture.Id;

            var listeners = catalog.ListeningApps(capture.Id);
            if (listeners.Count > 0 && !options.Force)
            {
                Ui.Error($"CABLE Output сейчас слушают: {string.Join(", ", listeners.Select(l => l.ProcessName))}.");
                Ui.Info("Тестовые тоны услышат ваши собеседники. Выйдите из звонка (или закройте программу) и повторите.");
                Ui.Info("Запустить всё равно: .\\poc.cmd engine-selftest --force");
                return 6;
            }
        }

        var settings = AudioSettings.Default with { HeadphonesEnabled = false, MicPassthroughEnabled = false, LatencyProfile = options.Profile };
        var engineOptions = AudioEngineOptions.For(options.Profile);
        var engine = new AudioEngine(engineOptions, msg => { if (EngineCommands.Verbose) Ui.Dim($"  · {msg}"); });
        var endReasons = new System.Collections.Concurrent.ConcurrentDictionary<VoiceId, (PlaybackEndReason? Reason, long Ticks)>();
        engine.PlaybackEnded += (_, e) => endReasons[e.Voice] = (e.Reason, Stopwatch.GetTimestamp());

        try
        {
            engine.StartAsync(settings).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }

        try
        {
            if (!WaitFor(() => engine.Status.Cable.State == BusState.Running, TimeSpan.FromSeconds(3)))
            {
                Ui.Error("Шина VB-CABLE не запустилась.");
                EngineCommands.PrintStatus(engine.Status);
                return 3;
            }

            Thread.Sleep(400);   // пауза после старта кабеля (движок 150 мс не принимает голоса)
            using var probe = new CableProbe(cableCaptureId);
            probe.Start();
            Thread.Sleep(300);

            Ui.Heading($"Самопроверка движка (профиль {options.Profile}, буфер кабеля {engineOptions.CableBufferMs} мс" +
                       (options.Load > 0 ? $", нагрузка {options.Load} потоков + GC" : "") + ")");
            Ui.Info($"  Кабель: {engine.Status.Cable.DeviceName}; приоритет звукового потока: {engine.Status.Cable.AudioThreadPriority}");

            using var load = options.Load > 0 ? LoadGenerator.Start(options.Load) : null;
            if (load is not null)
                Thread.Sleep(500);

            var results = new List<CaseResult>();
            for (var run = 1; run <= options.Runs; run++)
            {
                if (options.Runs > 1)
                    Ui.Dim($"  — прогон {run}/{options.Runs}");
                results.Add(CaseTone(engine, probe));
                results.Add(CaseOverlap(engine, probe));
                results.Add(CaseLimiter(engine, probe));
                results.Add(CaseStopAll(engine, probe, endReasons));
                foreach (var r in results.Skip(results.Count - 4))
                    Print(r);
            }

            var status = engine.Status;
            var counters = new CaseResult(
                "счётчики",
                status.Cable.CallbackErrors == 0 && status.Cable.AudioThreadAllocatedBytes == 0 && probe.Discontinuities == 0,
                $"ошибок в потоке {status.Cable.CallbackErrors}, выделений {status.Cable.AudioThreadAllocatedBytes} Б, " +
                $"разрывов захвата {probe.Discontinuities}, почти-пусто {status.Cable.NearEmpty}, пересборок {status.Cable.Restarts}");
            Print(counters);
            results.Add(counters);

            var failed = results.Count(r => !r.Ok);
            Ui.Info("");
            if (failed == 0)
            {
                Ui.Ok($"✓ Движок прошёл самопроверку ({results.Count} проверок).");
                return 0;
            }

            Ui.Error($"✗ Не пройдено проверок: {failed} из {results.Count}.");
            return 5;
        }
        finally
        {
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // ───────── проверки ─────────

    private static CaseResult CaseTone(AudioEngine engine, CableProbe probe)
    {
        const double hz = 1000, db = -12, seconds = 2.0;
        var sound = engine.AddSound("тон", TestTones.Tone(hz, db, seconds));
        var start = probe.Mark();
        probe.Arm();
        var t0 = Stopwatch.GetTimestamp();
        var play = engine.Play(sound);
        if (play.Status != PlayStatus.Started)
            return new CaseResult("тон 1 кГц", false, $"не запущен: {play.Status}");
        Thread.Sleep((int)(seconds * 1000) + 400);

        var data = probe.Since(start);
        var (first, last) = Bounds(data);
        if (first < 0)
            return new CaseResult("тон 1 кГц", false, "сигнал не дошёл до CABLE Output");

        var duration = (last - first + 1) / (double)AudioFormat.SampleRate;
        var amplitude = Math.Pow(10, db / 20);
        var peakDb = 20 * Math.Log10(Peak(data, first, last));
        var edge = AudioFormat.SampleRate / 50;   // 20 мс от краёв
        var jumps = Discontinuities(data, first + edge, last - edge, hz, amplitude);
        var latency = probe.LatencyMs(t0);

        var ok = Math.Abs(duration - seconds) < 0.005 && Math.Abs(peakDb - db) < 0.5 && jumps == 0;
        return new CaseResult("тон 1 кГц", ok,
            $"длительность {duration:0.0000} с, пик {peakDb:0.00} dBFS, разрывов {jumps}, задержка Play→кабель ≈ {latency:0} мс");
    }

    private static CaseResult CaseOverlap(AudioEngine engine, CableProbe probe)
    {
        var a = engine.AddSound("1 кГц", TestTones.Tone(1000, -12, 1.5));
        var b = engine.AddSound("1,5 кГц", TestTones.Tone(1500, -12, 1.5));
        var start = probe.Mark();
        engine.Play(a);
        engine.Play(b);
        Thread.Sleep(1900);

        var data = probe.Since(start);
        var (first, last) = Bounds(data);
        if (first < 0)
            return new CaseResult("наложение", false, "сигнал не дошёл");
        var mid = first + ((last - first) / 4);
        var len = (last - first) / 2;
        var l1 = Goertzel(data, mid, len, 1000);
        var l2 = Goertzel(data, mid, len, 1500);
        var ok = Math.Abs(l1 + 12) < 1 && Math.Abs(l2 + 12) < 1;
        return new CaseResult("наложение", ok, $"1 кГц {l1:0.0} dBFS, 1,5 кГц {l2:0.0} dBFS (ожидалось по −12)");
    }

    private static CaseResult CaseLimiter(AudioEngine engine, CableProbe probe)
    {
        var start = probe.Mark();
        foreach (var hz in new[] { 300.0, 500, 700, 900 })
            engine.Play(engine.AddSound($"{hz} Гц", TestTones.Tone(hz, 0, 1.0)));
        Thread.Sleep(1400);

        var data = probe.Since(start);
        var (first, last) = Bounds(data);
        if (first < 0)
            return new CaseResult("лимитер", false, "сигнал не дошёл");
        var peakDb = 20 * Math.Log10(Peak(data, first, last));
        return new CaseResult("лимитер", peakDb <= -0.95, $"4 тона по 0 dBFS → пик {peakDb:0.00} dBFS (потолок −1)");
    }

    private static CaseResult CaseStopAll(AudioEngine engine, CableProbe probe,
        System.Collections.Concurrent.ConcurrentDictionary<VoiceId, (PlaybackEndReason? Reason, long Ticks)> ended)
    {
        var sound = engine.AddSound("длинный тон", TestTones.Tone(1000, -12, 3.0));
        var start = probe.Mark();
        var play = engine.Play(sound);
        Thread.Sleep(1000);
        var t0 = Stopwatch.GetTimestamp();
        engine.StopAll();
        Thread.Sleep(500);

        var data = probe.Since(start);
        var (first, last) = Bounds(data);
        if (first < 0)
            return new CaseResult("стоп всё", false, "сигнал не дошёл");

        // Длина затухания: от последнего сэмпла ≥ 90 % амплитуды до конца сигнала
        var amplitude = (float)Math.Pow(10, -12 / 20.0);
        var lastFull = last;
        while (lastFull > first && Math.Abs(data[lastFull]) < 0.9f * amplitude)
            lastFull--;
        var fadeMs = (last - lastFull) * 1000.0 / AudioFormat.SampleRate;
        var played = (last - first) / (double)AudioFormat.SampleRate;

        var eventOk = ended.TryGetValue(play.Voice, out var info) && info.Reason == PlaybackEndReason.StopAll;
        var eventMs = eventOk ? (info.Ticks - t0) * 1000.0 / Stopwatch.Frequency : double.NaN;
        var ok = eventOk && fadeMs is > 4 and < 20 && played < 2.0 && eventMs < 100;
        return new CaseResult("стоп всё", ok,
            $"затухание ≈ {fadeMs:0.0} мс, событие {(eventOk ? $"StopAll через {eventMs:0} мс" : "не пришло")}");
    }

    // ───────── анализ ─────────

    private static (int First, int Last) Bounds(ReadOnlySpan<float> data)
    {
        int first = -1, last = -1;
        for (var i = 0; i < data.Length; i++)
        {
            if (Math.Abs(data[i]) > Threshold)
            {
                if (first < 0)
                    first = i;
                last = i;
            }
        }

        return (first, last);
    }

    private static float Peak(ReadOnlySpan<float> data, int first, int last)
    {
        var peak = 0f;
        for (var i = first; i <= last; i++)
            peak = Math.Max(peak, Math.Abs(data[i]));
        return peak;
    }

    /// <summary>Разрывы синуса: |x[n] − 2cos(w)·x[n−1] + x[n−2]| выше 2 % амплитуды.</summary>
    private static int Discontinuities(ReadOnlySpan<float> data, int from, int to, double hz, double amplitude)
    {
        var c = 2 * Math.Cos(2 * Math.PI * hz / AudioFormat.SampleRate);
        var limit = 0.02 * amplitude;
        var count = 0;
        for (var n = Math.Max(from, 2); n <= to; n++)
        {
            if (Math.Abs(data[n] - (c * data[n - 1]) + data[n - 2]) > limit)
            {
                count++;
                n += 48;   // один разрыв — одно событие
            }
        }

        return count;
    }

    /// <summary>Уровень синуса на частоте (алгоритм Гёрцеля), dBFS по пиковой амплитуде.</summary>
    private static double Goertzel(ReadOnlySpan<float> data, int start, int length, double hz)
    {
        var w = 2 * Math.PI * hz / AudioFormat.SampleRate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < length; i++)
        {
            var s = data[start + i] + (coeff * s1) - s2;
            s2 = s1;
            s1 = s;
        }

        var power = (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
        var amplitude = 2 * Math.Sqrt(Math.Max(power, 0)) / length;
        return 20 * Math.Log10(Math.Max(amplitude, 1e-9));
    }

    private static void Print(CaseResult r)
    {
        var line = $"  {(r.Ok ? "✓" : "✗")} {r.Name,-10} {r.Details}";
        if (r.Ok)
            Ui.Info(line);
        else
            Ui.Error(line);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>Захват CABLE Output в память (первый канал). Никуда не выводится.</summary>
    private sealed class CableProbe : IDisposable
    {
        private readonly float[] data = new float[AudioFormat.SampleRate * 600];
        private readonly MMDeviceEnumerator enumerator = new();
        private readonly MMDevice device;
        private readonly WasapiRecorder recorder;
        private readonly WaveFormat format;
        private int count;
        private long discontinuities;
        private long packets;
        private int armed;
        private long detectedTicks;

        public CableProbe(string deviceId)
        {
            device = enumerator.GetDevice(deviceId);
            recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(100)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            format = recorder.WaveFormat;
            recorder.DataAvailable += OnData;
        }

        public long Discontinuities => Interlocked.Read(ref discontinuities);

        public void Start() => recorder.StartRecording();

        public int Mark() => Volatile.Read(ref count);

        public ReadOnlySpan<float> Since(int index) => data.AsSpan(index, Volatile.Read(ref count) - index);

        public void Arm()
        {
            Volatile.Write(ref detectedTicks, 0);
            Volatile.Write(ref armed, 1);
        }

        public double LatencyMs(long t0)
        {
            var t = Volatile.Read(ref detectedTicks);
            return t == 0 ? double.NaN : (t - t0) * 1000.0 / Stopwatch.Frequency;
        }

        public void Dispose()
        {
            recorder.StopRecording();
            recorder.Dispose();
            device.Dispose();
            enumerator.Dispose();
        }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
        {
            var now = Stopwatch.GetTimestamp();
            // Первый пакет после старта WASAPI обычно помечен как разрыв — это не сбой
            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && Interlocked.Increment(ref packets) > 1)
                Interlocked.Increment(ref discontinuities);
            else if ((flags & AudioClientBufferFlags.DataDiscontinuity) == 0)
                Interlocked.Increment(ref packets);

            var start = count;
            var samples = new List<float>(0);
            var frames = SampleDecoder.AppendFirstChannel(buffer.ToArray(), buffer.Length, format, samples);
            var n = Math.Min(frames, data.Length - start);
            for (var i = 0; i < n; i++)
                data[start + i] = (flags & AudioClientBufferFlags.Silent) != 0 ? 0 : samples[i];
            Volatile.Write(ref count, start + n);

            if (Volatile.Read(ref armed) == 1)
            {
                for (var i = 0; i < n; i++)
                {
                    if (Math.Abs(data[start + i]) > Threshold)
                    {
                        Volatile.Write(ref detectedTicks, now - (long)((n - i) * (double)Stopwatch.Frequency / format.SampleRate));
                        Volatile.Write(ref armed, 0);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Нагрузка: отдельный процесс, занимающий N ядер (обычный приоритет), плюс поток с мусором для GC у нас.</summary>
    private sealed class LoadGenerator : IDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource cts = new();
        private readonly Thread gcThread;

        private LoadGenerator(int threads)
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"burn {threads}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            gcThread = new Thread(() =>
            {
                // ~100 МБ/с мусора и принудительные сборки: GC останавливает все управляемые потоки, в т.ч. звуковые
                var sw = Stopwatch.StartNew();
                var keep = new List<byte[]>();
                while (!cts.IsCancellationRequested)
                {
                    keep.Add(new byte[100_000]);
                    if (keep.Count > 200)
                        keep.Clear();
                    if (sw.ElapsedMilliseconds > 2000)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                        sw.Restart();
                    }

                    Thread.Sleep(1);
                }
            })
            { IsBackground = true, Name = "gc-stress" };
            gcThread.Start();
        }

        public static LoadGenerator Start(int threads) => new(threads);

        public void Dispose()
        {
            cts.Cancel();
            gcThread.Join(1000);
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Уже завершился
            }

            process.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>Команда burn N: занять N ядер на 10 минут (или до завершения родителем).</summary>
    public static int Burn(int threads)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => new Thread(() =>
        {
            double x = 1;
            while (DateTime.UtcNow < until)
                x = Math.Sqrt(x + 1.000001);
            GC.KeepAlive(x);
        })
        { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        return 0;
    }
}
