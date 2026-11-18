using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xoard.App.Theme;
using Xoard.Core.Recording;

namespace Xoard.App.ViewModels;

/// <summary>Палитра в настройках: образец цветов и выбор.</summary>
public sealed partial class PaletteChoice : ObservableObject
{
    public PaletteChoice(string id, string name, Palette sample, Action<string> pick)
    {
        Id = id;
        Name = name;
        Bg = new SolidColorBrush(ThemeManager.ToColor(sample.Bg));
        Card = new SolidColorBrush(ThemeManager.ToColor(sample.Surface));
        Main = new SolidColorBrush(ThemeManager.ToColor(sample.Primary));
        Accent = new SolidColorBrush(ThemeManager.ToColor(sample.Accent));
        PickCommand = new RelayCommand(() => pick(id));
    }

    public string Id { get; }

    public string Name { get; }

    public Brush Bg { get; }

    public Brush Card { get; }

    public Brush Main { get; }

    public Brush Accent { get; }

    public IRelayCommand PickCommand { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Name;
}

/// <summary>Вид: палитра, форма, раскладка, юмор, настройки, живая волна повтора.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Попросить окно выбрать файлы для добавления.</summary>
    public event Action? AddSoundsRequested;

    public ObservableCollection<PaletteChoice> PaletteChoices { get; } = [];

    [ObservableProperty]
    public partial string PaletteId { get; set; } = Palettes.AutoId;

    [ObservableProperty]
    public partial bool IsSoftShape { get; set; }

    [ObservableProperty]
    public partial bool SettingsOpen { get; set; }

    /// <summary>Раскладка главного окна: pult, dock или stream.</summary>
    [ObservableProperty]
    public partial string LayoutId { get; set; } = "pult";

    public bool IsLayoutPult => LayoutId == "pult";

    public bool IsLayoutDock => LayoutId == "dock";

    public bool IsLayoutStream => LayoutId == "stream";

    public bool IsStrictShape
    {
        get => !IsSoftShape;
        set => IsSoftShape = !value;
    }

    /// <summary>Цвет точки состояния в шапке: всё хорошо / замечания / проблема.</summary>
    public Brush StatusDot => ThemeManager.Current.Brush(StatusSeverity switch
    {
        InfoBarSeverity.Error => "Accent",
        InfoBarSeverity.Warning => "Accent",
        _ => "Primary",
    });

    /// <summary>Точка в уведомлении: красная — ошибка, акцентная — всё остальное.</summary>
    public Brush ToastDot => ToastSeverity == InfoBarSeverity.Error
        ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["XRecording"]
        : ThemeManager.Current.Brush("Accent");

    partial void OnToastSeverityChanged(InfoBarSeverity value) => OnPropertyChanged(nameof(ToastDot));

    public string ReplayTitle => $"Повтор · {ReplaySeconds:0} с";

    public string ReplayLengthText => $"{ReplaySeconds:0} с";

    public string ReplayStateText => ReplayEnabled ? "Пишется" : "Выключен";

    public string ReplayTickFar => $"−{ReplaySeconds:0} с";

    public string ReplayTickMid => $"−{ReplaySeconds * 2 / 3:0} с";

    public string ReplayTickNear => $"−{ReplaySeconds / 3:0} с";

    public string ReplaySaveShort => $"Сохранить {ReplaySeconds:0} с";

    public string ReplaySaveHint => config.ReplayHotkey is { } key ? $"или {key} в игре" : "Клавиша — в настройках";

    public string RecordHotkeyShort => config.RecordHotkey ?? "";

