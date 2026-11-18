using Xoard.Core.Audio;

namespace Xoard.Core.Recording;

/// <summary>Дорожка записи и повтора.</summary>
public enum TrackKind
{
    /// <summary>Звук приложения Discord (голоса друзей).</summary>
    Discord,

    /// <summary>Весь остальной звук компьютера, кроме Discord и Xoard (игра, браузер, …).</summary>
    Game,

    /// <summary>Мой голос — как его слышат друзья (после шумоподавления и выравнивания).</summary>
    Voice,
}

/// <summary>Названия и порядок дорожек.</summary>
public static class Tracks
{
    /// <summary>Все дорожки в порядке показа.</summary>
    public static IReadOnlyList<TrackKind> All { get; } = [TrackKind.Discord, TrackKind.Game, TrackKind.Voice];

    /// <summary>Название дорожки (оно же имя файла).</summary>
    public static string Title(TrackKind kind) => kind switch
    {
        TrackKind.Discord => "Discord",
        TrackKind.Game => "Игра",
        TrackKind.Voice => "Голос",
        _ => kind.ToString(),
    };

    /// <summary>Что входит в общий звук для источника старого API: компьютер — Discord и игра, микрофон — голос.</summary>
    public static IReadOnlyList<TrackKind> For(RecordingSource source) =>
        source == RecordingSource.Microphone ? [TrackKind.Voice] : [TrackKind.Discord, TrackKind.Game];
}

/// <summary>
/// Одна дорожка клипа в памяти: стерео 16 бит, 48 кГц (вдвое меньше памяти, чем float; в WAV пишется
/// так же 16 бит, поэтому без потерь).
/// </summary>
public sealed record ClipTrack(TrackKind Kind, short[] Stereo)
{
    /// <summary>Тише этого (пик) дорожка считается пустой и не сохраняется.</summary>
    public const float SilentPeak = 0.002f;

    private float? peak;

    public string Title => Tracks.Title(Kind);

    public double Seconds => Stereo.Length / 2.0 / AudioFormat.SampleRate;

    /// <summary>Наибольший уровень дорожки (0..1).</summary>
    public float Peak => peak ??= ComputePeak();

    /// <summary>В дорожке ничего не звучало.</summary>
    public bool IsSilent => Peak < SilentPeak;

    private float ComputePeak()
    {
        var max = 0;
        foreach (var s in Stereo)
        {
            var a = Math.Abs((int)s);
            if (a > max)
                max = a;
        }

        return max / 32768f;
    }
}

/// <summary>
/// Запись или повтор по дорожкам (ещё не файлы). Сохраняется папкой с WAV каждой дорожки
/// и общим звуком из выбранных дорожек.
/// </summary>
public sealed record MultiTrackClip(IReadOnlyList<ClipTrack> Tracks, string SuggestedName)
{
    public double Seconds => Tracks.Count == 0 ? 0 : Tracks.Max(t => t.Seconds);

    public ClipTrack? this[TrackKind kind] => Tracks.FirstOrDefault(t => t.Kind == kind);

    /// <summary>Общий звук: сумма выбранных дорожек (пустые пропускаются; ничего не выбрано — тишина).</summary>
    public AudioClip Mix(IEnumerable<TrackKind> include)
    {
        var frames = (int)Math.Round(Seconds * AudioFormat.SampleRate);
        var sum = new float[frames * 2];
        foreach (var kind in include.Distinct())
        {
            if (this[kind] is not { } track)
                continue;
            var source = track.Stereo;
            var n = Math.Min(source.Length, sum.Length);
            for (var i = 0; i < n; i++)
                sum[i] += source[i] / 32768f;
        }

        return new AudioClip(sum, SuggestedName);
    }

    /// <summary>
    /// Звук для сохранения: одна дорожка (<paramref name="track"/>) или все вместе (null). Имя по
    /// умолчанию — с названием дорожки («Повтор Discord 21-14-05»).
    /// </summary>
    public AudioClip ClipOf(TrackKind? track) => track is { } kind
        ? Mix([kind]) with { SuggestedName = NameFor(kind) }
        : Mix(Recording.Tracks.All);

    /// <summary>«Повтор 21-14-05» → «Повтор Discord 21-14-05».</summary>
    public string NameFor(TrackKind kind)
    {
        var space = SuggestedName.LastIndexOf(' ');
        var title = Recording.Tracks.Title(kind);
        return space > 0 ? $"{SuggestedName[..space]} {title}{SuggestedName[space..]}" : $"{SuggestedName} {title}";
    }
}
