using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xoard.Core.Audio;

namespace Xoard.Core.Recording;

/// <summary>Что записывать (старый выбор источника; теперь — какие дорожки идут в общий звук, см. <see cref="Tracks.For"/>).</summary>
public enum RecordingSource
{
    /// <summary>Звук компьютера: дорожки «Discord» и «Игра».</summary>
    System,

    /// <summary>Микрофон: дорожка «Голос».</summary>
    Microphone,
}

/// <summary>
/// Запись и «повтор» по трём дорожкам: Discord, игра (весь остальной звук компьютера, кроме Discord и
/// Xoard) и голос (как его слышат друзья). Звук программ пишется петлями процессов (Windows 10 2004+),
/// голос приходит от звукового движка (<see cref="IVoiceTap"/>). Все дорожки лежат на одной шкале
/// времени (<see cref="TrackTimeline"/>), поэтому совпадают по времени; паузы без пакетов — тишина.
/// Повтор — кольцо последних N секунд, из которого по клавише сохраняются дорожки.
/// </summary>
public sealed class RecordingService : IVoiceTap, IAsyncDisposable
{
    /// <summary>Предел длины одной записи.</summary>
    public static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(10);

    /// <summary>Длина одного отрезка живой волны, секунд.</summary>
    public const double LivePeakSeconds = 0.05;

