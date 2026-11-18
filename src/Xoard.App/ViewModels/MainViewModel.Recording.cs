using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Xoard.Core.Audio.Sounds;
using Xoard.Core.Library;
using Xoard.Core.Recording;

namespace Xoard.App.ViewModels;

/// <summary>На что назначается горячая клавиша.</summary>
public enum HotkeyKind
{
    StopAll,
    Record,
    Replay,
    Sound,
}

/// <summary>Цель горячей клавиши: «Стоп всё», «Запись», «Повтор» или звук.</summary>
public sealed record HotkeyTarget(HotkeyKind Kind, SoundItem? Sound = null)
{
    public static HotkeyTarget StopAll { get; } = new(HotkeyKind.StopAll);

    public static HotkeyTarget Record { get; } = new(HotkeyKind.Record);

    public static HotkeyTarget Replay { get; } = new(HotkeyKind.Replay);

    public static HotkeyTarget ForSound(SoundItem sound) => new(HotkeyKind.Sound, sound);

    public string Title => Kind switch
    {
        HotkeyKind.StopAll => "Клавиша «Стоп всё»",
        HotkeyKind.Record => "Клавиша «Начать/остановить запись»",
        HotkeyKind.Replay => "Клавиша «Сохранить повтор»",
        _ => $"Клавиша для «{Sound?.Name}»",
    };
}

/// <summary>
/// Клип для редактора: новая запись или повтор (ещё не файл) либо звук, который обрезают
/// (<paramref name="TrimTarget"/>). У записи и повтора есть дорожки (<paramref name="Tracks"/>):
/// <paramref name="Clip"/> — выбранная дорожка (<paramref name="Track"/>) или все вместе (null).
/// </summary>
public sealed record ClipDraft(AudioClip Clip, SoundItem? TrimTarget, string Title, string Subtitle, MultiTrackClip? Tracks = null, TrackKind? Track = null)
{
    public bool IsTrim => TrimTarget is not null;

    /// <summary>Запись или повтор по дорожкам: одна дорожка или все вместе (null).</summary>
    public static ClipDraft FromTracks(MultiTrackClip tracks, string title, string subtitle, TrackKind? track = null) =>
        new(tracks.ClipOf(track), null, title, subtitle, tracks, track);
}

/// <summary>Запись, повтор и обрезка.</summary>
public sealed partial class MainViewModel
{
    private readonly RecordingService recorder = new();
    private bool recordingBusy;

    private Xoard.Core.Audio.Sounds.SoundHandle? previewHandle;

    /// <summary>Открыть редактор клипа: новая запись или повтор (ещё не файл) либо обрезка звука.</summary>
    public event Action<ClipDraft>? ClipEditRequested;

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingTimeText { get; set; } = "0:00.0";

    [ObservableProperty]
    public partial double RecordingMeter { get; set; }

    [ObservableProperty]
    public partial bool ReplayEnabled { get; set; }

    [ObservableProperty]
    public partial double ReplaySeconds { get; set; } = 30;

    [ObservableProperty]
    public partial string ReplayStatusText { get; set; } = "";

    public string RecordButtonText => IsRecording ? "Остановить запись" : "Начать запись";

    public string RecordButtonGlyph => IsRecording ? "" : "";

    public string ReplaySaveText => $"Сохранить последние {ReplaySeconds:0} с";

