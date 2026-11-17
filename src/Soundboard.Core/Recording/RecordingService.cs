using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundboard.Core.Audio;

namespace Soundboard.Core.Recording;

/// <summary>Что записывать.</summary>
public enum RecordingSource
{
    /// <summary>Звук компьютера (всё, кроме самого саундборда).</summary>
    System,

    /// <summary>Микрофон.</summary>
    Microphone,
}

/// <summary>
/// Запись звука компьютера или микрофона и «повтор» — постоянный кольцевой буфер звука компьютера,
/// из которого по клавише сохраняются последние N секунд.
/// Звук компьютера пишется через петлю процессов БЕЗ собственного процесса (Windows 10 2004+):
/// звуки саундборда не попадают в записи. На старых Windows — петля устройства вывода по умолчанию.
/// Петля не присылает пакетов, пока тихо, поэтому паузы досыпаются тишиной по времени.
/// </summary>
public sealed class RecordingService : IAsyncDisposable
{
    /// <summary>Предел длины одной записи.</summary>
    public static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly Func<long> clock;
    private readonly long ticksPerSecond;

    // Захват звука компьютера (общий для повтора и записи)
    private WasapiRecorder? systemRecorder;
    private MMDevice? systemDevice;
    private WaveFormat? systemFormat;
    private int systemRate;
    private long lastSystemTicks;
    private float[] convertScratch = new float[48_000 * 2];

    // Повтор
    private float[]? ring;
    private int ringCapacityFrames;
    private long ringWritten;
    private bool replayEnabled;
    private int replaySeconds = 30;

    // Текущая запись
    private RecordingSource? recording;
    private List<float[]> chunks = [];
    private long recordedFrames;
    private int recordingRate;
    private long recordingStartTicks;
    private WasapiRecorder? micRecorder;
    private MMDevice? micDevice;
    private WaveFormat? micFormat;
    private float recordingPeak;

    public RecordingService()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    internal RecordingService(Func<long> clock, long ticksPerSecond)
    {
        this.clock = clock;
        this.ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Последняя ошибка (по-русски) или null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Звук компьютера пишется без звуков саундборда (петля процессов доступна).</summary>
    public bool ExcludesOwnSounds { get; private set; }

    public bool ReplayEnabled
    {
        get
        {
            lock (gate)
                return replayEnabled && ring is not null;
        }
    }

    public int ReplaySeconds
    {
        get
        {
            lock (gate)
                return replaySeconds;
        }
    }

    /// <summary>Сколько секунд уже накоплено в буфере повтора.</summary>
    public double ReplayBufferedSeconds
    {
        get
        {
            lock (gate)
                return ring is null || systemRate == 0 ? 0 : Math.Min(ringWritten, ringCapacityFrames) / (double)systemRate;
        }
    }

    public RecordingSource? Recording
    {
        get
        {
            lock (gate)
                return recording;
        }
    }

    public TimeSpan RecordingElapsed
    {
        get
        {
            lock (gate)
                return recording is null || recordingRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(recordedFrames / (double)recordingRate);
        }
    }

    /// <summary>Пик сигнала записи с прошлого вызова (0..1).</summary>
    public float TakeRecordingPeak()
    {
        lock (gate)
        {
            var p = recordingPeak;
            recordingPeak = 0;
            return p;
        }
    }

    /// <summary>Включить/выключить повтор и задать длину буфера (10–120 с).</summary>
    public async Task ConfigureReplayAsync(bool enabled, int seconds)
    {
        seconds = Math.Clamp(seconds, 10, 120);
        lock (gate)
        {
            replayEnabled = enabled;
            if (replaySeconds != seconds || !enabled)
            {
                ring = null;           // пересоздадим под новую длину
                ringWritten = 0;
            }

            replaySeconds = seconds;
        }

        await UpdateSystemCaptureAsync().ConfigureAwait(false);
    }

    /// <summary>Начать запись.</summary>
    /// <exception cref="InvalidOperationException">Уже идёт запись или не удалось открыть источник.</exception>
    public async Task StartRecordingAsync(RecordingSource source, string? micDeviceId)
    {
        lock (gate)
        {
            if (recording is not null)
                throw new InvalidOperationException("Запись уже идёт.");
            chunks = [];
            recordedFrames = 0;
            recordingPeak = 0;
            recordingStartTicks = clock();
        }

        if (source == RecordingSource.System)
        {
            lock (gate)
                recording = RecordingSource.System;
            await UpdateSystemCaptureAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (systemRecorder is null)
                {
                    recording = null;
                    throw new InvalidOperationException(LastError ?? "Не удалось начать запись звука компьютера.");
                }

                recordingRate = systemRate;
            }

            return;
        }

        await Task.Run(() => StartMic(micDeviceId)).ConfigureAwait(false);
    }

