using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Xoard.Core.Audio.Sounds;

/// <summary>Ключи кэша звуков.</summary>
public static class SoundFileStamp
{
    /// <summary>Префикс ключа звуков, созданных в памяти (тестовые сигналы).</summary>
    public const string MemoryKeyPrefix = "memory:";

    /// <summary>Ключ файла: полный путь + размер + время изменения. Изменённый файл получает новый ключ.</summary>
    public static string KeyFor(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }
}

/// <summary>
/// Декодирует файл целиком в память во внутреннем формате (48 кГц, float, стерео).
/// Работает в пуле потоков, никогда — на звуковом потоке.
/// </summary>
public static class SoundDecoder
{
    /// <summary>Максимальная длительность звука по умолчанию.</summary>
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMinutes(10);

    private const float Minus3Db = 0.70710678f;

    /// <summary>Декодирует файл. Ошибки — <see cref="SoundLoadException"/> с кодом.</summary>
    public static Task<DecodedSound> DecodeAsync(string path, TimeSpan? maxDuration = null, CancellationToken ct = default) =>
        Task.Run(() => Decode(path, maxDuration ?? DefaultMaxDuration, ct), ct);

    /// <summary>Синхронное декодирование (для тестов и фоновых задач).</summary>
    public static DecodedSound Decode(string path, TimeSpan maxDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new SoundLoadException(SoundLoadError.FileNotFound, fullPath);

        AudioFileReader reader;
        try
        {
            reader = new AudioFileReader(fullPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SoundLoadException(SoundLoadError.AccessDenied, fullPath, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new SoundLoadException(SoundLoadError.InvalidFormat, fullPath, ex.Message, ex);
        }

        using (reader)
        {
            if (reader.TotalTime > maxDuration)
                throw new SoundLoadException(SoundLoadError.TooLong, fullPath, $"{reader.TotalTime:mm\\:ss} > {maxDuration:mm\\:ss}");

            var sourceRate = reader.WaveFormat.SampleRate;
            var sourceChannels = reader.WaveFormat.Channels;
            var resampler = SoundResampler.None;
            MediaFoundationResampler? resamplerToDispose = null;
            ISampleProvider provider = reader;

            try
            {
                if (sourceRate != AudioFormat.SampleRate)
                {
                    try
                    {
                        var mf = new MediaFoundationResampler(reader, AudioFormat.SampleRate) { ResamplerQuality = 60 };
                        resamplerToDispose = mf;
                        provider = mf.ToSampleProvider();
                        resampler = SoundResampler.MediaFoundation;
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or DllNotFoundException or TypeInitializationException)
                    {
                        // Нет Media Foundation (Windows N) — запасной ресемплер
                        provider = new WdlResamplingSampleProvider(reader, AudioFormat.SampleRate);
                        resampler = SoundResampler.Wdl;
                    }
                }

                var samples = ReadAllAsStereo(provider, sourceChannels, maxDuration, fullPath, ct);
                if (samples.Length == 0)
                    throw new SoundLoadException(SoundLoadError.Empty, fullPath);

                return DecodedSound.FromSamples(samples, new SoundSourceInfo(sourceRate, sourceChannels, resampler));
            }
            catch (SoundLoadException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OutOfMemoryException ex)
            {
                throw new SoundLoadException(SoundLoadError.OutOfMemory, fullPath, ex.Message, ex);
            }
            catch (Exception ex)
            {
                throw new SoundLoadException(SoundLoadError.DecodeFailed, fullPath, ex.Message, ex);
            }
            finally
            {
                resamplerToDispose?.Dispose();
            }
        }
    }

    private static float[] ReadAllAsStereo(ISampleProvider provider, int channels, TimeSpan maxDuration, string path, CancellationToken ct)
    {
        var maxFrames = (long)(maxDuration.TotalSeconds * AudioFormat.SampleRate) + AudioFormat.SampleRate;
        var block = new float[AudioFormat.SampleRate / 10 * channels]; // 100 мс
        var stereoBlock = new float[AudioFormat.SampleRate / 10 * 2];
        var chunks = new List<float[]>();
        long totalFrames = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = provider.Read(block);
            read -= read % channels;
            if (read <= 0)
                break;

            var frames = read / channels;
            ToStereo(block.AsSpan(0, read), channels, stereoBlock.AsSpan(0, frames * 2));
            chunks.Add(stereoBlock.AsSpan(0, frames * 2).ToArray());
            totalFrames += frames;
            if (totalFrames > maxFrames)
                throw new SoundLoadException(SoundLoadError.TooLong, path);
        }

        var result = new float[totalFrames * 2];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }

        return result;
    }

    /// <summary>Сведение в стерео: моно дублируется, ≥3 каналов — стандартное сведение (LFE отбрасывается).</summary>
    internal static void ToStereo(ReadOnlySpan<float> source, int channels, Span<float> stereo)
    {
        var frames = source.Length / channels;
        switch (channels)
        {
            case 1:
                for (var i = 0; i < frames; i++)
                {
                    stereo[2 * i] = source[i];
                    stereo[(2 * i) + 1] = source[i];
                }

                break;
            case 2:
                source[..(frames * 2)].CopyTo(stereo);
                break;
            default:
                // Порядок WAVE_FORMAT_EXTENSIBLE: FL, FR, FC, LFE, BL/SL, BR/SR, …
                var hasCenter = channels >= 3;
                var hasSurround = channels >= 6 || channels == 4;
                var leftSurround = channels == 4 ? 2 : 4;
                var rightSurround = channels == 4 ? 3 : 5;
                var centerIndex = channels == 4 ? -1 : 2;
                var norm = 1f / (1f + (hasCenter && centerIndex >= 0 ? Minus3Db : 0f) + (hasSurround ? Minus3Db : 0f));
                for (var i = 0; i < frames; i++)
                {
                    var f = source.Slice(i * channels, channels);
                    var c = centerIndex >= 0 ? f[centerIndex] * Minus3Db : 0f;
                    var l = f[0] + c + (hasSurround ? f[leftSurround] * Minus3Db : 0f);
                    var r = f[1] + c + (hasSurround ? f[rightSurround] * Minus3Db : 0f);
                    stereo[2 * i] = l * norm;
                    stereo[(2 * i) + 1] = r * norm;
                }

                break;
        }
    }
}
