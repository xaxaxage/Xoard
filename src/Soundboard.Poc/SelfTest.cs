using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Классический WasapiCapture (в NAudio 3 помечен устаревшим) — см. комментарий в Player.cs
#pragma warning disable CS0618

namespace Soundboard.Poc;

/// <summary>
/// Самопроверка без Discord: играем тон в «CABLE Input» и одновременно пишем «CABLE Output».
/// Если тон пришёл с той же частотой, длительностью и без обрывов — цепочка VB-CABLE работает.
/// </summary>
internal static class SelfTest
{
    private const float SignalThreshold = 0.02f;   // ≈ −34 dBFS
    private const double DropoutMs = 2.0;          // тишина дольше этого внутри тона — обрыв
    private const double MaxDropoutMs = 100.0;     // участок, отделённый большей паузой, — посторонний звук
    private const double FrequencyTolerance = 0.03;

    public static int Run(CliOptions options)
    {
        var frequency = options.Frequency ?? 1000;
        var seconds = options.Seconds ?? 1.5;
        var gain = options.Volume ?? 0.25f;

        using var enumerator = new MMDeviceEnumerator();
        var render = AudioDevices.FindVbCableRender(enumerator);
        var capture = AudioDevices.FindVbCableCapture(enumerator);

        Ui.Heading("Самопроверка VB-CABLE");
        Ui.Info($"  Играю в:  {render.FriendlyName}  ({DescribeMix(render)})");
        Ui.Info($"  Слушаю:   {capture.FriendlyName}  ({DescribeMix(capture)})");
        Ui.Info($"  Тон:      {frequency:0} Гц, {seconds:0.0} с, уровень {ToDb(gain):0.0} dBFS");
        Ui.Dim("  (в наушники ничего не выводится)");
        Ui.Info("");

        var clock = Stopwatch.StartNew();
        var samples = new List<float>((int)((seconds + 2) * 48_000));
        var playStartedMs = -1.0;
        var firstSignalMs = -1.0;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var recorder = new WasapiCapture(capture, useEventSync: true, audioBufferMillisecondsLength: 20);
        var format = recorder.WaveFormat;

        recorder.DataAvailable += (_, e) =>
        {
            var nowMs = clock.Elapsed.TotalMilliseconds;
            var start = samples.Count;
            var frames = SampleDecoder.AppendFirstChannel(e.Buffer, e.BytesRecorded, format, samples);

            if (firstSignalMs < 0 && Volatile.Read(ref playStartedMs) >= 0)
            {
                for (var i = 0; i < frames; i++)
                {
                    if (Math.Abs(samples[start + i]) > SignalThreshold)
                    {
                        // Момент появления сигнала: время прихода буфера минус «хвост» буфера после этого сэмпла
                        firstSignalMs = nowMs - ((frames - i) * 1000.0 / format.SampleRate);
                        break;
                    }
                }
            }
        };
        recorder.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);

        recorder.StartRecording();
        Thread.Sleep(300); // даём захвату стабилизироваться

        var tone = Signals.CreateTone(frequency, gain, seconds);
        Player.PlayToEnd([new PlaybackTarget(render, tone)], options.LatencyMs,
            onStarted: () => Volatile.Write(ref playStartedMs, clock.Elapsed.TotalMilliseconds + options.PreRollMs),
            preRollMs: options.PreRollMs, postRollMs: options.PostRollMs);

        Thread.Sleep(200); // ловим хвост
        recorder.StopRecording();
        if (stopped.Task.Wait(TimeSpan.FromSeconds(3)) && stopped.Task.Result is { } captureError)
        {
            throw captureError;
        }