    /// <summary>
    /// Остановить запись и сохранить WAV в папку. Возвращает путь или null, если записалось меньше 0,2 с.
    /// </summary>
    public async Task<string?> StopRecordingAsync(string folder, string? name)
    {
        RecordingSource? source;
        WasapiRecorder? mic;
        MMDevice? device;
        lock (gate)
        {
            source = recording;
            mic = micRecorder;
            device = micDevice;
            micRecorder = null;
            micDevice = null;
        }

        if (source is null)
            return null;

        if (mic is not null)
        {
            await Task.Run(() =>
            {
                mic.StopRecording();
                mic.Dispose();
                device?.Dispose();
            }).ConfigureAwait(false);
        }

        float[] samples;
        int rate;
        lock (gate)
        {
            if (source == RecordingSource.System)
                PadSilenceToNow();
            recording = null;
            rate = recordingRate;
            samples = new float[recordedFrames * 2];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(samples, offset);
                offset += chunk.Length;
            }

            chunks = [];
            recordedFrames = 0;
        }

        await UpdateSystemCaptureAsync().ConfigureAwait(false);
        if (rate == 0 || samples.Length < rate / 5 * 2)
            return null;

        var path = UniquePath(folder, name ?? $"Запись {DateTime.Now:HH-mm-ss}");
        await Task.Run(() => AudioClips.WriteWav(path, samples, rate)).ConfigureAwait(false);
        return path;
    }

    /// <summary>Сохранить последние <paramref name="seconds"/> секунд повтора как WAV.</summary>
    /// <exception cref="InvalidOperationException">Повтор выключен или буфер пуст.</exception>
    public async Task<string> SaveReplayAsync(double seconds, string folder, string? name)
    {
        float[] samples;
        int rate;
        lock (gate)
        {
            if (!replayEnabled || ring is null || systemRate == 0)
                throw new InvalidOperationException(LastError ?? "Повтор выключен — включите его на странице «Запись».");
            PadSilenceToNow();
            rate = systemRate;
            var available = (int)Math.Min(ringWritten, ringCapacityFrames);
            var frames = (int)Math.Min(available, Math.Max(1, Math.Round(seconds * rate)));
            if (frames < rate / 5)
                throw new InvalidOperationException("В буфере повтора пока почти ничего нет.");

            samples = new float[frames * 2];
            var start = (ringWritten - frames) % ringCapacityFrames;
            var first = (int)Math.Min(frames, ringCapacityFrames - start);
            Array.Copy(ring, start * 2, samples, 0, first * 2);
            if (frames > first)
                Array.Copy(ring, 0, samples, first * 2, (frames - first) * 2);
        }

        var path = UniquePath(folder, name ?? $"Повтор {DateTime.Now:HH-mm-ss}");
        await Task.Run(() => AudioClips.WriteWav(path, samples, rate)).ConfigureAwait(false);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            replayEnabled = false;
            recording = null;
        }

        var mic = micRecorder;
        micRecorder = null;
        mic?.Dispose();
        micDevice?.Dispose();
        await UpdateSystemCaptureAsync().ConfigureAwait(false);
    }

    // ───────── захват звука компьютера ─────────

    private async Task UpdateSystemCaptureAsync()
    {
        bool needed;
        lock (gate)
            needed = replayEnabled || recording == RecordingSource.System;

        if (needed && systemRecorder is null)
        {
            await Task.Run(StartSystemCaptureAsync).ConfigureAwait(false);
        }
        else if (!needed && systemRecorder is not null)
        {
            var rec = systemRecorder;
            var dev = systemDevice;
            lock (gate)
            {
                systemRecorder = null;
                systemDevice = null;
                ring = null;
                ringWritten = 0;
            }

            await Task.Run(() =>
            {
                rec.StopRecording();
                rec.Dispose();
                dev?.Dispose();
            }).ConfigureAwait(false);
        }

        lock (gate)
        {
            if (replayEnabled && ring is null && systemRate > 0 && systemRecorder is not null)
            {
                ringCapacityFrames = replaySeconds * systemRate;
                ring = new float[ringCapacityFrames * 2];
                ringWritten = 0;
            }
        }
    }

    private async Task StartSystemCaptureAsync()
    {
        WasapiRecorder? rec = null;
        MMDevice? device = null;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                // Всё, что звучит в системе, кроме процессов самого саундборда
                rec = await new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 2))
                    .BuildAsync().ConfigureAwait(false);
                ExcludesOwnSounds = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rec = null;
            LastError = $"Петля процессов недоступна ({ex.Message}), пишу звук устройства вывода.";
        }

        try
        {
            if (rec is null)
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                rec = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().Build();
                ExcludesOwnSounds = false;
            }

            var format = rec.WaveFormat;
            lock (gate)
            {
                systemFormat = format;
                systemRate = format.SampleRate;
                lastSystemTicks = clock();
                systemRecorder = rec;
                systemDevice = device;
            }

            rec.DataAvailable += OnSystemData;
            rec.StartRecording();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"Не удалось записывать звук компьютера: {ex.Message}";
            lock (gate)
            {
                systemRecorder = null;
                systemDevice = null;
            }

            rec?.Dispose();
            device?.Dispose();
        }
    }

    private void OnSystemData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
    {
        var now = clock();
        lock (gate)
        {
            if (systemFormat is null)
                return;
            var frames = StereoConverter.FramesIn(buffer.Length, systemFormat);
            EnsureScratch(frames);
            var stereo = convertScratch.AsSpan(0, frames * 2);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                stereo.Clear();
            else
                StereoConverter.Convert(buffer, systemFormat, stereo);

            // Пауза без пакетов (никто не играл) — досыпаем тишиной
            var elapsedFrames = (long)((now - lastSystemTicks) * (double)systemRate / ticksPerSecond);
            var gap = elapsedFrames - frames;
            if (gap > systemRate / 20)
                WriteSystem(new float[Math.Min(gap, (long)systemRate * 130) * 2]);
            lastSystemTicks = now;
            WriteSystem(stereo);
        }
    }

    /// <summary>Досыпать тишину за время без пакетов (под блокировкой).</summary>
    private void PadSilenceToNow()
    {
        if (systemRate == 0)
            return;
        var now = clock();
        var gap = (long)((now - lastSystemTicks) * (double)systemRate / ticksPerSecond);
        if (gap > systemRate / 20)
        {
            WriteSystem(new float[Math.Min(gap, (long)systemRate * 130) * 2]);
            lastSystemTicks = now;
        }
    }

    private void WriteSystem(ReadOnlySpan<float> stereo)
    {
        if (ring is not null)
        {
            var frames = stereo.Length / 2;
            var offset = 0;
            while (offset < frames)
            {
                var pos = (int)(ringWritten % ringCapacityFrames);
                var count = Math.Min(frames - offset, ringCapacityFrames - pos);
                stereo.Slice(offset * 2, count * 2).CopyTo(ring.AsSpan(pos * 2));
                ringWritten += count;
                offset += count;
            }
        }

        if (recording == RecordingSource.System)
            AppendRecording(stereo);
    }

    // ───────── микрофон ─────────

    private void StartMic(string? deviceId)
    {
        MMDevice? device = null;
        WasapiRecorder? rec = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                device = deviceId is null
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                    : enumerator.GetDevice(deviceId);
            }

            rec = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithBufferLength(100).Build();
            var format = rec.WaveFormat;
            lock (gate)
            {
                micFormat = format;
                recordingRate = format.SampleRate;
                micRecorder = rec;
                micDevice = device;
                recording = RecordingSource.Microphone;
            }

            rec.DataAvailable += (buffer, flags, _, _) =>
            {
                lock (gate)
                {
                    if (recording != RecordingSource.Microphone || micFormat is null)
                        return;
                    var frames = StereoConverter.FramesIn(buffer.Length, micFormat);
                    EnsureScratch(frames);
                    var stereo = convertScratch.AsSpan(0, frames * 2);
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        stereo.Clear();
                    else
                        StereoConverter.Convert(buffer, micFormat, stereo);
                    AppendRecording(stereo);
                }
            };
            rec.StartRecording();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (gate)
            {
                recording = null;
                micRecorder = null;
                micDevice = null;
            }

            rec?.Dispose();
            device?.Dispose();
            var denied = ex is UnauthorizedAccessException || (uint)ex.HResult == 0x80070005;
            throw new InvalidOperationException(denied
                ? "Доступ к микрофону запрещён в параметрах конфиденциальности Windows."
                : $"Не удалось начать запись с микрофона: {ex.Message}", ex);
        }
    }

    // ───────── общее ─────────

    private void AppendRecording(ReadOnlySpan<float> stereo)
    {
        if (recordingRate > 0 && recordedFrames >= (long)(MaxRecording.TotalSeconds * recordingRate))
            return;   // предел длины: дальше не пишем
        chunks.Add(stereo.ToArray());
        recordedFrames += stereo.Length / 2;
        for (var i = 0; i < stereo.Length; i++)
        {
            var a = Math.Abs(stereo[i]);
            if (a > recordingPeak)
                recordingPeak = a;
        }
    }

    private void EnsureScratch(int frames)
    {
        if (convertScratch.Length < frames * 2)
            convertScratch = new float[frames * 2];
    }

    private static string UniquePath(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        var candidate = Path.Combine(folder, name + ".wav");
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{name} ({i}).wav");
        return candidate;
    }
}

