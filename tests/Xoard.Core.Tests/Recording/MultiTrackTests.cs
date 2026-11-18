using Xoard.Core.Audio;
using Xoard.Core.Audio.Sounds;
using Xoard.Core.Recording;

namespace Xoard.Core.Tests.Recording;

public sealed class MultiTrackTests : IDisposable
{
    private const int Rate = AudioFormat.SampleRate;
    private readonly string dir = Directory.CreateTempSubdirectory("xoard-tracks-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static void Near(int expected, short actual) => Assert.InRange((int)actual, expected - 1, expected + 1);

    private static float[] Constant(int frames, float value) => Enumerable.Repeat(value, frames * 2).ToArray();

    private static short[] ReadRing(TrackTimeline timeline, long start, int frames)
    {
        var result = new short[frames * 2];
        timeline.Read(start, result);
        return result;
    }

    // ───────── шкала времени ─────────

    [Fact]
    public void Sources_add_up_on_the_same_place_of_the_timeline()
    {
        var timeline = new TrackTimeline();
        timeline.SetRing(Rate, 0);
        timeline.Add(100, Constant(50, 0.25f));
        timeline.Add(120, Constant(50, 0.25f));

        var read = ReadRing(timeline, 100, 100);
        Near(8192, read[0]);                 // только первый источник
        Near(16384, read[2 * 30]);           // оба вместе
        Near(8192, read[2 * 60]);            // только второй
        Assert.Equal(0, read[2 * 80]);                  // ещё ничего не было
    }

    [Fact]
    public void Ring_forgets_what_is_older_than_its_length_and_silences_reused_places()
    {
        var timeline = new TrackTimeline();
        timeline.SetRing(1000, 0);
        timeline.Add(0, Constant(100, 0.5f));
        timeline.Add(1500, Constant(10, 0.1f));         // кольцо ушло дальше, чем на свою длину

        Assert.All(ReadRing(timeline, 0, 100), s => Assert.Equal(0, s));
        // На месте старого звука (кадры 1000…1099 = те же ячейки кольца) — тишина, а не звук «круг назад»
        Assert.All(ReadRing(timeline, 1000, 100), s => Assert.Equal(0, s));
        Near(3277, ReadRing(timeline, 1500, 1)[0]);
    }

    [Fact]
    public void Loud_sum_is_clipped_not_wrapped()
    {
        var timeline = new TrackTimeline();
        timeline.SetRing(Rate, 0);
        timeline.Add(0, Constant(10, 0.9f));
        timeline.Add(0, Constant(10, 0.9f));
        Assert.Equal(short.MaxValue, ReadRing(timeline, 0, 1)[0]);
    }

    [Fact]
    public void Recording_keeps_only_its_own_time_span()
    {
        var timeline = new TrackTimeline();
        timeline.StartRecording(1000, maxFrames: 3000);
        timeline.Add(900, Constant(200, 0.5f));         // половина — до начала записи
        timeline.Add(3900, Constant(200, 0.5f));        // половина — после предела
        var recorded = timeline.StopRecording(5000);

        Assert.Equal(3000 * 2, recorded.Length);
        Near(16384, recorded[0]);
        Near(16384, recorded[2 * 99]);
        Assert.Equal(0, recorded[2 * 100]);
        Near(16384, recorded[2 * 2999]);
        Assert.False(timeline.IsRecording);
    }

    // ───────── место пакетов ─────────

    [Fact]
    public void Packets_follow_each_other_despite_jitter_and_restart_after_a_pause()
    {
        var placer = new SourcePlacer();
        Assert.Equal(1000 - 480, placer.Place(1000, 480));
        Assert.Equal(1000, placer.Place(1000 + 480 + 300, 480));      // пришёл с опозданием — всё равно подряд
        Assert.Equal(1480, placer.Place(1000 + 960 - 200, 480));      // пришёл раньше — тоже подряд

        var afterPause = 1000 + 960 + (Rate / 2);
        Assert.Equal(afterPause - 480, placer.Place(afterPause, 480)); // полсекунды тишины — заново по часам
        Assert.Equal(5000 - 480 - 960, new SourcePlacer().Place(5000, 480, delayFrames: 960));
    }

    // ───────── выбор программ ─────────

    [Fact]
    public void Apps_are_split_into_discord_and_game_without_xoard_and_without_doubles()
    {
        var processes = new Dictionary<int, ProcessNode>
        {
            [10] = new(4, "explorer.exe"),
            [20] = new(10, "Xoard.exe"),           // это мы
            [21] = new(20, "Xoard.Mcp.exe"),       // наш потомок
            [30] = new(10, "Discord.exe"),
            [31] = new(30, "Discord.exe"),         // процесс звука Discord
            [40] = new(10, "steam.exe"),
            [41] = new(40, "cs2.exe"),             // игра — потомок Steam
            [50] = new(10, "chrome.exe"),
            [51] = new(50, "chrome.exe"),
            [60] = new(10, "launcher.exe"),
            [61] = new(60, "Discord.exe"),         // Discord, запущенный лаунчером
        };
        var sessions = new[] { 0, 10, 20, 21, 31, 40, 41, 51, 60, 61 };

        var chosen = AppSelection.Choose(sessions, processes, self: 20);

        Assert.Equal(TrackKind.Discord, chosen[31]);
        Assert.Equal(TrackKind.Discord, chosen[61]);
        Assert.Equal(TrackKind.Game, chosen[40]);          // Steam пишется вместе с игрой…
        Assert.False(chosen.ContainsKey(41));             // …поэтому игра отдельно не пишется
        Assert.Equal(TrackKind.Game, chosen[51]);
        Assert.False(chosen.ContainsKey(10));             // Проводник — предок Xoard
        Assert.False(chosen.ContainsKey(20));
        Assert.False(chosen.ContainsKey(21));
        Assert.False(chosen.ContainsKey(60));             // лаунчер — предок Discord, на «Игру» нельзя
        Assert.False(chosen.ContainsKey(0));
    }

    // ───────── клип по дорожкам ─────────

    private static ClipTrack Track(TrackKind kind, int frames, short value) => new(kind, Enumerable.Repeat(value, frames * 2).ToArray());

    [Fact]
    public void Mix_sums_only_chosen_tracks()
    {
        var clip = new MultiTrackClip([Track(TrackKind.Discord, 100, 8192), Track(TrackKind.Game, 100, 4096), Track(TrackKind.Voice, 100, 0)], "Повтор");
        Assert.Equal(0.375f, clip.Mix(Tracks.All).Stereo[10], 3);
        Assert.Equal(0.125f, clip.Mix([TrackKind.Game]).Stereo[10], 3);
        Assert.Equal(0f, clip.Mix([]).Stereo[10]);
        Assert.True(clip[TrackKind.Voice]!.IsSilent);
    }

    [Fact]
    public void Saving_writes_each_audible_track_and_the_mix()
    {
        var clip = new MultiTrackClip([Track(TrackKind.Discord, Rate, 8192), Track(TrackKind.Game, Rate, 4096), Track(TrackKind.Voice, Rate, 0)], "Повтор");
        var (mixPath, folder) = clip.SaveAs(dir, "Смешной момент", 0.25, 0.75, [TrackKind.Discord, TrackKind.Game]);

        Assert.Equal(Path.Combine(dir, MultiTrackClip.TracksFolderName, "Смешной момент"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "Discord.wav")));
        Assert.True(File.Exists(Path.Combine(folder, "Игра.wav")));
        Assert.False(File.Exists(Path.Combine(folder, "Голос.wav")));   // тишина не сохраняется
        Assert.Equal(Path.Combine(dir, "Смешной момент.wav"), mixPath);
        Assert.Equal(0.5, SoundDecoder.Decode(mixPath!, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken).Duration.TotalSeconds, 2);

        // Второй раз с тем же именем — новые папка и файл, старые не трогаются
        var (again, againFolder) = clip.SaveAs(dir, "Смешной момент", 0, 1, [TrackKind.Voice]);
        Assert.Null(again);                                               // голос пуст — общего звука нет
        Assert.EndsWith("Смешной момент (2)", againFolder, StringComparison.Ordinal);

        var silent = new MultiTrackClip([Track(TrackKind.Voice, Rate, 0)], "Тишина");
        Assert.Throws<ArgumentException>(() => silent.SaveAs(dir, "x", 0, 1, Tracks.All));
    }

