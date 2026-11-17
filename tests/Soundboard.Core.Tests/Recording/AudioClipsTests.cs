using NAudio.Wave;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Recording;

namespace Soundboard.Core.Tests.Recording;

public sealed class AudioClipsTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("sb-clips-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static float[] Tone(double seconds, int rate = 48_000, double hz = 440, float amp = 0.5f)
    {
        var frames = (int)(seconds * rate);
        var s = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            s[2 * i] = s[(2 * i) + 1] = amp * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return s;
    }

    [Fact]
    public void WriteWav_writes_48k_16bit_stereo_with_soft_edges()
    {
        var path = Path.Combine(dir, "a.wav");
        AudioClips.WriteWav(path, Tone(1.0), 48_000);

        using var reader = new WaveFileReader(path);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(48_000, reader.SampleCount);

        var sound = SoundDecoder.Decode(path, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(0f, sound.Samples[0], 3);                       // начало из тишины
        Assert.Equal(0f, sound.Samples[^1], 3);                      // конец в тишину
        Assert.InRange(sound.Peak, 0.48f, 0.51f);
    }

    [Fact]
    public void WriteWav_resamples_other_rates()
    {
        var path = Path.Combine(dir, "b.wav");
        AudioClips.WriteWav(path, Tone(1.0, 44_100), 44_100);

        using var reader = new WaveFileReader(path);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.InRange(reader.SampleCount, 48_000 - 200, 48_000 + 200);
    }

    [Fact]
    public void Trim_cuts_the_requested_interval()
    {
        var source = Path.Combine(dir, "src.wav");
        AudioClips.WriteWav(source, Tone(3.0), 48_000);
        var dest = Path.Combine(dir, "cut.wav");

        var length = AudioClips.TrimToWav(source, 0.5, 2.0, dest);

        Assert.Equal(1.5, length.TotalSeconds, 2);
        using var reader = new WaveFileReader(dest);
        Assert.Equal(72_000, reader.SampleCount);
    }

    [Theory]
    [InlineData(2.0, 1.0)]
    [InlineData(-1, 1.0)]
    [InlineData(5.0, 6.0)]
    [InlineData(1.0, 1.005)]
    public void Trim_rejects_bad_intervals(double start, double end)
    {
        var source = Path.Combine(dir, "src2.wav");
        AudioClips.WriteWav(source, Tone(3.0), 48_000);
        Assert.Throws<ArgumentException>(() => AudioClips.TrimToWav(source, start, end, Path.Combine(dir, "x.wav")));
    }

    [Fact]
    public void Peaks_follow_the_signal()
    {
        var samples = new float[48_000 * 2];
        for (var i = 24_000; i < 48_000; i++)
            samples[2 * i] = samples[(2 * i) + 1] = 0.8f;   // тишина, потом громко

        var peaks = AudioClips.Peaks(samples, 10);

        Assert.Equal(10, peaks.Length);
        Assert.Equal(0f, peaks[0].Max);
        Assert.Equal(0.8f, peaks[9].Max, 3);
    }

    [Fact]
    public void Stereo_converter_handles_pcm16_and_mono()
    {
        var pcm = new byte[] { 0x00, 0x40, 0x00, 0xC0 };   // L=+0.5, R=-0.5
        var stereo = new float[2];
        StereoConverter.Convert(pcm, new WaveFormat(48_000, 16, 2), stereo);
        Assert.Equal(0.5f, stereo[0], 3);
        Assert.Equal(-0.5f, stereo[1], 3);

        var mono = BitConverter.GetBytes(0.25f);
        StereoConverter.Convert(mono, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1), stereo);
        Assert.Equal(0.25f, stereo[0]);
        Assert.Equal(0.25f, stereo[1]);
    }
}
