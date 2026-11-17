namespace Xoard.Core.Recording;

/// <summary>
/// Пики звука по коротким отрезкам (кольцо фиксированной длины) — для живой волны
/// «последние 30 секунд» в окне. Пишет поток захвата, читает поток интерфейса.
/// </summary>
public sealed class PeakHistory
{
    private readonly float[] ring;
    private readonly Lock gate = new();
    private long written;

    public PeakHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ring = new float[capacity];
    }

    public int Capacity => ring.Length;

    /// <summary>Сколько отрезков записано за всё время (для плавной прокрутки: изменилось — пришёл новый).</summary>
    public long Written
    {
        get
        {
            lock (gate)
                return written;
        }
    }

    /// <summary>Сколько отрезков записано (не больше ёмкости).</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return (int)Math.Min(written, ring.Length);
        }
    }

    public void Add(float peak)
    {
        lock (gate)
        {
            ring[(int)(written % ring.Length)] = peak;
            written++;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            written = 0;
            Array.Clear(ring);
        }
    }

    /// <summary>
    /// Последние <c>dest.Length</c> пиков, самый новый — в конце. Если записано меньше,
    /// начало заполняется нулями (тишина). Возвращает, сколько пиков настоящих.
    /// </summary>
    public int CopyLatest(Span<float> dest)
    {
        lock (gate)
        {
            var available = (int)Math.Min(Math.Min(written, ring.Length), dest.Length);
            var pad = dest.Length - available;
            dest[..pad].Clear();
            for (var i = 0; i < available; i++)
                dest[pad + i] = ring[(int)((written - available + i) % ring.Length)];
            return available;
        }
    }
}