    // ───────── служба целиком (свои часы, без петель программ) ─────────

    [Fact]
    public async Task Voice_lands_in_the_replay_at_its_time_and_in_the_live_wave()
    {
        long now = 0;
        await using var service = new RecordingService(() => now, Rate, captureApps: false);
        await service.ConfigureReplayAsync(true, 10);

        now += Rate;                                          // секунда тишины
        var block = Constant(480, 0.5f);
        for (var i = 0; i < 10; i++)
        {
            now += 480;
            service.OnVoice(block, 0);
        }

        Assert.Equal(1.1, service.ReplayBufferedSeconds, 2);
        Assert.Equal(1.1, (await service.TakeReplayClipAsync(5)).Seconds, 2);   // больше, чем накоплено, не бывает
        var clip = await service.TakeReplayClipAsync(1);
        var voice = clip[TrackKind.Voice]!;
        Assert.Equal(Rate * 2, voice.Stereo.Length);
        Assert.Equal(0, voice.Stereo[voice.Stereo.Length - (2 * 4800) - 2]);   // перед голосом — тишина
        Near(16384, voice.Stereo[voice.Stereo.Length - (2 * 4800)]);
        Near(16384, voice.Stereo[^1]);
        Assert.True(clip[TrackKind.Discord]!.IsSilent);
        Assert.True(clip[TrackKind.Game]!.IsSilent);

        now += Rate / 5;                                      // живая волна отстаёт на 0,1 с
        service.UpdateLive();
        var peaks = new float[10];
        service.CopyLivePeaks(TrackKind.Voice, peaks);
        Assert.Contains(peaks, p => p > 0.49f);
        service.CopyLivePeaks(TrackKind.Discord, peaks);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public async Task Recording_gives_all_tracks_from_start_to_stop()
    {
        long now = Rate;
        await using var service = new RecordingService(() => now, Rate, captureApps: false);
        service.OnVoice(Constant(480, 0.5f), 0);              // до записи и без повтора — никуда

        await service.StartRecordingAsync([TrackKind.Voice]);
        Assert.Equal([TrackKind.Voice], service.RecordingMix);
        for (var i = 0; i < 50; i++)
        {
            now += 480;
            service.OnVoice(Constant(480, 0.25f), 0);
        }

        Assert.Equal(0.5, service.RecordingElapsed.TotalSeconds, 2);
        Assert.True(service.TakeRecordingPeak() > 0.24f);
        var clip = await service.StopRecordingClipAsync();

        Assert.NotNull(clip);
        Assert.False(service.IsRecording);
        Assert.Equal(3, clip.Tracks.Count);
        Assert.Equal(0.5, clip.Seconds, 3);
        Assert.All(clip[TrackKind.Voice]!.Stereo, s => Near(8192, s));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TakeReplayClipAsync(5));   // повтор выключен
    }
}
