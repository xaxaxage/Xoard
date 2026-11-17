using Xoard.Core.Recording;

namespace Xoard.Core.Tests.Recording;

public class PeakHistoryTests
{
    [Fact]
    public void Latest_peaks_come_last_and_missing_ones_are_silence()
    {
        var history = new PeakHistory(5);
        history.Add(0.1f);
        history.Add(0.2f);

        var dest = new float[4];
        Assert.Equal(2, history.CopyLatest(dest));
        Assert.Equal([0f, 0f, 0.1f, 0.2f], dest);
    }

    [Fact]
    public void Old_peaks_are_overwritten_when_the_ring_is_full()
    {
        var history = new PeakHistory(3);
        for (var i = 1; i <= 5; i++)
            history.Add(i / 10f);

        var dest = new float[3];
        Assert.Equal(3, history.CopyLatest(dest));
        Assert.Equal([0.3f, 0.4f, 0.5f], dest);

        var two = new float[2];
        history.CopyLatest(two);
        Assert.Equal([0.4f, 0.5f], two);

        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.CopyLatest(dest));
        Assert.Equal([0f, 0f, 0f], dest);
    }

    [Fact]
    public void Envelope_is_normalized_to_the_loudest_part()
    {
        // 1 с тишины, 1 с тихого тона (0,1), 1 с громкого (0,4)
        var frames = 48_000;
        var stereo = new float[frames * 3 * 2];
        for (var i = 0; i < frames; i++)
        {
            var quiet = 0.1f * (float)Math.Sin(2 * Math.PI * 440 * i / 48_000);
            var loud = 0.4f * (float)Math.Sin(2 * Math.PI * 440 * i / 48_000);
            stereo[(2 * (frames + i))] = stereo[(2 * (frames + i)) + 1] = quiet;
            stereo[(2 * ((2 * frames) + i))] = stereo[(2 * ((2 * frames) + i)) + 1] = loud;
        }

        var env = AudioClips.Envelope(stereo, 3);
        Assert.Equal(0f, env[0], 3);
        Assert.Equal(0.25f, env[1], 2);
        Assert.Equal(1f, env[2], 3);
        Assert.All(AudioClips.Envelope([], 4), v => Assert.Equal(0f, v));
    }
}
