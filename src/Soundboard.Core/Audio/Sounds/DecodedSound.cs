using System.Diagnostics.CodeAnalysis;

namespace Soundboard.Core.Audio.Sounds;

/// <summary>Каким ресемплером звук был приведён к 48 кГц при декодировании.</summary>
public enum SoundResampler
{
    /// <summary>Исходник уже был 48 кГц, ресемплинг не нужен.</summary>
    None,

    /// <summary>MediaFoundationResampler (качество 60, без задержки).</summary>
    MediaFoundation,

    /// <summary>WdlResamplingSampleProvider — запасной путь без Media Foundation (Windows N).</summary>
    Wdl,
}

/// <summary>Исходный формат файла (для статуса и диагностики).</summary>
/// <param name="SampleRate">Частота дискретизации файла, Гц.</param>
/// <param name="Channels">Число каналов файла.</param>
/// <param name="Resampler">Чем звук приводился к 48 кГц.</param>
public sealed record SoundSourceInfo(int SampleRate, int Channels, SoundResampler Resampler);

/// <summary>
/// Звук, полностью декодированный в память во внутреннем формате движка
/// (<see cref="AudioFormat"/>: 48 кГц, float, стерео, чередующиеся отсчёты L R L R …).
/// </summary>
/// <remarks>
/// Неизменяемый по договорённости: массив <see cref="Samples"/> читают аудиопотоки обеих шин
/// одновременно (у каждой своя позиция), поэтому писать в него после создания нельзя.
/// <c>Samples.Length == Frames * 2</c> всегда.
/// </remarks>
public sealed class DecodedSound
{
    internal DecodedSound(float[] samples, float peak, SoundSourceInfo? source = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("Число отсчётов должно быть кратно числу каналов (стерео).", nameof(samples));
        }
        if (float.IsNaN(peak) || peak < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(peak), peak, "Пик должен быть неотрицательным числом.");
        }

        Samples = samples;
        Frames = samples.Length / AudioFormat.Channels;
        Peak = peak;
        Source = source;
    }

    /// <summary>Чередующиеся стерео-отсчёты 48 кГц. Только для чтения (см. remarks класса).</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Горячий путь воспроизведения читает массив напрямую; копия на каждый Play недопустима.")]
    public float[] Samples { get; }

    /// <summary>Число стерео-кадров (отсчётов на канал).</summary>
    public int Frames { get; }

    /// <summary>Пиковое значение |x| по обоим каналам (линейное, 1.0 = 0 dBFS).</summary>
    public float Peak { get; }

    /// <summary>Пик в dBFS (−∞ заменяется на −200 dB для тишины).</summary>
    public double PeakDbfs => Peak > 0 ? 20 * Math.Log10(Peak) : -200;

    /// <summary>Длительность звука.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)AudioFormat.SampleRate);

    /// <summary>Сколько байт занимают отсчёты в памяти (для бюджета кэша).</summary>
    public long Bytes => Samples.LongLength * sizeof(float);

    /// <summary>Исходный формат файла; <see langword="null"/> для сгенерированных звуков.</summary>
    public SoundSourceInfo? Source { get; }

    /// <summary>
    /// Создаёт звук из готового массива (без копирования, массив переходит во владение звука)
    /// и вычисляет пик.
    /// </summary>
    public static DecodedSound FromSamples(float[] interleavedStereo, SoundSourceInfo? source = null)
    {
        ArgumentNullException.ThrowIfNull(interleavedStereo);
        return new DecodedSound(interleavedStereo, ComputePeak(interleavedStereo), source);
    }

    /// <summary>Максимум |x| по массиву.</summary>
    internal static float ComputePeak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }
        return peak;
    }
}