    private bool AnimationsOn => AnimationsEnabled && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void InitLook()
    {
        suppressSettings = true;
        PaletteId = config.Palette;
        IsSoftShape = config.Shape == "soft";
        LayoutId = config.Layout is "dock" or "stream" ? config.Layout : "pult";
        suppressSettings = false;

        var windowsDark = ThemeManager.WindowsIsDark();
        PaletteChoices.Add(new PaletteChoice(Palettes.AutoId, "Авто", Palettes.Resolve(Palettes.AutoId, windowsDark), id => PaletteId = id));
        foreach (var p in Palettes.All)
            PaletteChoices.Add(new PaletteChoice(p.Id, p.Name, p, id => PaletteId = id));
        MarkPalette();

        // «Авто» следует теме Windows
        uiSettings.ColorValuesChanged += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (PaletteId == Palettes.AutoId)
                ThemeManager.Current.ApplyPalette(PaletteId, ThemeManager.WindowsIsDark(), AnimationsOn, dispatcher);
        });
    }

    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();

    private void MarkPalette()
    {
        foreach (var c in PaletteChoices)
            c.IsSelected = c.Id == PaletteId;
    }

    partial void OnPaletteIdChanged(string value)
    {
        MarkPalette();
        if (suppressSettings)
            return;
        ThemeManager.Current.ApplyPalette(value, ThemeManager.WindowsIsDark(), AnimationsOn, dispatcher);
        OnPropertyChanged(nameof(StatusDot));
        config = config with { Palette = value };
        ScheduleSave();
    }

    partial void OnIsSoftShapeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStrictShape));
        if (suppressSettings)
            return;
        ThemeManager.Current.ApplyShape(value ? "soft" : "strict");
        config = config with { Shape = value ? "soft" : "strict" };
        ScheduleSave();
    }

    partial void OnStatusSeverityChanged(InfoBarSeverity value) => OnPropertyChanged(nameof(StatusDot));

    partial void OnLayoutIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsLayoutPult));
        OnPropertyChanged(nameof(IsLayoutDock));
        OnPropertyChanged(nameof(IsLayoutStream));
        if (suppressSettings)
            return;
        config = config with { Layout = value };
        ScheduleSave();
    }

    [RelayCommand]
    private void PickLayout(string id) => LayoutId = id is "dock" or "stream" ? id : "pult";

    /// <summary>Сохранить повтор как есть, без окна обрезки.</summary>
    [RelayCommand]
    private Task<SoundItem?> SaveReplayAsIs() => SaveReplayAsync(fromHotkey: true);

    [RelayCommand]
    private void OpenSettings() => SettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => SettingsOpen = false;

    [RelayCommand]
    private void AddSounds() => AddSoundsRequested?.Invoke();

    /// <summary>Сменились длина повтора или клавиша — обновить подписи живой полосы.</summary>
    private void RaiseReplayTexts()
    {
        OnPropertyChanged(nameof(ReplayTitle));
        OnPropertyChanged(nameof(ReplayDivisions));
        OnPropertyChanged(nameof(ReplayLengthText));
        OnPropertyChanged(nameof(ReplayStateText));
        OnPropertyChanged(nameof(ReplayTickFar));
        OnPropertyChanged(nameof(ReplayTickMid));
        OnPropertyChanged(nameof(ReplayTickNear));
        OnPropertyChanged(nameof(ReplaySaveShort));
        OnPropertyChanged(nameof(ReplaySaveHint));
        OnPropertyChanged(nameof(RecordHotkeyShort));
    }

    /// <summary>Живые волны повтора по дорожкам (Discord, игра, голос) для элемента LiveTracksView.</summary>
    public IReadOnlyList<Controls.LiveTrack> LiveTracks => liveTracks ??=
        [.. Tracks.All.Select(kind => new Controls.LiveTrack(Tracks.Title(kind), new LiveFeed(this, kind), new RelayCommand(() => _ = SaveTrackReplay(kind))))];

    /// <summary>Деления на живой волне — каждые 5 секунд.</summary>
    public int ReplayDivisions => Math.Max(1, (int)Math.Round(ReplaySeconds / 5));

    private IReadOnlyList<Controls.LiveTrack>? liveTracks;

    private sealed class LiveFeed(MainViewModel vm, TrackKind track) : Controls.ILiveWaveSource
    {
        public long Written => vm.recorder.LiveBucketsWritten;

        public int Buckets => (int)Math.Round(vm.ReplaySeconds / RecordingService.LivePeakSeconds);

        public double BucketSeconds => RecordingService.LivePeakSeconds;

        public void Copy(float[] peaks, float[] rms)
        {
            if (vm.ReplayEnabled)
            {
                vm.recorder.CopyLivePeaks(track, peaks);
                vm.recorder.CopyLiveRms(track, rms);
            }
            else
            {
                Array.Clear(peaks);
                Array.Clear(rms);
            }
        }

        public void Tick()
        {
            if (vm.ReplayEnabled)
                vm.recorder.UpdateLive();
        }
    }
}
