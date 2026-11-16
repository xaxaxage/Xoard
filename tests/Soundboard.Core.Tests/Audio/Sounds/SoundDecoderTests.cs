using NAudio.Wave;
using Soundboard.Core.Audio.Sounds;

namespace Soundboard.Core.Tests.Audio.Sounds;

public sealed class SoundDecoderTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("sb-decoder-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string WriteTone(string name, WaveFormat format, double seconds, double hz, float amplitude)
    {
        var path = Path.Combine(dir, name);
        using var writer = new WaveFileWriter(path, format);
        var frames = (int)(seconds * format.SampleRate);
        for (var i = 0; i < frames; i++)
        {
            var v = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / format.SampleRate);
            for (var c = 0; c < format.Channels; c++)
                writer.WriteSample(v);
        }

        return path;
    }

    [Fact]
    public void Mono_44k_pcm16_becomes_48k_stereo_with_same_duration()
    {
        var path = WriteTone("mono.wav", new WaveFormat(44_100, 16, 1), 1.0, 1000, 0.5f);

        var sound = SoundDecoder.Decode(path, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.InRange(sound.Frames, 48_000 - 64, 48_000 + 64);
        Assert.Equal(sound.Frames * 2, sound.Samples.Length);
        for (var i = 1000; i < 1100; i++)
            Assert.Equal(sound.Samples[2 * i], sound.Samples[(2 * i) + 1]);   // L = R
        Assert.InRange(sound.Peak, 0.45f, 0.55f);
        Assert.Equal(44_100, sound.Source?.SampleRate);
    }

    [Fact]
    public void Stereo_48k_float_is_passed_through()
    {
        var path = WriteTone("st.wav", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), 0.5, 440, 0.25f);

        var sound = SoundDecoder.Decode(path, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(24_000, sound.Frames);
        Assert.Equal(SoundResampler.None, sound.Source?.Resampler);
        Assert.InRange(sound.Peak, 0.24f, 0.26f);
    }

    [Fact]
    public void Six_channels_are_downmixed_to_stereo()
    {
        var path = WriteTone("51.wav", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 6), 0.2, 440, 0.3f);

        var sound = SoundDecoder.Decode(path, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(9600, sound.Frames);
        Assert.True(sound.Peak <= 1f);
    }

    [Fact]
    public void Too_long_sound_is_rejected()
    {
        var path = WriteTone("long.wav", new WaveFormat(48_000, 16, 1), 3.0, 440, 0.2f);

        var ex = Assert.Throws<SoundLoadException>(() => SoundDecoder.Decode(path, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(SoundLoadError.TooLong, ex.Error);
    }

    [Fact]
    public void Corrupt_file_is_rejected()
    {
        var path = Path.Combine(dir, "bad.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<SoundLoadException>(() => SoundDecoder.Decode(path, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Equal(SoundLoadError.InvalidFormat, ex.Error);
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var ex = Assert.Throws<SoundLoadException>(() => SoundDecoder.Decode(Path.Combine(dir, "nope.wav"), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Equal(SoundLoadError.FileNotFound, ex.Error);
    }

    [Fact]
    public async Task Cache_deduplicates_and_evicts_least_recently_used()
    {
        var a = WriteTone("a.wav", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), 1.0, 440, 0.1f);   // 384 КБ
        var b = WriteTone("b.wav", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), 1.0, 440, 0.1f);
        using var cache = new SoundCache(budgetBytes: 500_000);

        var handles = await Task.WhenAll(cache.LoadAsync(a, TestContext.Current.CancellationToken), cache.LoadAsync(a, TestContext.Current.CancellationToken));
        Assert.Equal(handles[0].Key, handles[1].Key);
        Assert.Equal(1, cache.Count);

        var hb = await cache.LoadAsync(b, TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Count);                       // a вытеснен бюджетом
        Assert.True(cache.TryGet(hb.Key, out _));
        Assert.False(cache.TryGet(handles[0].Key, out _));
    }
}
