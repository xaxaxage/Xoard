using NAudio.Wave;

namespace Soundboard.Core.Audio;

/// <summary>Единый внутренний формат движка: 48 кГц, float, стерео. Всё приводится к нему.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    public static WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>Сколько float-сэмплов (с учётом каналов) занимает заданное время.</summary>
    public static int SamplesFor(TimeSpan duration) => (int)Math.Round(duration.TotalSeconds * SampleRate) * Channels;

    /// <summary>Длительность заданного числа float-сэмплов (с учётом каналов).</summary>
    public static TimeSpan DurationOf(long samples) => TimeSpan.FromSeconds(samples / (double)(SampleRate * Channels));
}
