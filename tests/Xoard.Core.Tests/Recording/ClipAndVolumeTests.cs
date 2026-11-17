using NAudio.Wave;
using Xoard.Core.Audio;
using Xoard.Core.Audio.Dsp;
using Xoard.Core.Config;
using Xoard.Core.Recording;

namespace Xoard.Core.Tests.Recording;

public sealed class ClipAndVolumeTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("xoard-clip-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static AudioClip Tone(double seconds)
    {
        var frames = (int)(seconds * AudioFormat.SampleRate);
        var s = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            s[2 * i] = s[(2 * i) + 1] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / AudioFormat.SampleRate);
        return new AudioClip(s, "Повтор 21-14-05");
    }

    [Fact]
    public void Clip_saves_whole_or_a_piece_and_never_overwrites()
    {
        var clip = Tone(2.0);
        Assert.Equal(2.0, clip.Seconds, 3);

        var whole = clip.SaveAs(dir, "", 0, clip.Seconds);
        Assert.Equal("Повтор 21-14-05.wav", Path.GetFileName(whole));   // пустое имя — предложенное
        using (var r = new WaveFileReader(whole))
            Assert.Equal(2.0, r.TotalTime.TotalSeconds, 2);

        var piece = clip.SaveAs(dir, "Кусок", 0.5, 1.25);
        using (var r = new WaveFileReader(piece))
            Assert.Equal(0.75, r.TotalTime.TotalSeconds, 2);

        var again = clip.SaveAs(dir, "Кусок", 0, 1);
        Assert.Equal("Кусок (2).wav", Path.GetFileName(again));
        Assert.Throws<ArgumentException>(() => clip.SaveAs(dir, "Пусто", 1.0, 1.005));
    }

    [Fact]
    public void Volume_goes_to_200_percent_with_100_as_unity()
    {
        Assert.Equal(1f, VolumeCurve.ToGain(1f), 5);                    // 100 % — без усиления
        Assert.Equal(0.125f, VolumeCurve.ToGain(0.5f), 5);              // 50 % ≈ −18 дБ, как раньше
        Assert.Equal(4f, VolumeCurve.ToGain(2f), 5);                    // 200 % ≈ +12 дБ
        Assert.Equal(4f, VolumeCurve.ToGain(5f), 5);                    // выше 200 % — не бывает
        Assert.True(VolumeCurve.ToGain(1.5f) > 1f);
        Assert.Equal(1.5f, VolumeCurve.ToLevel(VolumeCurve.ToGain(1.5f)), 4);
        Assert.Equal(0.5f, VolumeCurve.ToLevel(VolumeCurve.ToGain(0.5f)), 4);
        Assert.True(VolumeCurve.IsValidLevel(2f));
        Assert.False(VolumeCurve.IsValidLevel(2.01f));

        Assert.Equal(1f, AudioSettings.Default.HeadphonesLevel);         // по умолчанию — 100 %
        (AudioSettings.Default with { CableLevel = 2f }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (AudioSettings.Default with { MicLevel = 2.5f }).Validate());
    }

    [Fact]
    public void Old_settings_with_auto_palette_switch_to_yellow_xoard()
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{ "version": 1, "palette": "auto", "layout": "dock" }""");
        var old = new ConfigStore(path).Load();
        Assert.Equal("xoard", old.Palette);
        Assert.Equal("dock", old.Layout);
        Assert.Equal(AppConfig.CurrentVersion, old.Version);

        File.WriteAllText(path, """{ "version": 1, "palette": "matcha" }""");
        Assert.Equal("matcha", new ConfigStore(path).Load().Palette);   // свой выбор не трогаем

        Assert.Equal("xoard", new AppConfig().Palette);
        Assert.Null(new AppConfig().SoundsFolder);
    }
}
