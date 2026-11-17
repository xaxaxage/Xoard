using System.Diagnostics;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xoard.Core.Audio.Buffers;
using Xoard.Core.Audio.Dsp;

namespace Xoard.Core.Audio.Mic;

/// <summary>
/// Передача голоса с микрофона в шину VB-CABLE с компенсацией дрейфа часов двух устройств.
/// Поток захвата пишет в кольцо (<see cref="OnPacket"/>), поток рендера VB-CABLE забирает
/// (<see cref="MixInto"/>) через ресемплер с коэффициентом от <see cref="DriftController"/>.
/// Между потоками — только SPSC-кольцо и «штамп» последнего пакета под seqlock: без блокировок.
/// </summary>
/// <remarks>
/// Заполнение оценивается по времени ПРИХОДА пакетов (ревью M3), а не по QPC устройства:
/// W(сейчас) = записано_к_последнему_пакету + min((сейчас − приход)·fs, размер_пакета).
/// Это убирает «пилу» от пакетного прихода без доверия к меткам драйвера.
/// </remarks>
internal sealed class MicPassthrough
{
    private const int RingFrames = 16_384;                 // ≈ 341 мс
    private const int MaxBlockFrames = 8_192;
    private const int FadeInFrames = AudioFormat.SampleRate / 200;    // 5 мс
    private const int FadeOutFrames = AudioFormat.SampleRate / 500;   // 2 мс

    private readonly FloatRing ring = new(RingFrames);
    private readonly PolyphaseResampler resampler = new(AudioFormat.Channels, MaxBlockFrames);
    private readonly DriftController drift = new();
    private readonly SmoothedGain gain;
    private readonly LevelMeter inputMeter = new();
    private readonly LevelMeter outputMeter = new();
    private readonly float[] captureScratch = new float[MaxBlockFrames * AudioFormat.Channels];
    private readonly float[] ringScratch = new float[(MaxBlockFrames + 64) * AudioFormat.Channels];
    private readonly float[] outScratch = new float[MaxBlockFrames * AudioFormat.Channels];
    private readonly long ticksPerSecond;
    private readonly Func<long> clock;

    // Штамп последнего пакета (seqlock): нечётный seq — запись в процессе
    private int stampSeq;
    private long stampFrames;
    private long stampTicks;
    private int stampPacketFrames;

    private CaptureConverter? converter;    // меняет только управляющий поток при остановленном захвате
    private long staleBefore;               // кадры, записанные до подключения источника (остатки прошлой сессии)
    private int targetFrames;
    private int resyncRequested = 1;
    private int fadeInLeft;
    private float levelGain = 1f;
    private bool muted;
    private double warmStartPpm;

    private long underruns;
    private long overflows;
    private long resyncs;
    private long discontinuities;
    private long droppedFrames;
    private long packets;
    private double lastFillFrames;