    public bool IsNotRecording => !IsRecording;

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotRecording));
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordButtonGlyph));
    }

    partial void OnReplayEnabledChanged(bool value)
    {
        RaiseReplayTexts();
        if (suppressSettings)
            return;
        config = config with { ReplayEnabled = value };
        ScheduleSave();
        _ = ApplyReplayAsync();
    }

    partial void OnReplaySecondsChanged(double value)
    {
        OnPropertyChanged(nameof(ReplaySaveText));
        RaiseReplayTexts();
        if (suppressSettings)
            return;
        config = config with { ReplaySeconds = (int)Math.Round(value) };
        ScheduleSave();
        _ = ApplyReplayAsync();
    }

    private async Task InitRecordingAsync()
    {
        suppressSettings = true;
        ReplayEnabled = config.ReplayEnabled;
        ReplaySeconds = Math.Clamp(config.ReplaySeconds, 10, 120);
        suppressSettings = false;
        engine.VoiceTap = recorder;
        await ApplyReplayAsync().ConfigureAwait(true);
    }

    private async Task ApplyReplayAsync()
    {
        await recorder.ConfigureReplayAsync(ReplayEnabled, (int)Math.Round(ReplaySeconds)).ConfigureAwait(true);
        if (ReplayEnabled && !recorder.ReplayEnabled && recorder.LastError is { } error)
            ShowToast(error, InfoBarSeverity.Warning);
    }

    [RelayCommand]
    private Task<SoundItem?> ToggleRecording() => ToggleRecordingAsync(fromHotkey: false);

    [RelayCommand]
    private Task<SoundItem?> SaveReplay() => SaveReplayAsync(fromHotkey: false);

    /// <summary>
    /// Начать или остановить запись. Из окна после остановки открывается редактор клипа (файла ещё нет);
    /// по клавише (в игре) запись сразу сохраняется.
    /// </summary>
    public async Task<SoundItem?> ToggleRecordingAsync(bool fromHotkey, string? name = null)
    {
        if (recordingBusy)
            return null;
        recordingBusy = true;
        try
        {
            if (!recorder.IsRecording)
            {
                await recorder.StartRecordingAsync().ConfigureAwait(true);
                IsRecording = true;
                if (fromHotkey)
                    ShowToast("Запись началась — нажмите клавишу ещё раз, чтобы остановить.", InfoBarSeverity.Informational);
                return null;
            }

            if (!fromHotkey)
            {
                var clip = await recorder.StopRecordingClipAsync().ConfigureAwait(true);
                IsRecording = false;
                if (clip is null)
                {
                    ShowToast("Запись слишком короткая — ничего не сохранено.", InfoBarSeverity.Warning);
                    return null;
                }

                ClipEditRequested?.Invoke(ClipDraft.FromTracks(clip, "Новая запись", $"Длина {Clock(clip.Seconds)}"));
                return null;
            }

            var path = await recorder.StopRecordingAsync(library.Folder, name).ConfigureAwait(true);
            IsRecording = false;
            if (path is null)
            {
                ShowToast("Запись слишком короткая — ничего не сохранено.", InfoBarSeverity.Warning);
                return null;
            }

            var item = AddRecordedFile(path);
            ShowToast($"Запись сохранена: «{item.Name}».", InfoBarSeverity.Success);
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            IsRecording = recorder.IsRecording;
            ShowToast(ex.Message, InfoBarSeverity.Error);
            return null;
        }
        finally
        {
            recordingBusy = false;
        }
    }

    /// <summary>
    /// Последние N секунд повтора: из окна — в редактор клипа (файла ещё нет), по клавише — сразу новым звуком.
    /// <paramref name="track"/> — только одна дорожка (кнопка на полосе), null — все вместе.
    /// </summary>
    public async Task<SoundItem?> SaveReplayAsync(bool fromHotkey, double? seconds = null, string? name = null, TrackKind? track = null)
    {
        try
        {
            if (!fromHotkey)
            {
                var clip = await recorder.TakeReplayClipAsync(seconds ?? ReplaySeconds).ConfigureAwait(true);
                ClipEditRequested?.Invoke(ClipDraft.FromTracks(clip, "Повтор", $"Последние {Clock(clip.Seconds)}", track));
                return null;
            }

            var path = await recorder.SaveReplayAsync(seconds ?? ReplaySeconds, library.Folder, name, track is { } one ? [one] : null).ConfigureAwait(true);
            var item = AddRecordedFile(path);
            ShowToast($"Повтор сохранён: «{item.Name}».", InfoBarSeverity.Success);
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message, InfoBarSeverity.Warning);
            return null;
        }
    }

    /// <summary>Кнопка на полосе дорожки: последние секунды только этой дорожки — в редактор.</summary>
    private Task<SoundItem?> SaveTrackReplay(TrackKind track) => SaveReplayAsync(fromHotkey: false, track: track);

    /// <summary>Файл уже в папке звуков — добавить плитку и загрузить.</summary>
    private SoundItem AddRecordedFile(string path)
    {
        var (added, _) = library.Import([path]);
        var file = added.Count > 0 ? added[0] : new SoundFile(path, Path.GetFileNameWithoutExtension(path));
        var item = new SoundItem(this, file);
        Sounds.Add(item);
        byFile[file.FileName] = item;
        _ = LoadAsync(item);
        RefreshVisible();
        SaveOrder();
        return item;
    }

    /// <summary>Обрезать звук: открыть его в редакторе клипа.</summary>
    public async void RequestTrim(SoundItem item)
    {
        try
        {
            var sound = await SoundDecoder.DecodeAsync(item.File.Path).ConfigureAwait(true);
            ClipEditRequested?.Invoke(new ClipDraft(new AudioClip(sound.Samples, item.Name), item, "Обрезка", $"{item.Name} · {Clock(sound.Duration.TotalSeconds)}"));
        }
        catch (Exception ex) when (ex is SoundLoadException or IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Сохранить клип (весь или кусок [start, end)) новым звуком. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SaveClipAsync(AudioClip clip, string name, double start, double end)
    {
        try
        {
            var folder = library.Folder;
            var path = await Task.Run(() => clip.SaveAs(folder, name, start, end)).ConfigureAwait(true);
            var item = AddRecordedFile(path);
            ShowToast($"Сохранено: «{item.Name}».", InfoBarSeverity.Success);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Прослушать кусок клипа в наушниках (только себе); предыдущее прослушивание останавливается.</summary>
    public void PreviewClip(AudioClip clip, double start, double end)
    {
        StopPreview();
        var slice = AudioClips.Slice(clip.Stereo, start, end);
        if (slice.Length == 0 || !engineRunning)
            return;
        previewHandle = engine.AddSound("предпрослушивание", DecodedSound.FromSamples(slice));
        engine.Play(previewHandle, new Xoard.Core.Audio.PlayOptions(Outputs: Xoard.Core.Audio.OutputMask.Headphones));
    }

    public void StopPreview()
    {
        if (previewHandle is not null)
            engine.StopSound(previewHandle);
        previewHandle = null;
    }

    /// <summary>«0:12.4» для подписей.</summary>
    private static string Clock(double seconds) =>
        $"{(int)(seconds / 60)}:{seconds % 60:00.0}".Replace(',', '.');

    /// <summary>
    /// Обрезать звук до [start, end) секунд: новый WAV вместо файла, оригинал — в корзину.
    /// Возвращает текст ошибки или null.
    /// </summary>
    public async Task<string?> TrimAsync(SoundItem item, double start, double end)
    {
        var temp = Path.Combine(library.Folder, $"~trim-{Guid.NewGuid():N}.tmp");
        try
        {
            var source = item.File.Path;
            await Task.Run(() => AudioClips.TrimToWav(source, start, end, temp)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ArgumentException or SoundLoadException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return ex.Message;
        }

        if (item.Handle is not null)
            engine.StopSound(item.Handle);

        try
        {
            library.Delete(item.File);
        }
        catch (SoundLibraryException ex)
        {
            TryDelete(temp);
            return ex.Message;
        }

        var target = Path.Combine(library.Folder, item.Name + ".wav");
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(library.Folder, $"{item.Name} ({i}).wav");
        File.Move(temp, target);

        var oldFile = item.File.FileName;
        var renamed = new SoundFile(target, Path.GetFileNameWithoutExtension(target));
        if (item.Handle is not null)
            byKey.Remove(item.Handle.Key);
        byFile.TryRemove(oldFile, out _);
        byFile[renamed.FileName] = item;
        MoveHotkey(oldFile, renamed.FileName);
        item.File = renamed;
        item.Name = renamed.Name;
        item.Handle = null;
        item.IsLoading = true;
        await LoadAsync(item).ConfigureAwait(true);
        SaveOrder();
        ShowToast($"«{item.Name}» обрезан, оригинал — в корзине.", InfoBarSeverity.Success);
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Не страшно — временный файл
        }
    }

    /// <summary>Таймер окна: время записи, уровень, заполнение повтора.</summary>
    private void UpdateRecordingUi()
    {
        var recording = recorder.IsRecording;
        if (IsRecording != recording)
            IsRecording = recording;
        if (recording)
        {
            var t = recorder.RecordingElapsed;
            RecordingTimeText = $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
            var peak = recorder.TakeRecordingPeak();
            var db = peak > 0 ? 20 * Math.Log10(peak) : -120;
            var level = Math.Clamp((db + 60) / 60.0, 0, 1);
            RecordingMeter = level >= RecordingMeter ? level : RecordingMeter + ((level - RecordingMeter) * 0.25);
        }
        else if (RecordingMeter > 0)
        {
            RecordingMeter = 0;
        }

        ReplayStatusText = !ReplayEnabled
            ? "Повтор выключен."
            : recorder.ReplayEnabled
                ? $"В буфере {recorder.ReplayBufferedSeconds:0} из {ReplaySeconds:0} с · " +
                  (recorder.CapturesApps ? "Discord, игра и голос по дорожкам" : "только голос")
                : "Повтор не запущен" + (recorder.LastError is { } e ? $": {e}" : ".");
    }
}
