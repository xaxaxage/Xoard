using Xoard.Core.Audio;

namespace Xoard.Core.Recording;

/// <summary>
/// Звук одной дорожки на общей шкале времени (кадры 48 кГц): кольцо повтора и, пока идёт запись, куски
/// записи. Источники ДОБАВЛЯЮТ звук в свои места шкалы — так «Игра» складывается из нескольких программ.
/// Хранение — стерео 16 бит. Не потокобезопасно: вызывающий держит блокировку.
/// </summary>
internal sealed class TrackTimeline
{
    private const int ChunkFrames = AudioFormat.SampleRate;   // кусок записи — 1 с
    private const float ToShort = 32767f;
    private const float FromShort = 1f / 32768f;

    private short[]? ring;
    private int capacity;
    private long prepared;          // кольцо готово (обнулено и дописано) до этого кадра, не включая
    private List<short[]?>? chunks;
    private long recordStart;
    private long recordLimit;

    /// <summary>Кольцо повтора есть.</summary>
    public bool HasRing => ring is not null;

    /// <summary>Идёт запись.</summary>
    public bool IsRecording => chunks is not null;

    /// <summary>Пересоздать кольцо повтора (0 — убрать). До <paramref name="now"/> в нём тишина.</summary>
    public void SetRing(int capacityFrames, long now)
    {
        ring = capacityFrames > 0 ? new short[capacityFrames * 2] : null;
        capacity = capacityFrames;
        prepared = now;
    }

    /// <summary>Начать запись с кадра <paramref name="start"/>; больше <paramref name="maxFrames"/> не пишется.</summary>
    public void StartRecording(long start, long maxFrames)
    {
        chunks = [];
        recordStart = start;
        recordLimit = start + maxFrames;
    }

    /// <summary>Закончить запись на кадре <paramref name="end"/> и отдать её (стерео 16 бит).</summary>
    public short[] StopRecording(long end)
    {
        var frames = (int)Math.Clamp(end - recordStart, 0, recordLimit - recordStart);
        var result = new short[frames * 2];
        if (chunks is not null)
        {
            for (var c = 0; c < chunks.Count; c++)
            {
                if (chunks[c] is not { } chunk)
                    continue;
                var offset = c * ChunkFrames;
                var count = Math.Min(ChunkFrames, frames - offset);
                if (count > 0)
                    chunk.AsSpan(0, count * 2).CopyTo(result.AsSpan(offset * 2));
            }
        }

        chunks = null;
        return result;
    }

    /// <summary>Добавить стерео float в кадры [pos, pos + n) шкалы.</summary>
    public void Add(long pos, ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        if (frames == 0)
            return;
        if (ring is not null)
            AddToRing(pos, stereo, frames);
        if (chunks is not null)
            AddToRecording(pos, stereo, frames);
    }

    /// <summary>Прочитать кадры [start, start + n) из кольца; чего нет (давно или ещё не было) — тишина.</summary>
    public void Read(long start, Span<short> stereo)
    {
        stereo.Clear();
        if (ring is null)
            return;
        var frames = stereo.Length / 2;
        var from = Math.Max(start, prepared - capacity);
        var to = Math.Min(start + frames, prepared);
        for (var p = from; p < to; p++)
        {
            var i = Index(p);
            var o = (int)(p - start) * 2;
            stereo[o] = ring[i];
            stereo[o + 1] = ring[i + 1];
        }
    }

    /// <summary>Пик и средняя громкость (RMS) кадров [start, start + n) кольца, 0..1.</summary>
    public (float Peak, float Rms) Measure(long start, int frames)
    {
        if (ring is null || frames <= 0)
            return (0, 0);
        var from = Math.Max(start, prepared - capacity);
        var to = Math.Min(start + frames, prepared);
        var peak = 0;
        double sum = 0;
        for (var p = from; p < to; p++)
        {
            var i = Index(p);
            int l = ring[i], r = ring[i + 1];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            sum += ((l * (double)l) + (r * (double)r)) * 0.5;
        }

        return (Math.Min(1f, peak * FromShort), (float)Math.Min(1.0, Math.Sqrt(sum / frames) * FromShort));
    }

    private void AddToRing(long pos, ReadOnlySpan<float> stereo, int frames)
    {
        var end = pos + frames;
        if (end > prepared)
        {
            // Новое место шкалы: сначала тишина (в кольце там лежит звук «круг назад»)
            for (var p = Math.Max(prepared, end - capacity); p < end; p++)
            {
                var i = Index(p);
                ring![i] = 0;
                ring[i + 1] = 0;
            }

            prepared = end;
        }

        for (var p = Math.Max(pos, prepared - capacity); p < end; p++)
        {
            var i = Index(p);
            var s = (int)(p - pos) * 2;
            ring![i] = Saturate(ring[i] + (stereo[s] * ToShort));
            ring[i + 1] = Saturate(ring[i + 1] + (stereo[s + 1] * ToShort));
        }
    }

    private void AddToRecording(long pos, ReadOnlySpan<float> stereo, int frames)
    {
        var from = Math.Max(pos, recordStart);
        var to = Math.Min(pos + frames, recordLimit);
        for (var p = from; p < to; p++)
        {
            var index = p - recordStart;
            var c = (int)(index / ChunkFrames);
            while (chunks!.Count <= c)
                chunks.Add(null);
            var chunk = chunks[c] ??= new short[ChunkFrames * 2];
            var i = (int)(index % ChunkFrames) * 2;
            var s = (int)(p - pos) * 2;
            chunk[i] = Saturate(chunk[i] + (stereo[s] * ToShort));
            chunk[i + 1] = Saturate(chunk[i + 1] + (stereo[s + 1] * ToShort));
        }
    }

    private int Index(long pos) => (int)(((pos % capacity) + capacity) % capacity) * 2;

    private static short Saturate(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
}

/// <summary>
/// Ставит пакеты одного источника на шкалу времени: подряд, пока они идут подряд, а после паузы
/// (в тишине Windows пакетов не присылает) или заметного ухода часов — заново, по времени прихода.
/// Так пакеты не «дрожат» вместе со временем прихода, а паузы заполняются тишиной.
/// </summary>
internal sealed class SourcePlacer
{
    /// <summary>Источник отстал от часов больше чем на это (пауза) — ставим заново.</summary>
    public const int LateTolerance = AudioFormat.SampleRate / 20;

    /// <summary>Источник убежал вперёд часов больше чем на это — ставим заново.</summary>
    public const int EarlyTolerance = AudioFormat.SampleRate / 5;

    private long next = long.MinValue;

    /// <summary>
    /// Место пакета из <paramref name="frames"/> кадров, пришедшего в момент <paramref name="arrival"/>
    /// (кадр шкалы); <paramref name="delayFrames"/> — сколько звук уже задержан до прихода.
    /// </summary>
    public long Place(long arrival, int frames, int delayFrames = 0)
    {
        var expected = arrival - frames - delayFrames;
        if (next == long.MinValue || expected - next > LateTolerance || next - expected > EarlyTolerance)
            next = expected;
        var pos = next;
        next += frames;
        return pos;
    }

    public void Reset() => next = long.MinValue;
}