/// <summary>Байты захвата (float32 / PCM 16/24/32, любые каналы) → стерео float.</summary>
internal static class StereoConverter
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static int FramesIn(int bytes, WaveFormat format) => format.BlockAlign == 0 ? 0 : bytes / format.BlockAlign;

    public static void Convert(ReadOnlySpan<byte> source, WaveFormat format, Span<float> stereo)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        var bytesPerSample = format.BitsPerSample / 8;
        var channels = format.Channels;
        var frames = FramesIn(source.Length, format);
        for (var f = 0; f < frames; f++)
        {
            var o = f * format.BlockAlign;
            var left = Sample(source, o, isFloat, bytesPerSample);
            var right = channels > 1 ? Sample(source, o + bytesPerSample, isFloat, bytesPerSample) : left;
            stereo[2 * f] = left;
            stereo[(2 * f) + 1] = right;
        }
    }

    private static float Sample(ReadOnlySpan<byte> b, int o, bool isFloat, int bytes) => (isFloat, bytes) switch
    {
        (true, 4) => BitConverter.ToSingle(b.Slice(o, 4)),
        (false, 2) => BitConverter.ToInt16(b.Slice(o, 2)) / 32768f,
        (false, 3) => (b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16)) / 8388608f,
        (false, 4) => BitConverter.ToInt32(b.Slice(o, 4)) / 2147483648f,
        _ => 0f,
    };
}