    private const int Rate = AudioFormat.SampleRate;
    private const int BucketFrames = (int)(Rate * LivePeakSeconds);
    private const int LiveLagFrames = Rate / 10;        // волна отстаёт на 0,1 с — чтобы пакеты успели прийти
    private const long BaseFrames = Rate * 3600L;       // шкала начинается не с нуля — без отрицательных кадров
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);

    private readonly Lock gate = new();
    private readonly Func<long> clock;
    private readonly long ticksPerSecond;
    private readonly long origin;
    private readonly Dictionary<TrackKind, TrackTimeline> timelines = Tracks.All.ToDictionary(k => k, _ => new TrackTimeline());
    private readonly Dictionary<TrackKind, (PeakHistory Peaks, PeakHistory Rms)> live =
        Tracks.All.ToDictionary(k => k, _ => (new PeakHistory((int)(120 / LivePeakSeconds)), new PeakHistory((int)(120 / LivePeakSeconds))));
    private readonly AppAudioCaptures? apps;
    private readonly SourcePlacer voicePlacer = new();
    private Timer? appTimer;

    private bool replayEnabled;
    private int replaySeconds = 30;
    private long replayStart;
    private long liveBucket = -1;

    private bool recording;
    private IReadOnlyList<TrackKind>? recordingMix;
    private long recordingStart;
    private float recordingPeak;

    public RecordingService()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency, captureApps: true)
    {
    }

    /// <summary>Для тестов: свои часы и без петель программ.</summary>
    internal RecordingService(Func<long> clock, long ticksPerSecond, bool captureApps)
    {
        this.clock = clock;
        this.ticksPerSecond = ticksPerSecond;
        origin = clock();
        if (captureApps && AppAudioCaptures.Supported)
        {
            apps = new AppAudioCaptures();
            apps.DataAvailable += OnAppData;
        }
    }

    /// <summary>
    /// Последние пики дорожки (0..1, по 50 мс, новый — в конце) для живой волны. Пока повтор выключен,
    /// волна пустая. Возвращает, сколько отрезков настоящих.
    /// </summary>
    public int CopyLivePeaks(TrackKind track, Span<float> dest)
    {
        lock (gate)
            return live[track].Peaks.CopyLatest(dest);
    }

    /// <summary>Средняя громкость (RMS, 0..1) тех же отрезков — для «плотной» части волны.</summary>
    public int CopyLiveRms(TrackKind track, Span<float> dest)
    {
        lock (gate)
            return live[track].Rms.CopyLatest(dest);
    }

    /// <summary>Сколько отрезков живой волны записано за всё время: изменилось — пришёл новый.</summary>
    public long LiveBucketsWritten
    {
        get
        {
            lock (gate)
                return live[TrackKind.Discord].Peaks.Written;
        }
    }

    /// <summary>Досчитать отрезки живой волны до текущего момента. Вызывать часто (каждый кадр) — дёшево.</summary>
    public void UpdateLive()
    {
        lock (gate)
        {
            if (!replayEnabled)
                return;
            var target = (NowPos() - LiveLagFrames) / BucketFrames;
            var capacity = live[TrackKind.Discord].Peaks.Capacity;
            if (liveBucket < 0 || target - liveBucket > capacity)
                liveBucket = Math.Max(liveBucket, target - capacity);
            for (; liveBucket < target; liveBucket++)
            {
                foreach (var (kind, timeline) in timelines)
                {
                    var (peak, rms) = timeline.Measure(liveBucket * BucketFrames, BucketFrames);
                    live[kind].Peaks.Add(peak);
                    live[kind].Rms.Add(rms);
                }
            }
        }
    }

    /// <summary>Последняя ошибка (по-русски) или null.</summary>
    public string? LastError => apps is null && !AppAudioCaptures.Supported
        ? "Дорожки «Discord» и «Игра» нужна Windows 10 версии 2004 или новее — пишется только голос."
        : apps?.LastError;

    /// <summary>Звук программ пишется по дорожкам (петли процессов доступны).</summary>
    public bool CapturesApps => apps is not null;

    public bool ReplayEnabled
    {
        get
        {
            lock (gate)
                return replayEnabled;
        }
    }

    public int ReplaySeconds
    {
        get
        {
            lock (gate)
                return replaySeconds;
        }
    }

    /// <summary>Сколько секунд уже накоплено в буфере повтора.</summary>
    public double ReplayBufferedSeconds
    {
        get
        {
            lock (gate)
                return replayEnabled ? Math.Min(NowPos() - replayStart, (long)replaySeconds * Rate) / (double)Rate : 0;
        }
    }

    /// <summary>Идёт запись.</summary>
    public bool IsRecording
    {
        get
        {
            lock (gate)
                return recording;
        }
    }

    /// <summary>Какие дорожки пойдут в общий звук, если запись сохранится сразу (null — не идёт).</summary>
    public IReadOnlyList<TrackKind>? RecordingMix
    {
        get
        {
            lock (gate)
                return recording ? recordingMix ?? Tracks.All : null;
        }
    }

    public TimeSpan RecordingElapsed
    {
        get
        {
            lock (gate)
                return recording ? TimeSpan.FromSeconds(Math.Min(NowPos() - recordingStart, MaxFrames) / (double)Rate) : TimeSpan.Zero;
        }
    }

    /// <summary>Пик записи (все дорожки) с прошлого вызова (0..1).</summary>
    public float TakeRecordingPeak()
    {
        lock (gate)
        {
            var p = recordingPeak;
            recordingPeak = 0;
            return p;
        }
    }

    /// <summary>Какие программы сейчас пишутся на дорожку (для подписей).</summary>
    public IReadOnlyList<string> AppNames(TrackKind track) => apps?.Names(track) ?? [];

    private static long MaxFrames => (long)(MaxRecording.TotalSeconds * Rate);

    /// <summary>Включить/выключить повтор и задать длину буфера (10–120 с).</summary>
    public async Task ConfigureReplayAsync(bool enabled, int seconds)
    {
        seconds = Math.Clamp(seconds, 10, 120);
        lock (gate)
        {
            if (enabled != replayEnabled || seconds != replaySeconds)
            {
                var now = NowPos();
                foreach (var timeline in timelines.Values)
                    timeline.SetRing(enabled ? seconds * Rate : 0, now);
                replayStart = now;
                liveBucket = -1;
                foreach (var (peaks, rms) in live.Values)
                {
                    peaks.Clear();
                    rms.Clear();
                }
            }

            replayEnabled = enabled;
            replaySeconds = seconds;
        }

        await UpdateCapturesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Начать запись всех дорожек. <paramref name="mix"/> — какие дорожки войдут в общий звук при
    /// сохранении сразу (по клавише, из Claude); null — все.
    /// </summary>
    /// <exception cref="InvalidOperationException">Запись уже идёт.</exception>
    public async Task StartRecordingAsync(IReadOnlyList<TrackKind>? mix = null)
    {
        lock (gate)
        {
            if (recording)
                throw new InvalidOperationException("Запись уже идёт.");
            recordingStart = NowPos();
            foreach (var timeline in timelines.Values)
                timeline.StartRecording(recordingStart, MaxFrames);
            recordingMix = mix;
            recordingPeak = 0;
            recording = true;
        }

        await UpdateCapturesAsync().ConfigureAwait(false);
    }

    /// <summary>Остановить запись и отдать дорожки в памяти, без файлов — для редактора. null — меньше 0,2 с.</summary>
    public async Task<MultiTrackClip?> StopRecordingClipAsync()
    {
        long end;
        lock (gate)
        {
            if (!recording)
                return null;
            end = NowPos();
        }

        await Task.Delay(SettleDelay).ConfigureAwait(false);   // пакеты последних мгновений ещё в пути
        List<ClipTrack> tracks;
        long frames;
        lock (gate)
        {
            frames = end - recordingStart;
            tracks = [.. timelines.Select(t => new ClipTrack(t.Key, t.Value.StopRecording(end)))];
            recording = false;
        }

        await UpdateCapturesAsync().ConfigureAwait(false);
        return frames < Rate / 5 ? null : new MultiTrackClip(tracks, $"Запись {DateTime.Now:HH-mm-ss}");
    }

    /// <summary>
    /// Остановить запись и сохранить сразу: дорожки — в «Дорожки\Имя», общий звук (дорожки из
    /// <see cref="StartRecordingAsync"/>) — в папку звуков. null — записалось меньше 0,2 с.
    /// </summary>
    public async Task<(string? MixPath, string TracksFolder)?> StopRecordingAsync(string folder, string? name)
    {
        var mix = RecordingMix ?? Tracks.All;
        var clip = await StopRecordingClipAsync().ConfigureAwait(false);
        if (clip is null)
            return null;
        return await Task.Run(() => clip.SaveAs(folder, name ?? clip.SuggestedName, 0, clip.Seconds, mix)).ConfigureAwait(false);
    }

    /// <summary>Последние <paramref name="seconds"/> секунд повтора по дорожкам, без файлов — для редактора.</summary>
    /// <exception cref="InvalidOperationException">Повтор выключен или буфер пуст.</exception>
    public async Task<MultiTrackClip> TakeReplayClipAsync(double seconds)
    {
        long end;
        lock (gate)
        {
            if (!replayEnabled)
                throw new InvalidOperationException("Повтор выключен — включите его в настройках, раздел «Запись и повтор».");
            end = NowPos();
        }

        await Task.Delay(SettleDelay).ConfigureAwait(false);
        List<ClipTrack> tracks;
        lock (gate)
        {
            var available = Math.Min(end - replayStart, (long)replaySeconds * Rate);
            var frames = (int)Math.Min(available, Math.Max(1, Math.Round(seconds * Rate)));
            if (frames < Rate / 5)
                throw new InvalidOperationException("В буфере повтора пока почти ничего нет.");
            tracks = [];
            foreach (var (kind, timeline) in timelines)
            {
                var samples = new short[frames * 2];
                timeline.Read(end - frames, samples);
                tracks.Add(new ClipTrack(kind, samples));
            }
        }

        return new MultiTrackClip(tracks, $"Повтор {DateTime.Now:HH-mm-ss}");
    }

    /// <summary>Сохранить последние <paramref name="seconds"/> секунд повтора сразу (дорожки и общий звук из <paramref name="mix"/>, null — все).</summary>
    /// <exception cref="InvalidOperationException">Повтор выключен или буфер пуст.</exception>
    /// <exception cref="ArgumentException">Во всех дорожках тишина.</exception>
    public async Task<(string? MixPath, string TracksFolder)> SaveReplayAsync(double seconds, string folder, string? name, IReadOnlyList<TrackKind>? mix = null)
    {
        var clip = await TakeReplayClipAsync(seconds).ConfigureAwait(false);
        return await Task.Run(() => clip.SaveAs(folder, name ?? clip.SuggestedName, 0, clip.Seconds, mix ?? Tracks.All)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void OnVoice(ReadOnlySpan<float> stereo, int delayFrames)
    {
        lock (gate)
        {
            if (!replayEnabled && !recording)
                return;
            var pos = voicePlacer.Place(NowPos(), stereo.Length / 2, delayFrames);
            Write(TrackKind.Voice, pos, stereo);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            replayEnabled = false;
            recording = false;
        }

        Timer? timer;
        lock (gate)
        {
            timer = appTimer;
            appTimer = null;
        }

        if (timer is not null)
            await timer.DisposeAsync().ConfigureAwait(false);
        if (apps is not null)
            await apps.DisposeAsync().ConfigureAwait(false);
    }

    // ───────── программы ─────────

    /// <summary>Петли программ нужны, пока включён повтор или идёт запись.</summary>
    private async Task UpdateCapturesAsync()
    {
        if (apps is null)
            return;
        Timer? stopped = null;
        lock (gate)
        {
            var needed = replayEnabled || recording;
            if (needed && appTimer is null)
            {
                appTimer = new Timer(_ => _ = apps.RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
            else if (!needed && appTimer is not null)
            {
                stopped = appTimer;
                appTimer = null;
            }
        }

        if (stopped is not null)
        {
            await stopped.DisposeAsync().ConfigureAwait(false);
            await apps.StopAllAsync().ConfigureAwait(false);
        }
    }

    private void OnAppData(AppCapture capture, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        lock (gate)
        {
            if (!replayEnabled && !recording)
                return;
            var pos = capture.Placer.Place(NowPos(), samples.Length / 2);
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                Write(capture.Kind, pos, samples);
        }
    }

    /// <summary>Добавить звук на дорожку (под блокировкой).</summary>
    private void Write(TrackKind kind, long pos, ReadOnlySpan<float> stereo)
    {
        timelines[kind].Add(pos, stereo);
        if (!recording)
            return;
        foreach (var s in stereo)
        {
            var a = Math.Abs(s);
            if (a > recordingPeak)
                recordingPeak = a;
        }
    }

    /// <summary>Текущий кадр шкалы времени.</summary>
    private long NowPos() => BaseFrames + (long)((clock() - origin) * (double)Rate / ticksPerSecond);
}
