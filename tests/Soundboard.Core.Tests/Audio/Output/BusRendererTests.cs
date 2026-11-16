using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Mic;
using Soundboard.Core.Audio.Output;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Audio.Voices;

namespace Soundboard.Core.Tests.Audio.Output;

public class BusRendererTests
{
    private static BusRenderer NewRenderer(BusId id = BusId.Headphones)
    {
        var r = new BusRenderer(id, 1f, rampMs: 1);
        r.Warmup();
        return r;
    }

    private static Voice NewVoice(float value, int frames, float gain = 1f)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        var sound = DecodedSound.FromSamples(samples);
        return new Voice(new VoiceId(1), new SoundHandle("k", "", "test", sound.Duration), sound, gain, DateTimeOffset.Now);
    }

    [Fact]
    public void Idle_bus_outputs_exact_silence_over_stale_buffer()
    {
        var r = NewRenderer();
        var buffer = new float[960];
        Array.Fill(buffer, 0.7f);

        Assert.Equal(buffer.Length, r.Read(buffer));
        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    [InlineData(4800)]
    public void Always_returns_full_block(int frames)
    {
        var r = NewRenderer();
        Assert.Equal(frames * 2, r.Read(new float[frames * 2]));
    }

    [Fact]
    public void Mixes_voices_and_removes_them_at_the_end()
    {
        var r = NewRenderer();
        r.Enqueue(new VoiceTap(NewVoice(0.1f, 100)));
        r.Enqueue(new VoiceTap(NewVoice(0.2f, 50)));
        var buffer = new float[300 * 2];
        var d = r.Limiter.LatencyFrames;   // упреждение лимитера сдвигает выход

        r.Read(buffer);

        Assert.Equal(0f, buffer[0]);                       // задержка лимитера
        Assert.Equal(0.3f, buffer[(d + 1) * 2], 4);        // обе
        Assert.Equal(0.3f, buffer[((d + 49) * 2) + 1], 4);
        Assert.Equal(0.1f, buffer[(d + 60) * 2], 4);       // вторая кончилась
        Assert.Equal(0f, buffer[(d + 150) * 2]);           // обе кончились
    }

    [Fact]
    public void Stop_fades_out_linearly_and_ends_tap()
    {
        var r = NewRenderer();
        var voice = NewVoice(0.5f, 48_000);
        var tap = new VoiceTap(voice);
        r.Enqueue(tap);
        r.Read(new float[480 * 2]);   // звучит

        voice.RequestStop(480, PlaybackEndReason.StopAll);
        var buffer = new float[960 * 2];
        r.Read(buffer);
        var d = r.Limiter.LatencyFrames;

        for (var f = d + 1; f < d + 480; f++)
            Assert.True(buffer[2 * f] <= buffer[2 * (f - 1)] + 1e-6f, "затухание не монотонно");
        Assert.True(buffer[2 * d] > 0.49f);
        Assert.Equal(0f, buffer[(d + 480) * 2]);
        Assert.Equal(TapState.Ended, tap.State);
        Assert.Equal(PlaybackEndReason.StopAll, tap.EndReason);
    }

    [Fact]
    public void Stop_before_first_read_ends_silently()
    {
        var voice = NewVoice(0.5f, 1000);
        var tap = new VoiceTap(voice);
        voice.RequestStop(480, PlaybackEndReason.Stopped);

        Assert.Equal(0, tap.Read(new float[960]));
        Assert.Equal(TapState.Ended, tap.State);
        Assert.False(tap.WasAudible);
    }

    [Fact]
    public void Two_taps_with_different_block_sizes_produce_identical_streams()
    {
        var samples = new float[3000 * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(i * 0.01);
        var sound = DecodedSound.FromSamples(samples);
        var voice = new Voice(new VoiceId(1), new SoundHandle("k", "", "t", sound.Duration), sound, 0.8f, DateTimeOffset.Now);

        static List<float> Drain(VoiceTap tap, int block)
        {
            var all = new List<float>();
            var buf = new float[block * 2];
            int n;
            do
            {
                n = tap.Read(buf);
                all.AddRange(buf.Take(n));
            }
            while (n == buf.Length);
            return all;
        }

        Assert.Equal(Drain(new VoiceTap(voice), 480), Drain(new VoiceTap(voice), 137));
    }

    [Fact]
    public void Mic_is_allowed_only_on_cable_bus()
    {
        var mic = new MicPassthrough();
        Assert.Throws<ArgumentException>(() => new BusRenderer(BusId.Headphones, 1f, 1, mic));
        _ = new BusRenderer(BusId.Cable, 1f, 1, mic);
    }

    [Fact]
    public void Limiter_keeps_sum_of_loud_voices_below_ceiling()
    {
        var r = NewRenderer(BusId.Cable);
        for (var i = 0; i < 4; i++)
            r.Enqueue(new VoiceTap(NewVoice(0.9f, 48_000)));

        var buffer = new float[4800 * 2];
        r.Read(buffer);
        r.Read(buffer);

        var ceiling = MathF.Pow(10, -1f / 20);
        Assert.All(buffer, s => Assert.True(Math.Abs(s) <= ceiling + 1e-4f, $"{s} выше потолка"));
    }

    [Fact]
    public void Read_does_not_allocate_after_warmup()
    {
        var r = NewRenderer(BusId.Cable);
        var voices = Enumerable.Range(0, 64).Select(i => new VoiceTap(NewVoice(0.01f, 2000 + (i * 300)))).ToList();
        var buffer = new float[480 * 2];
        for (var i = 0; i < 100; i++)
            r.Read(buffer);   // прогрев JIT

        long allocated = 0;
        var next = 0;
        for (var block = 0; block < 2000; block++)
        {
            if (block % 20 == 0 && next < voices.Count)
                r.Enqueue(voices[next++]);   // постановка в очередь (вне звукового потока) не считается
            var before = GC.GetAllocatedBytesForCurrentThread();
            r.Read(buffer);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }
}
