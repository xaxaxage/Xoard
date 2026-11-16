using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Soundboard.Core.Audio.Dsp;

/// <summary>
/// Векторизованные примитивы над блоками float-сэмплов (<see cref="Vector{T}"/>).
/// Без аллокаций и блокировок — пригодны для аудиопотока.
/// </summary>
internal static class SampleOps
{
    /// <summary>
    /// true, если все сэмплы конечны (нет NaN и ±∞). Для конечного x разность x − x равна
    /// ровно 0, для NaN/∞ — NaN, который «залипает» в аккумуляторе: один проход без ветвлений.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsAllFinite(ReadOnlySpan<float> samples)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
        var acc = Vector<float>.Zero;
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            acc += v - v;
        }

        if (!Vector.EqualsAll(acc, Vector<float>.Zero))
            return false;

        for (var i = vectors.Length * Vector<float>.Count; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Максимум модуля по блоку (линейная пиковая амплитуда). Пустой блок даёт 0.
    /// Для блоков с NaN результат не определён — проверяйте <see cref="IsAllFinite"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float PeakAbs(ReadOnlySpan<float> samples)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
        var max = Vector<float>.Zero;
        for (var i = 0; i < vectors.Length; i++)
            max = Vector.Max(max, Vector.Abs(vectors[i]));

        var peak = 0f;
        for (var lane = 0; lane < Vector<float>.Count; lane++)
            peak = MathF.Max(peak, max[lane]);

        for (var i = vectors.Length * Vector<float>.Count; i < samples.Length; i++)
            peak = MathF.Max(peak, MathF.Abs(samples[i]));

        return peak;
    }

    /// <summary>Сумма квадратов сэмплов (для RMS). NaN/∞ во входе дают нечисловой результат.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float SumOfSquares(ReadOnlySpan<float> samples)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
        var acc = Vector<float>.Zero;
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            acc += v * v;
        }

        var sum = Vector.Sum(acc);
        for (var i = vectors.Length * Vector<float>.Count; i < samples.Length; i++)
            sum += samples[i] * samples[i];

        return sum;
    }

    /// <summary>Умножает блок на постоянный коэффициент на месте.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Scale(Span<float> samples, float gain)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
        var g = new Vector<float>(gain);
        for (var i = 0; i < vectors.Length; i++)
            vectors[i] *= g;

        for (var i = vectors.Length * Vector<float>.Count; i < samples.Length; i++)
            samples[i] *= gain;
    }

    /// <summary>
    /// Прибавляет <paramref name="source"/> к началу <paramref name="destination"/>
    /// (destination[i] += source[i]). Длина назначения должна быть не меньше длины источника.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Add(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
            ThrowDestinationTooShort(nameof(destination));

        var dst = destination[..source.Length];
        var sv = MemoryMarshal.Cast<float, Vector<float>>(source);
        var dv = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (var i = 0; i < sv.Length; i++)
            dv[i] += sv[i];

        for (var i = sv.Length * Vector<float>.Count; i < source.Length; i++)
            dst[i] += source[i];
    }

    /// <summary>
    /// Прибавляет источник с коэффициентом (destination[i] += source[i] · gain).
    /// Длина назначения должна быть не меньше длины источника.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AddScaled(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        if (destination.Length < source.Length)
            ThrowDestinationTooShort(nameof(destination));

        var dst = destination[..source.Length];
        var sv = MemoryMarshal.Cast<float, Vector<float>>(source);
        var dv = MemoryMarshal.Cast<float, Vector<float>>(dst);
        var g = new Vector<float>(gain);
        for (var i = 0; i < sv.Length; i++)
            dv[i] += sv[i] * g;

        for (var i = sv.Length * Vector<float>.Count; i < source.Length; i++)
            dst[i] += source[i] * gain;
    }

    /// <summary>
    /// Ограничивает сэмплы диапазоном [−1, 1] на месте и возвращает число изменённых сэмплов.
    /// Ожидает конечные данные (NaN отсекается раньше, в <see cref="BusLimiter"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ClampUnit(Span<float> samples)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
        var one = Vector<float>.One;
        var minusOne = -one;
        var outside = Vector<int>.Zero;
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];
            // Маска сравнения: −1 в дорожках, где |v| > 1; вычитание накапливает их число.
            outside -= Vector.GreaterThan(Vector.Abs(v), one);
            vectors[i] = Vector.Min(Vector.Max(v, minusOne), one);
        }

        var count = Vector.Sum(outside);
        for (var i = vectors.Length * Vector<float>.Count; i < samples.Length; i++)
        {
            var x = samples[i];
            if (x > 1f)
            {
                samples[i] = 1f;
                count++;
            }
            else if (x < -1f)
            {
                samples[i] = -1f;
                count++;
            }
        }

        return count;
    }

    /// <summary>Проверяет, что длина буфера кратна числу каналов (только целые кадры).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureWholeFrames(int length, int channels, string paramName)
    {
        if (length % channels != 0)
            ThrowPartialFrame(length, channels, paramName);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPartialFrame(int length, int channels, string paramName) =>
        throw new ArgumentException(
            $"Длина буфера ({length}) не кратна числу каналов ({channels}): допускаются только целые кадры.",
            paramName);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDestinationTooShort(string paramName) =>
        throw new ArgumentException("Буфер назначения короче источника.", paramName);
}
