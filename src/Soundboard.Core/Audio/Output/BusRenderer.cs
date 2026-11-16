using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Audio.Dsp;
using Soundboard.Core.Audio.Mic;
using Soundboard.Core.Audio.Voices;

namespace Soundboard.Core.Audio.Output;

/// <summary>Шина вывода.</summary>
public enum BusId
{
    /// <summary>Наушники: только звуки саундборда.</summary>
    Headphones = 0,

    /// <summary>VB-CABLE (CABLE Input): звуки + голос с микрофона.</summary>
    Cable = 1,
}

/// <summary>
/// Звуковой конвейер одной шины. <see cref="Read"/> зовёт только поток воспроизведения WASAPI:
/// новые голоса → MixingSampleProvider → (голос, только VB-CABLE) → громкость шины → лимитер →
/// защита от NaN → измеритель. Всегда возвращает полный блок: поток VB-CABLE не должен
/// останавливаться (иначе кабель теряет начало и конец звуков, см. этап 0).
/// </summary>
internal sealed class BusRenderer : ISampleProvider
{
    /// <summary>Предел очереди новых голосов (защита от флуда командами).</summary>
    public const int MaxPendingTaps = 512;

    private readonly ConcurrentQueue<VoiceTap> pending = new();
    private readonly MixingSampleProvider mixer = new(AudioFormat.WaveFormat) { ReadFully = true };
    private readonly SmoothedGain gain;
    private readonly BusLimiter limiter;
    private readonly LevelMeter meter = new();
    private readonly MicPassthrough? mic;

    private long framesRendered;
    private long callbacks;
    private long errors;
    private int firstRequestFrames;
    private long nearEmpty;
    private int minRequestFrames = int.MaxValue;
    private int maxRequestFrames;
    private long lastCallbackTicks;
    private int audioThreadPriority = int.MinValue;
    private long allocatedAtStart = -1;
    private long allocatedBytes;
    private Exception? lastError;

    public BusRenderer(BusId id, float initialGain, int rampMs, MicPassthrough? mic = null)
    {
        if (mic is not null && id != BusId.Cable)
            throw new ArgumentException("Голос с микрофона идёт только в шину VB-CABLE.", nameof(mic));
        Id = id;
        this.mic = mic;
        gain = SmoothedGain.FromMilliseconds(rampMs, initialGain);
        limiter = new BusLimiter();
    }

    public BusId Id { get; }

    public WaveFormat WaveFormat => AudioFormat.WaveFormat;

    public long FramesRendered => Interlocked.Read(ref framesRendered);

    public long Callbacks => Interlocked.Read(ref callbacks);

    public long Errors => Interlocked.Read(ref errors);

    /// <summary>Сколько раз буфер устройства почти опустел (запрос ≈ весь буфер) — предвестник щелчка.</summary>
    public long NearEmpty => Interlocked.Read(ref nearEmpty);

    public int MinRequestFrames => Volatile.Read(ref minRequestFrames);

    public int MaxRequestFrames => Volatile.Read(ref maxRequestFrames);

    public long LastCallbackTicks => Volatile.Read(ref lastCallbackTicks);

    /// <summary>Приоритет звукового потока (GetThreadPriority) при первом вызове; int.MinValue — ещё не было.</summary>
    public int AudioThreadPriority => Volatile.Read(ref audioThreadPriority);

    /// <summary>Выделено байт на звуковом потоке после прогрева (должно быть 0).</summary>
    public long AudioThreadAllocatedBytes => Interlocked.Read(ref allocatedBytes);

    public Exception? LastError => Volatile.Read(ref lastError);

    public LevelMeter Meter => meter;

    public BusLimiter Limiter => limiter;

    public float GainTarget => gain.Target;

    /// <summary>Добавить голос (любой поток). false — очередь переполнена.</summary>
    public bool Enqueue(VoiceTap tap)
    {
        if (pending.Count >= MaxPendingTaps)
            return false;
        pending.Enqueue(tap);
        return true;
    }

