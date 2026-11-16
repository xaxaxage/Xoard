using System.Runtime.CompilerServices;

namespace Soundboard.Core.Audio.Dsp;

/// <summary>
/// Линейные рампы усиления по кадрам (затухания, нарастания, сглаживание громкости).
/// <para>
/// Соглашение: рампа из <c>startGain</c> в <c>endGain</c> длиной N кадров даёт кадру с номером
/// k = 1..N усиление <c>end − (end − start) · (N − k) / N</c>. Последний кадр получает ровно
/// <c>endGain</c> (затухание заканчивается точным нулём, нарастание — точной единицей), шаг между
/// кадрами равен (end − start) / N, последовательность монотонна. Рампу можно делить на блоки
/// любого размера: параметр <c>position</c> — сколько кадров рампы уже обработано, результат
/// не зависит от разбиения.
/// </para>
/// </summary>
internal static class Fade
{
    /// <summary>
    /// Максимальная длина рампы в кадрах (2²⁴ ≈ 349 с при 48 кГц): до этой границы целые
    /// номера кадров точно представимы во float, и рампа остаётся строго линейной и монотонной.
    /// </summary>
    public const int MaxRampFrames = 1 << 24;

    /// <summary>Число кадров формата движка (48 кГц) в заданном числе миллисекунд, с округлением.</summary>
    public static int FramesFor(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "Длительность должна быть конечной и неотрицательной.");

        var frames = Math.Round(milliseconds * AudioFormat.SampleRate / 1000.0);
        if (frames > MaxRampFrames)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "Слишком длинная рампа.");
        return (int)frames;
    }

    /// <summary>Число кадров формата движка (48 кГц) в заданном интервале, с округлением.</summary>
    public static int FramesFor(TimeSpan duration) => FramesFor(duration.TotalMilliseconds);

    /// <summary>
    /// Усиление после <paramref name="position"/> обработанных кадров рампы — то же значение,
    /// что получил последний обработанный кадр (0 → <paramref name="startGain"/>, N → ровно
    /// <paramref name="endGain"/>).
    /// </summary>
    public static float GainAt(float startGain, float endGain, int position, int totalFrames)
    {
        if (position <= 0)
            return startGain;
        if (position >= totalFrames)
            return endGain;
        var inverse = 1f / totalFrames;
        return endGain - (endGain - startGain) * ((totalFrames - position) * inverse);
    }

    /// <summary>
    /// Применяет к началу <paramref name="buffer"/> (чередующиеся каналы) продолжение рампы
    /// с позиции <paramref name="position"/>. Обрабатывает min(кадров в буфере, N − position)
    /// кадров и возвращает их число; остаток буфера не трогает (его усиление решает вызывающий:
    /// после затухания обычно тишина, после нарастания — без изменений).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ApplyLinearRamp(Span<float> buffer, int channels, float startGain, float endGain, int position, int totalFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalFrames, MaxRampFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, totalFrames);
        SampleOps.EnsureWholeFrames(buffer.Length, channels, nameof(buffer));

        var frames = Math.Min(buffer.Length / channels, totalFrames - position);
        var delta = endGain - startGain;
        var inverse = 1f / totalFrames;
        var remaining = totalFrames - position;

        if (channels == 2)
        {
            for (var i = 0; i < frames * 2; i += 2)
            {
                remaining--;
                var g = endGain - delta * (remaining * inverse);
                buffer[i] *= g;
                buffer[i + 1] *= g;
            }
        }
        else if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                remaining--;
                buffer[i] *= endGain - delta * (remaining * inverse);
            }
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                remaining--;
                var g = endGain - delta * (remaining * inverse);
                var frame = buffer.Slice(f * channels, channels);
                for (var c = 0; c < frame.Length; c++)
                    frame[c] *= g;
            }
        }

        return frames;
    }

    /// <summary>Затухание 1 → 0 за <paramref name="totalFrames"/> кадров (последний кадр — ровно 0).</summary>
    public static int FadeOut(Span<float> buffer, int channels, int position, int totalFrames) =>
        ApplyLinearRamp(buffer, channels, 1f, 0f, position, totalFrames);

    /// <summary>Нарастание 0 → 1 за <paramref name="totalFrames"/> кадров (последний кадр — ровно 1).</summary>
    public static int FadeIn(Span<float> buffer, int channels, int position, int totalFrames) =>
        ApplyLinearRamp(buffer, channels, 0f, 1f, position, totalFrames);
}
