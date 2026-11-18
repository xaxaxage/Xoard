using System.Diagnostics;
using Xoard.Core.Audio;
using Xoard.Core.Audio.Mic;
using Xoard.Core.Config;

namespace Xoard.Core.Tests.Audio.Mic;

public sealed class VoiceProcessorTests
{
    private const int Rate = AudioFormat.SampleRate;
    private const int Frame = VoiceProcessor.FrameSize;

    /// <summary>Подавитель для проверок: задерживает кадр, как RNNoise, и умножает на <paramref name="cleanGain"/>.</summary>
    private sealed class DelayDenoiser(float speech, float cleanGain = 1f) : IVoiceDenoiser
    {
        private readonly float[] previous = new float[Frame];

        public float Speech { get; set; } = speech;

        public int Resets { get; private set; }

        public float Process(ReadOnlySpan<float> input, Span<float> output)
        {
            for (var i = 0; i < Frame; i++)
                output[i] = previous[i] * cleanGain;
            input[..Frame].CopyTo(previous);
            return Speech;
        }

        public void Reset()
        {
            Array.Clear(previous);
            Resets++;
        }
    }

    /// <summary>Гонит моно-сигнал через обработку блоками неровного размера; возвращает левый канал.</summary>
    private static float[] Run(VoiceProcessor voice, Func<long, float> signal, int frames, long start = 0, Action<long>? beforeBlock = null)
    {
        var result = new float[frames];
        var block = new float[441 * 2];
        for (var done = 0; done < frames;)
        {
            beforeBlock?.Invoke(start + done);
            var n = Math.Min(441, frames - done);
            for (var i = 0; i < n; i++)
                block[2 * i] = block[(2 * i) + 1] = signal(start + done + i);
            voice.Process(block.AsSpan(0, n * 2));
            for (var i = 0; i < n; i++)
            {
                Assert.Equal(block[2 * i], block[(2 * i) + 1]);
                result[done + i] = block[2 * i];
            }

            done += n;
        }

        return result;
    }

