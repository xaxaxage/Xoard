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
    private float[] liveRaw = [];

    /// <summary>Попросить окно выбрать файлы для добавления.</summary>
    public event Action? AddSoundsRequested;

    /// <summary>Новые пики живой волны «последние N секунд» (0..1, новый — в конце), раз в 40 мс.</summary>
    public event Action<float[]>? LiveWaveUpdated;

    public ObservableCollection<PaletteChoice> PaletteChoices { get; } = [];

    [ObservableProperty]
    public partial string PaletteId { get; set; } = Palettes.AutoId;

    [ObservableProperty]
    public partial bool IsSoftShape { get; set; }

    [ObservableProperty]
    public partial bool HumorEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SettingsOpen { get; set; }

    /// <summary>Раскладка главного окна: pult, dock или stream.</summary>
    [ObservableProperty]
    public partial string LayoutId { get; set; } = "pult";

    public bool IsLayoutPult => LayoutId == "pult";

    public bool IsLayoutDock => LayoutId == "dock";

    public bool IsLayoutStream => LayoutId == "stream";

    /// <summary>Откуда пишет запись — коротко, для кнопки рядом с записью.</summary>
    public string RecordSourceShort => RecordSourceIndex == 1 ? "Микрофон" : "Компьютер";

    public string RecordSourceGlyph => RecordSourceIndex == 1 ? "" : "";

    public bool IsStrictShape
    {
        get => !IsSoftShape;
        set => IsSoftShape = !value;
    }

    public string HumorLine => HumorLines.ForToday(HumorEnabled);

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

    public bool RecordFromSystem
    {
        get => RecordSourceIndex == 0;
        set
        {
            if (value)
                RecordSourceIndex = 0;
        }
    }

    public bool RecordFromMic
    {
        get => RecordSourceIndex == 1;
        set
        {
            if (value)
                RecordSourceIndex = 1;
        }
    }

    private bool AnimationsOn => AnimationsEnabled && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void InitLook()
    {
        suppressSettings = true;
        PaletteId = config.Palette;
        IsSoftShape = config.Shape == "soft";
        LayoutId = config.Layout is "dock" or "stream" ? config.Layout : "pult";
        HumorEnabled = config.HumorEnabled;
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

    partial void OnHumorEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(HumorLine));
        if (suppressSettings)
            return;
        config = config with { HumorEnabled = value };
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

    [RelayCommand]
    private void ToggleRecordSource() => RecordSourceIndex = RecordSourceIndex == 1 ? 0 : 1;

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
        OnPropertyChanged(nameof(ReplayLengthText));
        OnPropertyChanged(nameof(ReplayStateText));
        OnPropertyChanged(nameof(ReplayTickFar));
        OnPropertyChanged(nameof(ReplayTickMid));
        OnPropertyChanged(nameof(ReplayTickNear));
        OnPropertyChanged(nameof(ReplaySaveShort));
        OnPropertyChanged(nameof(ReplaySaveHint));
        OnPropertyChanged(nameof(RecordHotkeyShort));
    }

    /// <summary>
    /// Живая волна: пики последних N секунд звука компьютера, по два отрезка в точку,
    /// в шкале громкости (−54…0 дБ), чтобы и тихий звук был виден.
    /// </summary>
    private void UpdateLiveWave()
    {
        var handler = LiveWaveUpdated;
        if (handler is null)
            return;

        var buckets = (int)Math.Round(ReplaySeconds / RecordingService.LivePeakSeconds);
        if (liveRaw.Length != buckets)
            liveRaw = new float[buckets];
        if (ReplayEnabled)
            recorder.CopyLivePeaks(liveRaw);
        else
            Array.Clear(liveRaw);

        var points = new float[Math.Max(2, buckets / 2)];
        for (var i = 0; i < points.Length; i++)
        {
            var v = Math.Max(liveRaw[Math.Min(liveRaw.Length - 1, 2 * i)], liveRaw[Math.Min(liveRaw.Length - 1, (2 * i) + 1)]);
            var db = 20 * Math.Log10(Math.Max(v, 1e-5));
            points[i] = (float)Math.Clamp((db + 54) / 54, 0, 1);
        }

        handler(points);
    }
}
