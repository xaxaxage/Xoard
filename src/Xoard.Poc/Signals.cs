using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Xoard.Poc;

internal static class Signals
{
    /// <summary>Синус во внутреннем формате с плавными краями (без щелчков в начале и конце).</summary>
    public static ISampleProvider CreateTone(double frequency, float gain, double seconds)
    {
        var generator = new SignalGenerator(InternalFormat.SampleRate, InternalFormat.Channels)
        {
            Frequency = frequency,
            Gain = gain,
            Type = SignalGeneratorType.Sin,
        };
        var totalFrames = (long)(seconds * InternalFormat.SampleRate);
        var fadeFrames = InternalFormat.SampleRate / 100; // 10 мс
        return new FadeEdgesSampleProvider(generator.Take(TimeSpan.FromSeconds(seconds)), totalFrames, fadeFrames);
    }
}

/// <summary>Линейное нарастание в начале и затухание в конце источника известной длины.</summary>
internal sealed class FadeEdgesSampleProvider(ISampleProvider source, long totalFrames, int fadeFrames) : ISampleProvider
{
    private long _positionFrames;

    public WaveFormat WaveFormat => source.WaveFormat;

    // NAudio 3: ISampleProvider читает в Span<float>
    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        var channels = WaveFormat.Channels;

        for (var i = 0; i < read; i += channels)
        {
            var frame = _positionFrames + (i / channels);
            var gain = 1f;
            if (frame < fadeFrames)
            {
                gain = frame / (float)fadeFrames;
            }

            var remaining = totalFrames - frame;
            if (remaining < fadeFrames)
            {
                gain = Math.Min(gain, Math.Max(0, remaining) / (float)fadeFrames);
            }

            for (var c = 0; c < channels && i + c < read; c++)
            {
                buffer[i + c] *= gain;
            }
        }

        _positionFrames += read / channels;
        return read;
    }
}
