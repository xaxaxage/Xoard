using System.Runtime.CompilerServices;

namespace Soundboard.Core.Audio.Buffers;

/// <summary>
/// Кольцевой буфер float-кадров: один писатель и один читатель (SPSC), без блокировок и выделений.
/// Счётчики 64-битные и только растут, поэтому «сколько записано/прочитано» не переполняется.
/// Если места нет, писатель отбрасывает новые данные (читатель потом догонит, пропустив старые).
/// </summary>
internal sealed class FloatRing
{
    private readonly float[] data;
    private readonly int mask;
    private readonly int channels;
    private long written;   // в сэмплах; пишет только писатель
    private long read;      // в сэмплах; пишет только читатель

    public FloatRing(int capacityFrames, int channels = AudioFormat.Channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        this.channels = channels;
        var samples = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(capacityFrames * channels));
        data = new float[samples];
        mask = samples - 1;
    }

    /// <summary>Ёмкость в кадрах.</summary>
    public int CapacityFrames => data.Length / channels;

    /// <summary>Всего записано кадров (с начала жизни буфера).</summary>
    public long FramesWritten => Volatile.Read(ref written) / channels;

    /// <summary>Всего прочитано (или пропущено) кадров.</summary>
    public long FramesRead => Volatile.Read(ref read) / channels;

    /// <summary>Сколько кадров можно прочитать прямо сейчас.</summary>
    public int AvailableFrames => (int)((Volatile.Read(ref written) - Volatile.Read(ref read)) / channels);

    /// <summary>Записывает кадры (писатель). Возвращает число записанных кадров (меньше при переполнении).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Write(ReadOnlySpan<float> interleaved)
    {
        var w = written;
        var free = data.Length - (int)(w - Volatile.Read(ref read));
        var count = Math.Min(interleaved.Length, free);
        count -= count % channels;
        if (count <= 0)
            return 0;

        var start = (int)(w & mask);
        var first = Math.Min(count, data.Length - start);
        interleaved[..first].CopyTo(data.AsSpan(start));
        if (count > first)
            interleaved[first..count].CopyTo(data);

        Volatile.Write(ref written, w + count);
        return count / channels;
    }

    /// <summary>Читает до <paramref name="destination"/>.Length сэмплов (читатель). Возвращает число кадров.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Read(Span<float> destination)
    {
        var r = read;
        var available = (int)(Volatile.Read(ref written) - r);
        var count = Math.Min(destination.Length, available);
        count -= count % channels;
        if (count <= 0)
            return 0;

        var start = (int)(r & mask);
        var first = Math.Min(count, data.Length - start);
        data.AsSpan(start, first).CopyTo(destination);
        if (count > first)
            data.AsSpan(0, count - first).CopyTo(destination[first..]);

        Volatile.Write(ref read, r + count);
        return count / channels;
    }

    /// <summary>Пропускает до <paramref name="frames"/> кадров (читатель). Возвращает число пропущенных.</summary>
    public int Skip(int frames)
    {
        var r = read;
        var available = (int)(Volatile.Read(ref written) - r) / channels;
        var count = Math.Clamp(frames, 0, available);
        Volatile.Write(ref read, r + ((long)count * channels));
        return count;
    }
}
