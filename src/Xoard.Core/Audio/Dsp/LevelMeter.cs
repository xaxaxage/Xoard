using System.Runtime.CompilerServices;

namespace Xoard.Core.Audio.Dsp;

/// <summary>Показание измерителя: линейные пик и RMS (0..1 для нормального сигнала).</summary>
/// <param name="Peak">Максимум модуля сэмпла.</param>
/// <param name="Rms">Среднеквадратичное значение по всем сэмплам блока (все каналы вместе).</param>
internal readonly record struct LevelReading(float Peak, float Rms)
{
    /// <summary>Пик в dBFS (не ниже <see cref="Decibels.Floor"/>).</summary>
    public float PeakDb => Decibels.FromLinear(Peak);

    /// <summary>RMS в dBFS (не ниже <see cref="Decibels.Floor"/>).</summary>
    public float RmsDb => Decibels.FromLinear(Rms);
}

/// <summary>
/// Измеритель уровня шины или микрофона. Аудиопоток вызывает <see cref="Process"/> на каждый
/// блок: пик и RMS блока публикуются через Volatile (<see cref="LastBlock"/>), а максимумы
/// с момента последнего чтения копятся lock-free (CAS). Читатель (управляющий поток, снимок
/// статуса) забирает их через <see cref="TakeMax"/> — Interlocked.Exchange со сбросом в 0,
/// так что короткий пик между двумя чтениями не теряется. Читатель предполагается один:
/// каждый вызов <see cref="TakeMax"/> забирает накопленное себе.
/// Блоки с NaN/∞ не портят показания: такие сэмплы пропускаются, блок учитывается в
/// <see cref="NonFiniteBlocks"/>. Без аллокаций и блокировок.
/// </summary>
internal sealed class LevelMeter
{
    private float lastPeak;
    private float lastRms;
    private float maxPeak;
    private float maxRms;
    private long blocks;
    private long nonFiniteBlocks;

    /// <summary>Показание последнего обработанного блока (любой поток).</summary>
    public LevelReading LastBlock => new(Volatile.Read(ref lastPeak), Volatile.Read(ref lastRms));

    /// <summary>Сколько блоков обработано (любой поток).</summary>
    public long Blocks => Volatile.Read(ref blocks);

    /// <summary>Сколько блоков содержали NaN/∞ (любой поток).</summary>
    public long NonFiniteBlocks => Volatile.Read(ref nonFiniteBlocks);

    /// <summary>
    /// Измеряет блок и публикует результат (только аудиопоток, один писатель).
    /// Пустой блок игнорируется. Возвращает показание этого блока.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public LevelReading Process(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return default;

        float peak;
        float rms;
        var sumOfSquares = SampleOps.SumOfSquares(samples);
        if (float.IsFinite(sumOfSquares))
        {
            peak = SampleOps.PeakAbs(samples);
            rms = MathF.Sqrt(sumOfSquares / samples.Length);
        }
        else
        {
            // NaN/∞ или переполнение суммы квадратов: медленный точный путь.
            if (MeasureSlow(samples, out peak, out rms))
                Volatile.Write(ref nonFiniteBlocks, nonFiniteBlocks + 1);
        }

        Volatile.Write(ref lastPeak, peak);
        Volatile.Write(ref lastRms, rms);
        AtomicFloat.RaiseMax(ref maxPeak, peak);
        AtomicFloat.RaiseMax(ref maxRms, rms);
        Volatile.Write(ref blocks, blocks + 1);
        return new LevelReading(peak, rms);
    }

    /// <summary>
    /// Максимальные пик и RMS блоков с момента предыдущего вызова (любой поток); сбрасывает
    /// накопление. Если блоков не было — нули.
    /// </summary>
    public LevelReading TakeMax() => new(AtomicFloat.Take(ref maxPeak), AtomicFloat.Take(ref maxRms));

    /// <summary>Сбрасывает показания и счётчики. Только когда <see cref="Process"/> не выполняется.</summary>
    public void Reset()
    {
        Volatile.Write(ref lastPeak, 0f);
        Volatile.Write(ref lastRms, 0f);
        Volatile.Write(ref maxPeak, 0f);
        Volatile.Write(ref maxRms, 0f);
        Volatile.Write(ref blocks, 0);
        Volatile.Write(ref nonFiniteBlocks, 0);
    }

    /// <summary>Точное измерение в double, пропуская нечисловые сэмплы. true — если такие были.</summary>
    private static bool MeasureSlow(ReadOnlySpan<float> samples, out float peak, out float rms)
    {
        var sum = 0d;
        var max = 0f;
        var finite = 0;
        foreach (var x in samples)
        {
            if (!float.IsFinite(x))
                continue;
            finite++;
            var a = MathF.Abs(x);
            if (a > max)
                max = a;
            sum += (double)x * x;
        }

        peak = max;
        rms = finite == 0 ? 0f : (float)Math.Sqrt(sum / finite);
        return finite != samples.Length;
    }
}
