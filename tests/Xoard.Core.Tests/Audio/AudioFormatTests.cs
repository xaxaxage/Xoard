using NAudio.Wave;
using Xoard.Core.Audio;

namespace Xoard.Core.Tests.Audio;

public class AudioFormatTests
{
    [Fact]
    public void Internal_format_is_48k_float_stereo()
    {
        var format = AudioFormat.WaveFormat;

        Assert.Equal(WaveFormatEncoding.IeeeFloat, format.Encoding);
        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(32, format.BitsPerSample);
    }

    [Theory]
    [InlineData(1000, 96_000)]
    [InlineData(10, 960)]
    [InlineData(0, 0)]
    public void SamplesFor_counts_interleaved_samples(int milliseconds, int expected) =>
        Assert.Equal(expected, AudioFormat.SamplesFor(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void DurationOf_is_inverse_of_SamplesFor()
    {
        var duration = TimeSpan.FromMilliseconds(1234);
        Assert.Equal(duration, AudioFormat.DurationOf(AudioFormat.SamplesFor(duration)));
    }
}
