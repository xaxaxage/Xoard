using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Sounds;

namespace Soundboard.Core.Recording;

/// <summary>Пики формы волны для отрисовки: минимум и максимум в каждом столбике.</summary>
public readonly record struct WavePeak(float Min, float Max);

/// <summary>Запись WAV, форма волны, обрезка (чистые функции и файлы, без устройств).</summary>
public static class AudioClips
{
    private const int EdgeFadeFrames = AudioFormat.SampleRate / 200; // 5 мс

    /// <summary>
    /// Сохраняет стерео float (интерлив) как WAV 48 кГц 16 бит; при другой частоте — ресемплинг.
    /// Края плавно (5 мс) входят и выходят из тишины, чтобы не было щелчков.
    /// </summary>
    public static void WriteWav(string path, float[] stereo, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        var samples = sampleRate == AudioFormat.SampleRate ? (float[])stereo.Clone() : Resample(stereo, sampleRate);
        FadeEdges(samples);

        var temp = path + ".tmp";
        using (var writer = new WaveFileWriter(temp, new WaveFormat(AudioFormat.SampleRate, 16, 2)))
        {
            var block = new byte[4096 * 2];
            for (var offset = 0; offset < samples.Length;)
            {
                var count = Math.Min(4096, samples.Length - offset);
                for (var i = 0; i < count; i++)
                {
                    var s = (short)Math.Round(Math.Clamp(samples[offset + i], -1f, 1f) * short.MaxValue);
                    block[2 * i] = (byte)s;
                    block[(2 * i) + 1] = (byte)(s >> 8);
                }

                writer.Write(block, 0, count * 2);
                offset += count;
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Пики для отрисовки: <paramref name="buckets"/> столбиков по моно-сумме каналов.</summary>
    public static WavePeak[] Peaks(float[] stereo, int buckets)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        var frames = stereo.Length / 2;
        var result = new WavePeak[buckets];
        if (frames == 0)
            return result;

        for (var b = 0; b < buckets; b++)
        {
            var from = (int)((long)b * frames / buckets);
            var to = Math.Max(from + 1, (int)((long)(b + 1) * frames / buckets));
            float min = 0, max = 0;
            for (var f = from; f < to && f < frames; f++)
            {
                var v = (stereo[2 * f] + stereo[(2 * f) + 1]) * 0.5f;
                if (v < min)
                    min = v;
                if (v > max)
                    max = v;
            }

            result[b] = new WavePeak(min, max);
        }

        return result;
    }

    /// <summary>Вырезает кусок [start, end) секунд из стерео 48 кГц.</summary>
    public static float[] Slice(float[] stereo, double startSeconds, double endSeconds)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        var frames = stereo.Length / 2;
        var from = (int)Math.Clamp(Math.Round(startSeconds * AudioFormat.SampleRate), 0, frames);
        var to = (int)Math.Clamp(Math.Round(endSeconds * AudioFormat.SampleRate), from, frames);
        return stereo.AsSpan(from * 2, (to - from) * 2).ToArray();
    }

    /// <summary>
    /// Обрезает файл звука до [start, end) секунд и сохраняет как WAV в <paramref name="destinationPath"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Пустой или вывернутый интервал.</exception>
    public static TimeSpan TrimToWav(string sourcePath, double startSeconds, double endSeconds, string destinationPath)
    {
        var sound = SoundDecoder.Decode(sourcePath, SoundDecoder.DefaultMaxDuration);
        var duration = sound.Duration.TotalSeconds;
        if (double.IsNaN(startSeconds) || double.IsNaN(endSeconds) || startSeconds < 0 || endSeconds <= startSeconds || startSeconds >= duration)
            throw new ArgumentException($"Неверный интервал: {startSeconds:0.##}–{endSeconds:0.##} с при длительности {duration:0.##} с.");
        var piece = Slice(sound.Samples, startSeconds, Math.Min(endSeconds, duration));
        if (piece.Length < AudioFormat.SampleRate / 50 * 2)
            throw new ArgumentException("Слишком короткий кусок (меньше 20 мс).");
        WriteWav(destinationPath, piece, AudioFormat.SampleRate);
        return TimeSpan.FromSeconds(piece.Length / 2.0 / AudioFormat.SampleRate);
    }

    private static void FadeEdges(float[] samples)
    {
        var frames = samples.Length / 2;
        var fade = Math.Min(EdgeFadeFrames, frames / 2);
        for (var f = 0; f < fade; f++)
        {
            var g = f / (float)fade;
            samples[2 * f] *= g;
            samples[(2 * f) + 1] *= g;
            var e = frames - 1 - f;
            samples[2 * e] *= g;
            samples[(2 * e) + 1] *= g;
        }
    }

    private static float[] Resample(float[] stereo, int sampleRate)
    {
        var source = new FloatArrayProvider(stereo, sampleRate);
        var resampler = new WdlResamplingSampleProvider(source, AudioFormat.SampleRate);
        var output = new List<float>((int)((long)stereo.Length * AudioFormat.SampleRate / sampleRate) + 4096);
        var buffer = new float[8192];
        int read;
        while ((read = resampler.Read(buffer)) > 0)
            output.AddRange(buffer.AsSpan(0, read));
        return [.. output];
    }

    private sealed class FloatArrayProvider(float[] data, int sampleRate) : ISampleProvider
    {
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, data.Length - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
    }
}