    private static Func<long, float> Sine(float amplitude, double hz = 220) =>
        i => amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate);

    private static double RmsDb(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x)
            sum += v * v;
        return 10 * Math.Log10(sum / x.Length);
    }

    private static float AmplitudeForRmsDb(double db) => (float)(Math.Sqrt(2) * Math.Pow(10, db / 20));

    [Fact]
    public void Without_denoiser_voice_passes_unchanged()
    {
        var voice = new VoiceProcessor(null);
        var block = new float[] { 0.1f, 0.2f, -0.3f, 0.4f };
        voice.Process(block);
        Assert.Equal([0.1f, 0.2f, -0.3f, 0.4f], block);
        Assert.False(voice.Available);
    }

    [Fact]
    public void Voice_is_delayed_by_two_frames()
    {
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 0));
        voice.Configure(noiseSuppression: true, autoLevel: false);
        voice.Reset("mic");

        var output = Run(voice, i => i == 1000 ? 0.5f : 0f, 4000);
        Assert.Equal(0.5f, output[1000 + VoiceProcessor.LatencyFrames], 4);
        Assert.Equal(0f, output.Sum(Math.Abs) - Math.Abs(output[1000 + VoiceProcessor.LatencyFrames]), 4);
    }

    [Fact]
    public void Switching_noise_suppression_keeps_voice_aligned()
    {
        // Подавитель ничего не меняет — значит, переключение не должно оставить следа
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 0));
        voice.Configure(noiseSuppression: true, autoLevel: false);
        voice.Reset("mic");
        var sine = Sine(0.3f);

        var output = Run(voice, sine, Rate, beforeBlock: at => voice.Configure(noiseSuppression: at / 4410 % 2 == 0, autoLevel: false));
        for (var i = VoiceProcessor.LatencyFrames; i < output.Length; i++)
            Assert.Equal(sine(i - VoiceProcessor.LatencyFrames), output[i], 4);
    }

    [Fact]
    public void Noise_suppression_off_gives_original_voice()
    {
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 0, cleanGain: 0.25f));
        voice.Configure(noiseSuppression: false, autoLevel: false);
        voice.Reset("mic");
        var sine = Sine(0.3f);

        var output = Run(voice, sine, Rate / 2);
        for (var i = VoiceProcessor.LatencyFrames; i < output.Length; i++)
            Assert.Equal(sine(i - VoiceProcessor.LatencyFrames), output[i], 4);
    }

    [Theory]
    [InlineData(-40.0)]
    [InlineData(-30.0)]
    [InlineData(-10.0)]
    public void Speech_is_brought_to_target_level(double inputDb)
    {
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 1));
        voice.Reset("mic");

        var output = Run(voice, Sine(AmplitudeForRmsDb(inputDb)), 4 * Rate);
        Assert.Equal(VoiceProcessor.TargetDb - inputDb, voice.GainDb, 1);
        Assert.Equal(VoiceProcessor.TargetDb, RmsDb(output.AsSpan(3 * Rate)), 0.5);
    }

    [Fact]
    public void Very_quiet_mic_gets_at_most_max_boost()
    {
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 1));
        voice.Reset("mic");
        Run(voice, Sine(AmplitudeForRmsDb(-65)), 4 * Rate);
        Assert.Equal(VoiceProcessor.MaxBoostDb, voice.GainDb, 3);
    }

    [Fact]
    public void Gain_holds_during_pauses_and_does_not_lift_background()
    {
        var denoiser = new DelayDenoiser(speech: 1);
        var voice = new VoiceProcessor(denoiser);
        voice.Reset("mic");
        Run(voice, Sine(AmplitudeForRmsDb(-36)), 3 * Rate);
        var leveled = voice.GainDb;

        denoiser.Speech = 0.1f;   // тишина с тихим фоном
        Run(voice, Sine(AmplitudeForRmsDb(-60), 3000), 3 * Rate);
        Assert.Equal(leveled, voice.GainDb, 3);
    }

    [Fact]
    public void Turning_auto_level_off_returns_to_unity_smoothly()
    {
        var voice = new VoiceProcessor(new DelayDenoiser(speech: 1));
        voice.Reset("mic");
        Run(voice, Sine(AmplitudeForRmsDb(-38)), 3 * Rate);
        Assert.True(voice.GainDb > 19);

        voice.Configure(noiseSuppression: true, autoLevel: false);
        Run(voice, Sine(AmplitudeForRmsDb(-38)), Rate / 2, 3 * Rate);
        Assert.True(voice.GainDb is > 5 and < 19, $"усиление {voice.GainDb}: должно уходить плавно");
        Run(voice, Sine(AmplitudeForRmsDb(-38)), Rate, (3 * Rate) + (Rate / 2));
        Assert.Equal(0f, voice.GainDb, 3);
    }

    [Fact]
    public void Same_mic_keeps_its_level_after_restart_other_mic_starts_over()
    {
        var denoiser = new DelayDenoiser(speech: 1);
        var voice = new VoiceProcessor(denoiser);
        voice.Reset("mic-a");
        Run(voice, Sine(AmplitudeForRmsDb(-40)), 3 * Rate);
        var leveled = voice.GainDb;

        voice.Reset("mic-a");
        Assert.Equal(leveled, voice.GainDb);
        Assert.Equal(2, denoiser.Resets);

        voice.Reset("mic-b");
        Assert.Equal(0f, voice.GainDb);
    }

    [Fact]
    public void Old_settings_with_loud_voice_return_to_100_percent_once()
    {
        var dir = Directory.CreateTempSubdirectory("xoard-voice-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "version": 2, "audio": { "micLevel": 1.8, "cableLevel": 1.5 } }""");
            var config = new ConfigStore(path).Load();
            Assert.Equal(1f, config.Audio.MicLevel);
            Assert.Equal(1.5f, config.Audio.CableLevel);
            Assert.True(config.Audio.VoiceNoiseSuppression);
            Assert.True(config.Audio.VoiceAutoLevel);
            Assert.Equal(AppConfig.CurrentVersion, config.Version);

            File.WriteAllText(path, """{ "version": 3, "audio": { "micLevel": 1.8, "voiceAutoLevel": false } }""");
            config = new ConfigStore(path).Load();
            Assert.Equal(1.8f, config.Audio.MicLevel);                       // новый формат — свой выбор не трогаем
            Assert.False(config.Audio.VoiceAutoLevel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────── настоящий RNNoise (rnnoise.dll рядом с тестами) ─────────

    [Fact]
    public void RnNoise_removes_steady_noise()
    {
        using var rnnoise = RnNoise.TryCreate();
        Assert.SkipWhen(rnnoise is null, $"rnnoise.dll нет: {RnNoise.LoadError}");

        var random = new Random(7);
        var input = new float[Frame];
        var output = new float[Frame];
        double inPower = 0, outPower = 0;
        for (var f = 0; f < 300; f++)
        {
            for (var i = 0; i < Frame; i++)
                input[i] = (float)(random.NextDouble() - 0.5) * 2000;   // ровный шум около −30 dBFS
            var speech = rnnoise.Process(input, output);
            Assert.InRange(speech, 0f, 1f);
            if (f < 100)
                continue;
            foreach (var x in input)
                inPower += x * x;
            foreach (var x in output)
                outPower += x * x;
        }

        var reductionDb = 10 * Math.Log10(inPower / Math.Max(outPower, 1e-9));
        Assert.True(reductionDb > 15, $"шум ослаблен только на {reductionDb:F1} дБ");
    }

    [Fact]
    public void RnNoise_takes_a_small_share_of_one_core()
    {
        using var rnnoise = RnNoise.TryCreate();
        Assert.SkipWhen(rnnoise is null, $"rnnoise.dll нет: {RnNoise.LoadError}");

        var random = new Random(3);
        var input = new float[Frame];
        var output = new float[Frame];
        for (var i = 0; i < Frame; i++)
            input[i] = (float)(random.NextDouble() - 0.5) * 4000;

        const int frames = 3000;    // 30 с голоса
        var watch = Stopwatch.StartNew();
        for (var f = 0; f < frames; f++)
            rnnoise.Process(input, output);
        var share = watch.Elapsed.TotalSeconds / (frames * Frame / (double)Rate);

        TestContext.Current.TestOutputHelper?.WriteLine($"RNNoise: {share:P2} одного ядра");
        Assert.True(share < 0.10, $"RNNoise занимает {share:P1} одного ядра");
    }
}