    public MicPassthrough(int targetFillMs = 30, int rampMs = 20)
        : this(targetFillMs, rampMs, Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    /// <summary>Конструктор для тестов: собственные часы (тики) вместо <see cref="Stopwatch"/>.</summary>
    internal MicPassthrough(int targetFillMs, int rampMs, Func<long> clock, long ticksPerSecond)
    {
        this.clock = clock;
        this.ticksPerSecond = ticksPerSecond;
        TargetFillMs = targetFillMs;
        gain = SmoothedGain.FromMilliseconds(rampMs, 1f);
    }

    /// <summary>Целевое заполнение буфера (задержка), мс.</summary>
    public int TargetFillMs
    {
        get => targetFrames * 1000 / AudioFormat.SampleRate;
        set
        {
            Volatile.Write(ref targetFrames, Math.Clamp(value, 10, 200) * AudioFormat.SampleRate / 1000);
            RequestResync();
        }
    }

    /// <summary>Источник подключён (захват идёт).</summary>
    public bool IsAttached => Volatile.Read(ref converter) is not null;

    public long Underruns => Interlocked.Read(ref underruns);

    public long Overflows => Interlocked.Read(ref overflows);

    public long Resyncs => Interlocked.Read(ref resyncs);

    public long Discontinuities => Interlocked.Read(ref discontinuities);

    public long DroppedFrames => Interlocked.Read(ref droppedFrames);

    public long Packets => Interlocked.Read(ref packets);

    /// <summary>Тик последнего пакета (часы движка, обычно <see cref="Stopwatch.GetTimestamp"/>), 0 — ещё не было.</summary>
    public long LastPacketTicks => Volatile.Read(ref stampTicks);

    public double DriftPpm => drift.EstimatedPpm;

    public bool Locked => drift.Locked;

    public double FillMs => Volatile.Read(ref lastFillFrames) * 1000.0 / AudioFormat.SampleRate;

    public LevelMeter InputMeter => inputMeter;

    public LevelMeter OutputMeter => outputMeter;

    /// <summary>Громкость голоса (линейная) и мягкое выключение. Можно звать с любого потока.</summary>
    public void SetGain(float linear, bool isMuted)
    {
        Volatile.Write(ref levelGain, linear);
        Volatile.Write(ref muted, isMuted);
        gain.SetTarget(isMuted ? 0f : linear);
    }

    /// <summary>
    /// Подключает новый источник (управляющий поток, захват ещё не запущен).
    /// <paramref name="ppmHint"/> — известный дрейф этой пары устройств для быстрого захвата.
    /// </summary>
    public void Attach(WaveFormat captureFormat, MicChannelMode mode, double ppmHint = 0)
    {
        warmStartPpm = ppmHint;
        // Захват остановлен — счётчик записи стабилен; всё до него читатель выбросит
        Volatile.Write(ref staleBefore, ring.FramesWritten);
        Volatile.Write(ref converter, new CaptureConverter(captureFormat, mode));
        RequestResync();
    }

    /// <summary>Отключает источник (после остановки захвата).</summary>
    public void Detach()
    {
        Volatile.Write(ref converter, null);
        RequestResync();
    }

    /// <summary>Запросить пересинхронизацию (тихий перезапуск буфера).</summary>
    public void RequestResync() => Volatile.Write(ref resyncRequested, 1);

    /// <summary>Поток захвата: пакет от WASAPI.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void OnPacket(ReadOnlySpan<byte> data, AudioClientBufferFlags flags)
    {
        var now = clock();
        var conv = Volatile.Read(ref converter);
        if (conv is null)
            return;

        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            Interlocked.Increment(ref discontinuities);

        var totalFrames = conv.FramesIn(data.Length);
        var silent = (flags & AudioClientBufferFlags.Silent) != 0;
        var offsetBytes = 0;
        var remaining = totalFrames;
        while (remaining > 0)
        {
            var frames = Math.Min(remaining, MaxBlockFrames);
            var dest = captureScratch.AsSpan(0, frames * AudioFormat.Channels);
            if (silent)
                dest.Clear();
            else
                conv.Convert(data.Slice(offsetBytes, frames * conv.BytesPerFrame), dest);

            inputMeter.Process(dest);
            var written = ring.Write(dest);
            if (written < frames)
                Interlocked.Add(ref droppedFrames, frames - written);

            offsetBytes += frames * conv.BytesPerFrame;
            remaining -= frames;
        }

        // Публикуем штамп: сколько записано и когда пришёл пакет
        Interlocked.Increment(ref stampSeq);
        Volatile.Write(ref stampFrames, ring.FramesWritten);
        Volatile.Write(ref stampTicks, now);
        Volatile.Write(ref stampPacketFrames, totalFrames);
        Interlocked.Increment(ref stampSeq);
        Interlocked.Increment(ref packets);
    }

    /// <summary>Поток рендера VB-CABLE: подмешать голос в блок шины.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void MixInto(Span<float> bus)
    {
        var frames = Math.Min(bus.Length / AudioFormat.Channels, MaxBlockFrames);
        if (frames == 0 || Volatile.Read(ref converter) is null)
            return;

        if (!TryReadStamp(out var written, out var ticks, out var packetFrames) || written == 0)
            return;

        var target = Volatile.Read(ref targetFrames);
        var output = outScratch.AsSpan(0, frames * AudioFormat.Channels);

        if (Volatile.Read(ref resyncRequested) != 0)
        {
            // Остатки прошлой сессии (до переподключения микрофона) не играем
            var stale = Volatile.Read(ref staleBefore) - ring.FramesRead;
            if (stale > 0)
                ring.Skip((int)Math.Min(stale, int.MaxValue));

            // Ждём, пока наберётся целевое заполнение, лишнее выбрасываем — затем мягко входим
            var available = ring.AvailableFrames;
            if (available < target)
                return;
            ring.Skip(available - target);
            resampler.Reset();
            drift.Reset(warmStartPpm);
            fadeInLeft = FadeInFrames;
            Volatile.Write(ref resyncRequested, 0);
            Interlocked.Increment(ref resyncs);
        }

        var now = clock();
        var sincePacket = Math.Clamp((now - ticks) * (double)AudioFormat.SampleRate / ticksPerSecond, 0, packetFrames);
        var fill = (written + sincePacket - ring.FramesRead) + resampler.PendingInputFrames;
        Volatile.Write(ref lastFillFrames, fill);

        // Переполнение (после зависания рендера): плавно выходим и пересинхронизируемся
        if (fill > target + (AudioFormat.SampleRate * 60 / 1000))
        {
            Interlocked.Increment(ref overflows);
            ProduceTail(output, frames);
            Volatile.Write(ref resyncRequested, 1);
            Finish(output, bus);
            return;
        }

        var ratio = drift.Update(fill - target, frames);
        var need = resampler.InputFramesNeeded(frames, ratio);
        var have = ring.AvailableFrames;
        if (have < need)
        {
            // Недогрузка: отдаём что есть, дополняем тишиной, плавно гасим и ждём заполнения
            Interlocked.Increment(ref underruns);
            Feed(have);
            resampler.WriteSilence(need - have);
            resampler.Process(output, ratio);
            Fade.FadeOut(output, AudioFormat.Channels, 0, Math.Min(frames, FadeOutFrames));
            if (frames > FadeOutFrames)
                output[(FadeOutFrames * AudioFormat.Channels)..].Clear();
            Volatile.Write(ref resyncRequested, 1);
            Finish(output, bus);
            return;
        }

        Feed(need);
        resampler.Process(output, ratio);

        if (fadeInLeft > 0)
        {
            var position = FadeInFrames - fadeInLeft;
            var done = Fade.FadeIn(output, AudioFormat.Channels, position, FadeInFrames);
            fadeInLeft = Math.Max(0, fadeInLeft - done);
        }

        Finish(output, bus);
    }

    private void Finish(Span<float> output, Span<float> bus)
    {
        gain.Apply(output);
        outputMeter.Process(output);
        SampleOps.Add(output, bus[..output.Length]);
    }

    private void ProduceTail(Span<float> output, int frames)
    {
        // Доигрываем то, что уже в ресемплере, с затуханием
        resampler.Process(output, 1.0);
        Fade.FadeOut(output, AudioFormat.Channels, 0, Math.Min(frames, FadeOutFrames));
        if (frames > FadeOutFrames)
            output[(FadeOutFrames * AudioFormat.Channels)..].Clear();
    }

    private void Feed(int frames)
    {
        while (frames > 0)
        {
            var chunk = Math.Min(frames, ringScratch.Length / AudioFormat.Channels);
            var got = ring.Read(ringScratch.AsSpan(0, chunk * AudioFormat.Channels));
            if (got == 0)
                return;
            resampler.Write(ringScratch.AsSpan(0, got * AudioFormat.Channels));
            frames -= got;
        }
    }

    private bool TryReadStamp(out long written, out long ticks, out int packetFrames)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var seq1 = Volatile.Read(ref stampSeq);
            if ((seq1 & 1) != 0)
            {
                Thread.SpinWait(8);
                continue;
            }

            written = Volatile.Read(ref stampFrames);
            ticks = Volatile.Read(ref stampTicks);
            packetFrames = Volatile.Read(ref stampPacketFrames);
            if (Volatile.Read(ref stampSeq) == seq1)
                return true;
        }

        written = 0;
        ticks = 0;
        packetFrames = 0;
        return false;
    }
}