        var latencyMs = playStartedMs >= 0 && firstSignalMs >= 0 ? firstSignalMs - playStartedMs : (double?)null;
        return Analyze([.. samples], format.SampleRate, frequency, gain, seconds, latencyMs);
    }

    private static int Analyze(float[] data, int sampleRate, double expectedHz, float gain, double seconds, double? latencyMs)
    {
        var segments = FindSegments(data, MsToSamples(DropoutMs, sampleRate));
        if (Environment.GetEnvironmentVariable("SOUNDBOARD_POC_DEBUG") == "1")
        {
            foreach (var (s, e) in segments)
            {
                Ui.Dim($"  [debug] сигнал {s * 1000.0 / sampleRate,7:0.0} … {e * 1000.0 / sampleRate,7:0.0} мс  ({(e - s + 1) * 1000.0 / sampleRate:0.0} мс)");
            }
        }

        if (segments.Count == 0)
        {
            Ui.Error("✗ Сигнал НЕ дошёл до CABLE Output.");
            Ui.Info("  Проверьте: Параметры звука Windows → «CABLE Output» не отключён и громкость не на нуле;");
            Ui.Info("  в панели VB-CABLE не стоит mute. Затем повторите: .\\poc.cmd selftest");
            return 4;
        }

        // Тон — самый длинный участок. Соседние участки за короткой паузой — его же куски (обрывы),
        // всё остальное — посторонний звук, который кто-то ещё играет в кабель.
        var toneIndex = segments.IndexOf(segments.MaxBy(s => s.End - s.Start));
        var (first, last) = segments[toneIndex];
        var maxGap = MsToSamples(MaxDropoutMs, sampleRate);
        var dropouts = 0;
        for (var i = toneIndex - 1; i >= 0 && first - segments[i].End <= maxGap; i--)
        {
            first = segments[i].Start;
            dropouts++;
        }

        for (var i = toneIndex + 1; i < segments.Count && segments[i].Start - last <= maxGap; i++)
        {
            last = segments[i].End;
            dropouts++;
        }

        var extraneous = segments.Count - 1 - dropouts;

        if (Environment.GetEnvironmentVariable("SOUNDBOARD_POC_DEBUG") == "1")
        {
            PrintPhaseJumps(data, first, last, sampleRate, expectedHz);
        }

        var length = last - first + 1;
        var midStart = first + (length / 4);
        var midEnd = last - (length / 4);

        var peak = 0f;
        for (var i = first; i <= last; i++)
        {
            peak = Math.Max(peak, Math.Abs(data[i]));
        }

        double sumSquares = 0;
        var crossings = 0;
        for (var i = midStart + 1; i <= midEnd; i++)
        {
            sumSquares += data[i] * data[i];
            if (data[i - 1] < 0 && data[i] >= 0)
            {
                crossings++;
            }
        }

        var rms = Math.Sqrt(sumSquares / Math.Max(1, midEnd - midStart));
        var measuredHz = crossings / ((midEnd - midStart) / (double)sampleRate);
        var durationSec = length / (double)sampleRate;

        var frequencyOk = Math.Abs(measuredHz - expectedHz) <= expectedHz * FrequencyTolerance;
        var durationOk = Math.Abs(durationSec - seconds) <= 0.05;
        var ok = frequencyOk && durationOk && dropouts == 0;

        Ui.Info($"  Частота:      {measuredHz:0.0} Гц (ожидалось {expectedHz:0}) {Mark(frequencyOk)}");
        Ui.Info($"  Длительность: {durationSec:0.00} с (ожидалось {seconds:0.00}) {Mark(durationOk)}");
        Ui.Info($"  Обрывы:       {dropouts} {Mark(dropouts == 0)}");
        Ui.Info($"  Уровень:      пик {ToDb(peak):0.0} dBFS, RMS {ToDb(rms):0.0} dBFS (отправлено пик {ToDb(gain):0.0} dBFS)");
        if (latencyMs is { } latency)
        {
            Ui.Info($"  Задержка:     ≈ {latency:0} мс (вывод → кабель → захват, грубая оценка)");
        }

        if (Math.Abs(ToDb(peak) - ToDb(gain)) > 3)
        {
            Ui.Warn("  ! Уровень отличается от отправленного больше чем на 3 дБ — проверьте громкость");
            Ui.Warn("    «CABLE Input» и «CABLE Output» в настройках звука Windows (должно быть 100%).");
        }

        if (extraneous > 0)
        {
            Ui.Warn($"  ! В кабеле был посторонний звук ({extraneous} фрагм.): какая-то программа тоже играет в CABLE Input.");
            Ui.Warn("    Обычно это значит, что CABLE Input выбран устройством вывода по умолчанию в Windows.");
        }

        Ui.Info("");
        if (ok)
        {
            Ui.Ok("✓ VB-CABLE работает: звук, сыгранный в CABLE Input, приходит в CABLE Output.");
            Ui.Info("  Следующий шаг — Discord: Настройки → Голос и видео → Устройство ввода = CABLE Output.");
            return 0;
        }

        Ui.Error("✗ Сигнал дошёл, но с искажениями (см. отметки ✗ выше).");
        return 5;
    }

    /// <summary>Участки сигнала, разделённые тишиной не короче minGap сэмплов.</summary>
    private static List<(int Start, int End)> FindSegments(float[] data, int minGap)
    {
        var segments = new List<(int, int)>();
        int start = -1, lastLoud = -1;
        for (var i = 0; i < data.Length; i++)
        {
            if (Math.Abs(data[i]) <= SignalThreshold)
            {
                continue;
            }

            if (start < 0)
            {
                start = i;
            }
            else if (i - lastLoud > minGap)
            {
                segments.Add((start, lastLoud));
                start = i;
            }

            lastLoud = i;
        }

        if (start >= 0)
        {
            segments.Add((start, lastLoud));
        }

        return segments;
    }

    /// <summary>Отладка: места, где период синуса «прыгает» — там выпали или вставились сэмплы.</summary>
    private static void PrintPhaseJumps(float[] data, int first, int last, int sampleRate, double expectedHz)
    {
        var expectedPeriod = sampleRate / expectedHz;
        var lastCrossing = -1.0;
        var jumps = 0;
        for (var i = first + 1; i <= last; i++)
        {
            if (data[i - 1] < 0 && data[i] >= 0)
            {
                // Точка пересечения нуля с субсэмпловой точностью
                var t = i - 1 + (data[i - 1] / (data[i - 1] - data[i]));
                if (lastCrossing >= 0)
                {
                    var period = t - lastCrossing;
                    if (Math.Abs(period - expectedPeriod) > 2)
                    {
                        jumps++;
                        Ui.Dim($"  [debug] скачок фазы на {(t - first) * 1000.0 / sampleRate,7:0.0} мс: период {period:0.0} сэмпл. вместо {expectedPeriod:0.0}");
                    }
                }

                lastCrossing = t;
            }
        }

        Ui.Dim($"  [debug] скачков фазы: {jumps}");
    }

    private static int MsToSamples(double ms, int sampleRate) => (int)(ms * sampleRate / 1000);

    private static string Mark(bool ok) => ok ? "✓" : "✗";

    private static string DescribeMix(MMDevice device) =>
        AudioDevices.TryGetMixFormat(device) is { } f ? InternalFormat.Describe(f) : "формат неизвестен";

    private static double ToDb(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-9));
}

/// <summary>Достаёт первый канал из буфера захвата WASAPI в float.</summary>
internal static class SampleDecoder
{
    public static int AppendFirstChannel(byte[] buffer, int bytes, WaveFormat format, List<float> destination)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == WaveSubFormats.IeeeFloat);
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = bytes / format.BlockAlign;

        for (var f = 0; f < frames; f++)
        {
            var o = f * format.BlockAlign;
            var value = (isFloat, bytesPerSample) switch
            {
                (true, 4) => BitConverter.ToSingle(buffer, o),
                (false, 2) => BitConverter.ToInt16(buffer, o) / 32768f,
                (false, 3) => (buffer[o] | (buffer[o + 1] << 8) | ((sbyte)buffer[o + 2] << 16)) / 8388608f,
                (false, 4) => BitConverter.ToInt32(buffer, o) / 2147483648f,
                _ => throw new NotSupportedException($"Формат захвата не поддерживается: {InternalFormat.Describe(format)}"),
            };
            destination.Add(value);
        }

        return frames;
    }
}