    /// <summary>Громкость шины (линейная), плавно (любой поток).</summary>
    public void SetGain(float linear) => gain.SetTarget(linear);

    /// <summary>
    /// Прогрев перед первым подключением (управляющий поток): JIT, ёмкость внутреннего списка
    /// микшера и его буфера — чтобы на звуковом потоке не было выделений.
    /// </summary>
    public void Warmup()
    {
        var dummy = new float[9600 * AudioFormat.Channels];
        // NAudio 3 не принимает один и тот же вход дважды — нужны разные объекты
        for (var i = 0; i < 256; i++)
            mixer.AddMixerInput(new SilenceProvider(AudioFormat.WaveFormat).ToSampleProvider());
        mixer.Read(dummy);
        mixer.RemoveAllMixerInputs();
        limiter.Process(dummy);
        limiter.Reset();
        meter.Reset();
        firstRequestFrames = 0;
    }

    /// <summary>Сбросить счётчики запросов при подключении нового потока (управляющий поток, поток не читает).</summary>
    public void OnStreamAttached()
    {
        firstRequestFrames = 0;
        Volatile.Write(ref audioThreadPriority, int.MinValue);
        allocatedAtStart = -1;
    }

    /// <summary>
    /// Завершить все голоса шины (только когда никакой поток воспроизведения её не читает).
    /// </summary>
    public void EndAllDetached(PlaybackEndReason reason)
    {
        while (pending.TryDequeue(out var tap))
            tap.ForceEnd(reason);
        foreach (var input in mixer.MixerInputs.ToList())
        {
            if (input is VoiceTap tap)
                tap.ForceEnd(reason);
        }

        mixer.RemoveAllMixerInputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / AudioFormat.Channels;
        Volatile.Write(ref lastCallbackTicks, Stopwatch.GetTimestamp());
        TrackRequest(frames);

        try
        {
            while (pending.TryDequeue(out var tap))
                mixer.AddMixerInput(tap);

            var read = mixer.Read(buffer);
            if (read < buffer.Length)
                buffer[read..].Clear();

            mic?.MixInto(buffer);
            gain.Apply(buffer);
            limiter.Process(buffer);
            meter.Process(buffer);
        }
        catch (Exception ex)
        {
            // Ошибка не должна убить поток: тишина, счётчик, голоса шины — завершены с ошибкой
            buffer.Clear();
            Interlocked.Increment(ref errors);
            Volatile.Write(ref lastError, ex);
            try
            {
                foreach (var input in mixer.MixerInputs.ToList())
                {
                    if (input is VoiceTap tap)
                        tap.ForceEnd(PlaybackEndReason.Error);
                }

                mixer.RemoveAllMixerInputs();
            }
            catch (InvalidOperationException)
            {
                // Список мог поменяться — голоса уйдут при следующем чтении
            }
        }

        Interlocked.Add(ref framesRendered, frames);
        Interlocked.Increment(ref callbacks);
        TrackAllocations();
        return buffer.Length;
    }

    private void TrackRequest(int frames)
    {
        if (firstRequestFrames == 0)
        {
            // Первый запрос WASAPI — весь буфер устройства; запоминаем приоритет звукового потока
            firstRequestFrames = frames;
            Volatile.Write(ref audioThreadPriority, NativeMethods.GetThreadPriority(NativeMethods.GetCurrentThread()));
        }
        else if (frames >= firstRequestFrames - 16)
        {
            Interlocked.Increment(ref nearEmpty);
        }

        if (frames < minRequestFrames)
            Volatile.Write(ref minRequestFrames, frames);
        if (frames > maxRequestFrames)
            Volatile.Write(ref maxRequestFrames, frames);
    }

    private void TrackAllocations()
    {
        var now = GC.GetAllocatedBytesForCurrentThread();
        var calls = callbacks;
        if (calls == 50)
            allocatedAtStart = now;        // прогрев: первые 50 блоков не считаем
        else if (calls > 50 && allocatedAtStart >= 0)
            Volatile.Write(ref allocatedBytes, now - allocatedAtStart);
    }
}
