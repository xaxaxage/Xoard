using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Devices;
using Soundboard.Core.Audio.Mic;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Audio.Status;
using Soundboard.Core.Config;
using Soundboard.Core.Hotkeys;
using Soundboard.Core.Library;

namespace Soundboard.App.ViewModels;

/// <summary>Вариант в списке устройств.</summary>
public sealed record DeviceOption(string? Id, string Label);

/// <summary>Проблема для показа (InfoBar).</summary>
public sealed record IssueView(string Title, string Message, InfoBarSeverity Severity, string? HelpUrl)
{
    public bool HasHelp => HelpUrl is not null;
}

/// <summary>
/// Главная модель окна: движок, библиотека звуков, настройки, поиск, микшер, устройства.
/// События движка приходят на его потоке уведомлений и переносятся в поток интерфейса.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly ConfigStore store;
    private readonly SoundLibrary library;
    private readonly AudioEngine engine;
    private readonly DispatcherQueueTimer meterTimer;
    private readonly DispatcherQueueTimer saveTimer;
    private readonly DispatcherQueueTimer toastTimer;
    private readonly Dictionary<string, SoundItem> byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SoundItem> byFile = new(StringComparer.OrdinalIgnoreCase);
    private const string StopHotkeyName = "stop";
    private const string SoundHotkeyPrefix = "sound:";
    private HotkeyManager? hotkeys;
    private Soundboard.Core.Control.ControlServer? controlServer;
    private AppConfig config;
    private bool suppressSettings;
    private bool engineRunning;
    private IReadOnlyList<DeviceOption> lastOutputs = [];
    private IReadOnlyList<DeviceOption> lastInputs = [];

    public MainViewModel(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;

        // SOUNDBOARD_DATA_DIR — отдельная папка для настроек и звуков (проверки, не трогая настоящие данные)
        var dataDir = Environment.GetEnvironmentVariable("SOUNDBOARD_DATA_DIR");
        store = new ConfigStore(string.IsNullOrWhiteSpace(dataDir) ? ConfigStore.DefaultPath : Path.Combine(dataDir, "settings.json"));
        config = store.Load();
        library = new SoundLibrary(string.IsNullOrWhiteSpace(dataDir) ? SoundLibrary.DefaultFolder : Path.Combine(dataDir, "Sounds"),
            new WindowsRecycleBin());
        engine = new AudioEngine(AudioEngineOptions.For(config.Audio.LatencyProfile));

        engine.StatusChanged += (_, s) => dispatcher.TryEnqueue(() => ApplyStatus(s));
        engine.PlaybackStarted += (_, e) => dispatcher.TryEnqueue(() => Find(e.Sound.Key)?.OnStarted());
        engine.PlaybackEnded += (_, e) => dispatcher.TryEnqueue(() => Find(e.Sound.Key)?.OnEnded());

        meterTimer = dispatcher.CreateTimer();
        meterTimer.Interval = TimeSpan.FromMilliseconds(40);
        meterTimer.Tick += (_, _) => UpdateMeters();

        saveTimer = dispatcher.CreateTimer();
        saveTimer.Interval = TimeSpan.FromSeconds(1);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => SaveNow();

        toastTimer = dispatcher.CreateTimer();
        toastTimer.Interval = TimeSpan.FromSeconds(5);
        toastTimer.IsRepeating = false;
        toastTimer.Tick += (_, _) => ToastOpen = false;

        suppressSettings = true;
        var a = config.Audio;
        HeadphonesLevel = a.HeadphonesLevel * 100;
        CableLevel = a.CableLevel * 100;
        MicLevel = a.MicLevel * 100;
        MicMuted = a.MicMuted;
        PassthroughEnabled = a.MicPassthroughEnabled;
        ModeIndex = a.PlaybackMode == PlaybackMode.Restart ? 1 : 0;
        LatencyIndex = (int)a.LatencyProfile;
        MicChannelIndex = (int)a.MicChannelMode;
        AnimationsEnabled = config.AnimationsEnabled;
        suppressSettings = false;

        if (store.LoadWarning is { } warning)
            ShowToast(warning, InfoBarSeverity.Warning);
    }

    /// <summary>Попросить окно показать диалог переименования.</summary>
    public event Action<SoundItem>? RenameRequested;

    /// <summary>Попросить окно показать диалог назначения клавиши (null — клавиша «Стоп всё»).</summary>
    public event Action<SoundItem?>? HotkeyRequested;

    /// <summary>Клавиша «Стоп всё» для настроек.</summary>
    public string StopAllHotkeyText => config.StopAllHotkey ?? "не назначена";

    /// <summary>Все звуки в сохранённом порядке.</summary>
    public ObservableCollection<SoundItem> Sounds { get; } = [];

    /// <summary>Звуки, видимые с учётом поиска (источник сетки).</summary>
    public ObservableCollection<SoundItem> VisibleSounds { get; } = [];

    public ObservableCollection<IssueView> Issues { get; } = [];

    public ObservableCollection<DeviceOption> HeadphoneOptions { get; } = [];

    public ObservableCollection<DeviceOption> MicOptions { get; } = [];

    public string SoundsFolder => library.Folder;

    /// <summary>Положение окна из настроек.</summary>
    public WindowPlacement? SavedWindow => config.Window;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial double HeadphonesLevel { get; set; }

    [ObservableProperty]
    public partial double CableLevel { get; set; }

    [ObservableProperty]
    public partial double MicLevel { get; set; }

    [ObservableProperty]
    public partial bool MicMuted { get; set; }

    [ObservableProperty]
    public partial bool PassthroughEnabled { get; set; }

    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial int LatencyIndex { get; set; }

    [ObservableProperty]
    public partial int MicChannelIndex { get; set; }

    [ObservableProperty]
    public partial int HeadphonesIndex { get; set; }

    [ObservableProperty]
    public partial int MicIndex { get; set; }

    [ObservableProperty]
    public partial bool AnimationsEnabled { get; set; }

    [ObservableProperty]
    public partial double HeadphonesMeter { get; set; }

    [ObservableProperty]
    public partial double CableMeter { get; set; }

    [ObservableProperty]
    public partial double MicMeter { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Запуск…";

    [ObservableProperty]
    public partial string StatusGlyph { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string VbCableText { get; set; } = "Проверяю VB-CABLE…";

    [ObservableProperty]
    public partial bool VbCableMissing { get; set; }

    [ObservableProperty]
    public partial bool ToastOpen { get; set; }

    [ObservableProperty]
    public partial string ToastMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity ToastSeverity { get; set; } = InfoBarSeverity.Informational;

    public bool IsSearchEmpty => string.IsNullOrWhiteSpace(SearchText);

    public bool HasNoSounds => Sounds.Count == 0;

    public bool HasNoMatches => Sounds.Count > 0 && VisibleSounds.Count == 0;

    public string ModeText => ModeIndex == 1 ? "Перезапуск" : "Наложение";

    public string ModeGlyph => ModeIndex == 1 ? "" : "";

    public string HeadphonesText => $"{HeadphonesLevel:0}%";

    public string CableText => $"{CableLevel:0}%";

    public string MicText => MicMuted ? "выкл." : $"{MicLevel:0}%";

    public string SoundsCountText => Sounds.Count switch
    {
        0 => "Звуков пока нет",
        _ => $"{Sounds.Count} {Plural(Sounds.Count, "звук", "звука", "звуков")}",
    };

    /// <summary>Запуск: звуки из папки, движок, таймер индикаторов.</summary>
    public async Task InitializeAsync()
    {
        foreach (var file in library.Scan(config.SoundOrder))
        {
            var item = new SoundItem(this, file);
            Sounds.Add(item);
            byFile[file.FileName] = item;
        }

        RefreshVisible();
        StartHotkeys();

        try
        {
            await engine.StartAsync(config.Audio).ConfigureAwait(true);
            engineRunning = true;
            ApplyStatus(engine.Status);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "Звук не запущен";
            StatusGlyph = "";
            StatusSeverity = InfoBarSeverity.Error;
            ShowToast(ex.Message, InfoBarSeverity.Error, sticky: true);
        }

        meterTimer.Start();
        foreach (var item in Sounds.ToList())
            _ = LoadAsync(item);

        // Управление извне (MCP для Claude): named pipe только для текущего пользователя
        var pipe = Environment.GetEnvironmentVariable("SOUNDBOARD_PIPE");
        controlServer = new Soundboard.Core.Control.ControlServer(this,
            string.IsNullOrWhiteSpace(pipe) ? Soundboard.Protocol.ControlProtocol.PipeName : pipe);
        controlServer.Start();
    }

    // ───────── звуки ─────────

    public void Play(SoundItem item, bool previewOnly)
    {
        if (!engineRunning)
        {
            ShowToast("Звуковой движок не запущен — см. сообщение выше.", InfoBarSeverity.Error);
            return;
        }

        if (item.Handle is null)
        {
            ShowToast(item.HasError ? item.Error! : $"«{item.Name}» ещё загружается.", InfoBarSeverity.Informational);
            return;
        }

        var options = new PlayOptions(Outputs: previewOnly ? OutputMask.Headphones : OutputMask.Both);
        var result = engine.Play(item.Handle, options);
        switch (result.Status)
        {
            case PlayStatus.NotLoaded:
                // Вытеснен из памяти — загрузим заново и сыграем
                _ = ReloadAndPlayAsync(item, options);
                break;
            case PlayStatus.NoOutput:
                ShowToast(previewOnly
                    ? "Нет наушников для предпрослушивания — выберите их в настройках."
                    : "Некуда выводить звук: нет ни VB-CABLE, ни наушников. Откройте настройки.", InfoBarSeverity.Warning);
                break;
        }
    }

    [RelayCommand]
    private void StopAll() => engine.StopAll();

    [RelayCommand]
    private void ToggleMode() => ModeIndex = ModeIndex == 1 ? 0 : 1;

    [RelayCommand]
    private void OpenSoundsFolder()
    {
        Directory.CreateDirectory(library.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{library.Folder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenVbCableSite() =>
        Process.Start(new ProcessStartInfo(VbCable.DownloadUrl) { UseShellExecute = true });

    public void RequestRename(SoundItem item) => RenameRequested?.Invoke(item);

    public void RequestHotkey(SoundItem? item) => HotkeyRequested?.Invoke(item);

    [RelayCommand]
    private void AssignStopHotkey() => RequestHotkey(null);

    // ───────── горячие клавиши ─────────

    private void StartHotkeys()
    {
        try
        {
            hotkeys = new HotkeyManager();
            hotkeys.Pressed += OnHotkeyPressed;
        }
        catch (InvalidOperationException ex)
        {
            ShowToast($"Горячие клавиши недоступны: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        var failed = RegisterAllHotkeys();
        if (failed.Count > 0)
            ShowToast($"Не удалось включить клавиши (заняты другой программой): {string.Join(", ", failed)}.", InfoBarSeverity.Warning);
    }

    /// <summary>Регистрирует все сохранённые сочетания; возвращает те, что не удалось.</summary>
    private List<string> RegisterAllHotkeys()
    {
        var failed = new List<string>();
        if (hotkeys is null)
            return failed;

        if (HotkeyGesture.TryParse(config.StopAllHotkey, out var stop) && hotkeys.Register(StopHotkeyName, stop) != HotkeyResult.Registered)
            failed.Add($"«Стоп всё» ({stop})");

        foreach (var (file, text) in config.SoundHotkeys)
        {
            if (!byFile.TryGetValue(file, out var item) || !HotkeyGesture.TryParse(text, out var g))
                continue;
            item.HotkeyText = g.ToString();
            if (hotkeys.Register(SoundHotkeyPrefix + file, g) != HotkeyResult.Registered)
                failed.Add($"«{item.Name}» ({g})");
        }

        return failed;
    }

    /// <summary>Поток клавиш: звук запускается сразу, без ожидания потока интерфейса.</summary>
    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        if (e.Name == StopHotkeyName)
        {
            engine.StopAll();
            return;
        }

        if (!e.Name.StartsWith(SoundHotkeyPrefix, StringComparison.Ordinal)
            || !byFile.TryGetValue(e.Name[SoundHotkeyPrefix.Length..], out var item))
        {
            return;
        }

        var handle = item.Handle;
        var options = new PlayOptions();
        if (handle is null || engine.Play(handle, options).Status == PlayStatus.NotLoaded)
            dispatcher.TryEnqueue(() => Play(item, previewOnly: false));
    }

    /// <summary>Снять все клавиши на время назначения новой (иначе нажатие сработает вместо записи).</summary>
    public void SuspendHotkeys() => hotkeys?.UnregisterAll();

    public void ResumeHotkeys() => RegisterAllHotkeys();

    /// <summary>Текущая клавиша звука (или «Стоп всё», если item = null).</summary>
    public string? CurrentHotkey(SoundItem? item) =>
        item is null ? config.StopAllHotkey : config.SoundHotkeys.GetValueOrDefault(item.File.FileName);

    /// <summary>
    /// Назначить (gesture ≠ null) или убрать клавишу. Возвращает текст ошибки или null.
    /// Вызывается при снятых клавишах (<see cref="SuspendHotkeys"/>), проверка занятости — пробной регистрацией.
    /// </summary>
    public string? SetHotkey(SoundItem? item, HotkeyGesture? gesture)
    {
        if (gesture is { } g)
        {
            if (!g.IsValid)
                return "Нужна обычная клавиша вместе с Ctrl/Alt/Shift или без них — одних модификаторов мало.";

            var text = g.ToString();
            if (item is not null && string.Equals(config.StopAllHotkey, text, StringComparison.OrdinalIgnoreCase))
                return $"{text} уже назначено на «Стоп всё».";
            var owner = config.SoundHotkeys.FirstOrDefault(kv =>
                string.Equals(kv.Value, text, StringComparison.OrdinalIgnoreCase) && (item is null || !string.Equals(kv.Key, item.File.FileName, StringComparison.OrdinalIgnoreCase)));
            if (owner.Key is not null)
                return $"{text} уже назначено звуку «{byFile.GetValueOrDefault(owner.Key)?.Name ?? owner.Key}».";

            // Пробная регистрация: сочетание может быть занято другой программой или Windows
            if (hotkeys is not null)
            {
                var probe = hotkeys.Register("probe", g);
                hotkeys.Unregister("probe");
                if (probe == HotkeyResult.InUse)
                    return $"{text} уже занято другой программой или Windows. Выберите другое сочетание.";
                if (probe != HotkeyResult.Registered)
                    return $"Windows не дала назначить {text}.";
            }
        }

        var gestureText = gesture?.ToString();
        if (item is null)
        {
            config = config with { StopAllHotkey = gestureText };
            OnPropertyChanged(nameof(StopAllHotkeyText));
        }
        else
        {
            var map = new Dictionary<string, string>(config.SoundHotkeys, StringComparer.OrdinalIgnoreCase);
            if (gestureText is null)
                map.Remove(item.File.FileName);
            else
                map[item.File.FileName] = gestureText;
            config = config with { SoundHotkeys = map };
            item.HotkeyText = gestureText ?? "";
        }

        ScheduleSave();
        return null;
    }

    private void MoveHotkey(string oldFile, string? newFile)
    {
        if (!config.SoundHotkeys.TryGetValue(oldFile, out var text))
            return;
        var map = new Dictionary<string, string>(config.SoundHotkeys, StringComparer.OrdinalIgnoreCase);
        map.Remove(oldFile);
        if (newFile is not null)
            map[newFile] = text;
        config = config with { SoundHotkeys = map };
        hotkeys?.Unregister(SoundHotkeyPrefix + oldFile);
        if (newFile is not null && HotkeyGesture.TryParse(text, out var g))
            hotkeys?.Register(SoundHotkeyPrefix + newFile, g);
    }

    /// <summary>Переименовать; возвращает текст ошибки или null.</summary>
    public string? Rename(SoundItem item, string newName)
    {
        try
        {
            var renamed = library.Rename(item.File, newName);
            if (renamed.Path == item.File.Path && renamed.Name == item.Name)
                return null;
            if (item.Handle is not null)
                byKey.Remove(item.Handle.Key);
            var oldFile = item.File.FileName;
            byFile.TryRemove(oldFile, out _);
            byFile[renamed.FileName] = item;
            MoveHotkey(oldFile, renamed.FileName);
            item.File = renamed;
            item.Name = renamed.Name;
            item.Handle = null;
            item.IsLoading = true;
            _ = LoadAsync(item);
            SaveOrder();
            return null;
        }
        catch (SoundLibraryException ex)
        {
            return ex.Message;
        }
    }

    public void Delete(SoundItem item)
    {
        try
        {
            if (item.Handle is not null)
                engine.StopSound(item.Handle);
            library.Delete(item.File);
            if (item.Handle is not null)
                byKey.Remove(item.Handle.Key);
            byFile.TryRemove(item.File.FileName, out _);
            MoveHotkey(item.File.FileName, null);
            Sounds.Remove(item);
            VisibleSounds.Remove(item);
            OnSoundsChanged();
            SaveOrder();
            ShowToast($"«{item.Name}» перемещён в корзину.", InfoBarSeverity.Informational);
        }
        catch (SoundLibraryException ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    public void ShowInFolder(SoundItem item) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.File.Path}\"") { UseShellExecute = true });

    /// <summary>Добавить файлы (из выбора или перетаскивания).</summary>
    public void Import(IReadOnlyList<string> paths)
    {
        var (added, skipped) = library.Import(paths);
        foreach (var file in added)
        {
            if (Sounds.Any(s => string.Equals(s.File.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            var item = new SoundItem(this, file);
            Sounds.Add(item);
            byFile[file.FileName] = item;
            _ = LoadAsync(item);
        }

        RefreshVisible();
        SaveOrder();

        if (skipped.Count > 0)
        {
            ShowToast($"Не добавлено: {string.Join(", ", skipped.Select(Path.GetFileName))}. Поддерживаются: " +
                      string.Join(", ", SoundLibrary.SupportedExtensions), InfoBarSeverity.Warning);
        }
        else if (added.Count > 0)
        {
            ShowToast(added.Count == 1 ? $"Добавлен «{added[0].Name}»." : $"Добавлено звуков: {added.Count}.", InfoBarSeverity.Success);
        }
    }

    /// <summary>Сетка переставила элементы — запомнить новый порядок.</summary>
    public void OnReordered()
    {
        if (!IsSearchEmpty)
            return;
        Sounds.Clear();
        foreach (var item in VisibleSounds)
            Sounds.Add(item);
        SaveOrder();
    }

    private async Task LoadAsync(SoundItem item)
    {
        try
        {
            var handle = await engine.LoadAsync(item.File.Path).ConfigureAwait(true);
            item.SetLoaded(handle);
            byKey[handle.Key] = item;
        }
        catch (SoundLoadException ex)
        {
            item.SetFailed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.SetFailed($"Не удалось прочитать файл: {ex.Message}");
        }
    }

    private async Task ReloadAndPlayAsync(SoundItem item, PlayOptions options)
    {
        await LoadAsync(item).ConfigureAwait(true);
        if (item.Handle is not null)
            engine.Play(item.Handle, options);
    }

    private SoundItem? Find(string key) => byKey.GetValueOrDefault(key);

    private void RefreshVisible()
    {
        var query = SearchText.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? Sounds.ToList()
            : Sounds.Where(s => s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        if (!visible.SequenceEqual(VisibleSounds))
        {
            VisibleSounds.Clear();
            foreach (var item in visible)
                VisibleSounds.Add(item);
        }

        OnSoundsChanged();
    }

    private void OnSoundsChanged()
    {
        OnPropertyChanged(nameof(HasNoSounds));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(SoundsCountText));
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshVisible();
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    // ───────── громкость и настройки ─────────

    partial void OnHeadphonesLevelChanged(double value)
    {
        OnPropertyChanged(nameof(HeadphonesText));
        if (!suppressSettings)
            SetLevel(LevelTarget.Headphones, value);
    }

    partial void OnCableLevelChanged(double value)
    {
        OnPropertyChanged(nameof(CableText));
        if (!suppressSettings)
            SetLevel(LevelTarget.Cable, value);
    }

    partial void OnMicLevelChanged(double value)
    {
        OnPropertyChanged(nameof(MicText));
        if (!suppressSettings)
            SetLevel(LevelTarget.Mic, value);
    }

    partial void OnMicMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MicText));
        if (suppressSettings)
            return;
        engine.SetMicMuted(value);
        UpdateAudio(a => a with { MicMuted = value }, apply: false);
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeGlyph));
        if (suppressSettings)
            return;
        var mode = value == 1 ? PlaybackMode.Restart : PlaybackMode.Overlap;
        engine.PlaybackMode = mode;
        UpdateAudio(a => a with { PlaybackMode = mode }, apply: false);
    }

    partial void OnPassthroughEnabledChanged(bool value)
    {
        if (!suppressSettings)
            UpdateAudio(a => a with { MicPassthroughEnabled = value }, apply: true);
    }

    partial void OnLatencyIndexChanged(int value)
    {
        if (!suppressSettings && value >= 0)
            UpdateAudio(a => a with { LatencyProfile = (LatencyProfile)value }, apply: true);
    }

    partial void OnMicChannelIndexChanged(int value)
    {
        if (!suppressSettings && value >= 0)
            UpdateAudio(a => a with { MicChannelMode = (MicChannelMode)value }, apply: true);
    }

    partial void OnHeadphonesIndexChanged(int value)
    {
        if (suppressSettings || value < 0 || value >= HeadphoneOptions.Count)
            return;
        var id = HeadphoneOptions[value].Id;
        UpdateAudio(a => a with { HeadphonesDeviceId = id }, apply: true);
    }

    partial void OnMicIndexChanged(int value)
    {
        if (suppressSettings || value < 0 || value >= MicOptions.Count)
            return;
        var id = MicOptions[value].Id;
        UpdateAudio(a => a with { MicDeviceId = id }, apply: true);
    }

    partial void OnAnimationsEnabledChanged(bool value)
    {
        if (suppressSettings)
            return;
        config = config with { AnimationsEnabled = value };
        ScheduleSave();
    }

    private void SetLevel(LevelTarget target, double percent)
    {
        var level = (float)Math.Clamp(percent / 100.0, 0, 1);
        engine.SetLevel(target, level);
        UpdateAudio(a => target switch
        {
            LevelTarget.Headphones => a with { HeadphonesLevel = level },
            LevelTarget.Cable => a with { CableLevel = level },
            _ => a with { MicLevel = level },
        }, apply: false);
    }

    private void UpdateAudio(Func<AudioSettings, AudioSettings> change, bool apply)
    {
        config = config with { Audio = change(config.Audio) };
        ScheduleSave();
        if (apply && engineRunning)
            _ = ApplyAsync(config.Audio);
    }

    private async Task ApplyAsync(AudioSettings settings)
    {
        try
        {
            await engine.ApplySettingsAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            ShowToast($"Не удалось применить настройки: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ───────── статус движка ─────────

    private void ApplyStatus(EngineStatus s)
    {
        var errors = s.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = s.Issues.Count(i => i.Severity == IssueSeverity.Warning);
        if (!s.VbCable.Installed)
        {
            StatusText = "Нет VB-CABLE";
            StatusGlyph = "";
            StatusSeverity = InfoBarSeverity.Error;
        }
        else if (errors + warnings > 0)
        {
            StatusText = errors > 0 ? "Есть проблемы" : "Есть замечания";
            StatusGlyph = "";
            StatusSeverity = errors > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        }
        else
        {
            StatusText = "VB-CABLE";
            StatusGlyph = "";
            StatusSeverity = InfoBarSeverity.Success;
        }

        VbCableMissing = !s.VbCable.Installed;
        VbCableText = s.VbCable.Installed
            ? $"Звуки и голос идут в «{s.VbCable.RenderName}». В Discord выберите устройство ввода «{s.VbCable.CaptureName ?? "CABLE Output"}»." +
              (s.VbCable.ListeningApps.Count > 0 ? $" Сейчас слушает: {string.Join(", ", s.VbCable.ListeningApps.Select(a => a.ProcessName))}." : "")
            : "VB-CABLE не найден: без него друзья не услышат звуки. Установите бесплатный драйвер и перезагрузите компьютер.";

        var issues = s.Issues.Select(i => new IssueView(IssueText.Title(i.Code), i.Message, ToSeverity(i.Severity), i.HelpUrl)).ToList();
        if (!issues.SequenceEqual(Issues))
        {
            Issues.Clear();
            foreach (var i in issues)
                Issues.Add(i);
        }

        UpdateDeviceLists(s);
    }

    private void UpdateDeviceLists(EngineStatus s)
    {
        var defaultOut = s.Headphones.Reason is ChoiceReason.WindowsDefault or ChoiceReason.FallbackToWindowsDefault ? s.Headphones.DeviceName : null;
        var outputs = new List<DeviceOption> { new(null, $"Как в Windows{(defaultOut is null ? "" : $" — {defaultOut}")}") };
        outputs.AddRange(s.Outputs.Where(d => !d.IsVbCable).Select(d => new DeviceOption(d.Id, d.FriendlyName)));

        var defaultIn = s.Mic.DeviceName is not null && config.Audio.MicDeviceId is null ? s.Mic.DeviceName : null;
        var inputs = new List<DeviceOption> { new(null, $"Как в Windows{(defaultIn is null ? "" : $" — {defaultIn}")}") };
        inputs.AddRange(s.Inputs.Where(d => !d.IsVbCable).Select(d => new DeviceOption(d.Id,
            d.FriendlyName + (d.LoopbackRisk == LoopbackRisk.None ? "" : " (возвращает звук компьютера — эхо!)"))));

        AddMissing(outputs, config.Audio.HeadphonesDeviceId);
        AddMissing(inputs, config.Audio.MicDeviceId);

        if (outputs.SequenceEqual(lastOutputs) && inputs.SequenceEqual(lastInputs))
            return;
        lastOutputs = outputs;
        lastInputs = inputs;

        suppressSettings = true;
        try
        {
            Replace(HeadphoneOptions, outputs);
            Replace(MicOptions, inputs);
            HeadphonesIndex = IndexOf(HeadphoneOptions, config.Audio.HeadphonesDeviceId);
            MicIndex = IndexOf(MicOptions, config.Audio.MicDeviceId);
        }
        finally
        {
            suppressSettings = false;
        }
    }

    private static void AddMissing(List<DeviceOption> list, string? id)
    {
        if (id is not null && list.All(o => o.Id != id))
            list.Add(new DeviceOption(id, "Выбранное устройство сейчас недоступно"));
    }

    private static int IndexOf(ObservableCollection<DeviceOption> list, string? id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id)
                return i;
        }

        return 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private void UpdateMeters()
    {
        if (!engineRunning)
            return;
        var m = engine.ReadMeters();
        HeadphonesMeter = Smooth(HeadphonesMeter, ToMeter(m.HeadphonesPeakDb));
        CableMeter = Smooth(CableMeter, ToMeter(m.CablePeakDb));
        MicMeter = Smooth(MicMeter, ToMeter(m.MicOutPeakDb));
        foreach (var item in VisibleSounds)
            item.Tick();
    }

    /// <summary>dBFS → 0..1 по шкале −60…0 дБ.</summary>
    private static double ToMeter(float db) => Math.Clamp((db + 60) / 60.0, 0, 1);

    /// <summary>Быстрый подъём, плавный спад.</summary>
    private static double Smooth(double current, double target) => target >= current ? target : current + ((target - current) * 0.25);

    private static InfoBarSeverity ToSeverity(IssueSeverity s) => s switch
    {
        IssueSeverity.Error => InfoBarSeverity.Error,
        IssueSeverity.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    // ───────── уведомления и сохранение ─────────

    public void ShowToast(string message, InfoBarSeverity severity, bool sticky = false)
    {
        ToastMessage = message;
        ToastSeverity = severity;
        ToastOpen = true;
        toastTimer.Stop();
        if (!sticky)
            toastTimer.Start();
    }

    private void SaveOrder()
    {
        config = config with { SoundOrder = Sounds.Select(s => s.File.FileName).ToList() };
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveNow()
    {
        try
        {
            store.Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Не удалось сохранить настройки: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Запомнить положение окна (сохраняется при выходе).</summary>
    public void SetWindowPlacement(WindowPlacement placement) => config = config with { Window = placement };

    public async ValueTask DisposeAsync()
    {
        meterTimer.Stop();
        saveTimer.Stop();
        SaveNow();
        hotkeys?.Dispose();
        if (controlServer is not null)
            await controlServer.DisposeAsync().ConfigureAwait(false);
        await engine.DisposeAsync().ConfigureAwait(false);
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
            return many;
        return mod10 switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }
}
