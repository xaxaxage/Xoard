using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xoard.App.ViewModels;

namespace Xoard.App.Views;

/// <summary>
/// Настройки: окно поверх главного — разделы слева (01–09), содержимое раздела справа.
/// Окно создаётся один раз, поэтому при следующем открытии остаётся последний раздел.
/// </summary>
public sealed partial class SettingsSheet : UserControl
{
    private readonly Dictionary<string, (StackPanel Page, string Title, string Subtitle)> pages;
    private bool syncing;

    public SettingsSheet(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        pages = new()
        {
            ["sound"] = (PageSound, "Звук и устройства", "Куда идут звуки и ваш голос"),
            ["keys"] = (PageKeys, "Горячие клавиши", "Работают в любой программе и в играх"),
            ["record"] = (PageRecord, "Запись и повтор", "Последние секунды звука компьютера — всегда под рукой"),
            ["look"] = (PageLook, "Вид", "Раскладка, палитра и форма — меняются сразу"),
            ["folder"] = (PageFolder, "Папка звуков", "Где лежат звуки, записи и повторы"),
            ["cable"] = (PageCable, "VB-CABLE и Discord", "Чтобы друзья слышали звуки"),
            ["startup"] = (PageStartup, "Запуск и трей", "Xoard работает и со свёрнутым окном"),
            ["updates"] = (PageUpdates, "Обновления", "Новая версия ставится только по кнопке"),
            ["claude"] = (PageClaude, "Claude Desktop", "Управление звуками из чата с Claude"),
        };

        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            Vm.Sounds.CollectionChanged += OnSoundsChanged;
            SyncSegments();
            UpdateFolderStats();
        };
        Unloaded += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            Vm.Sounds.CollectionChanged -= OnSoundsChanged;
        };
    }

    public MainViewModel Vm { get; }

    // ───────── разделы ─────────

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (pages is null || sender is not RadioButton { Tag: string key } || !pages.TryGetValue(key, out var shown))
            return;
        foreach (var (page, _, _) in pages.Values)
            page.Visibility = page == shown.Page ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = shown.Title;
        PageSubtitle.Text = shown.Subtitle;
        PageScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (key == "folder")
            UpdateFolderStats();
    }

    // ───────── переключатели из нескольких вариантов ─────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ModeIndex) or nameof(MainViewModel.LatencyIndex) or nameof(MainViewModel.MicChannelIndex))
            SyncSegments();
    }

    private void SyncSegments()
    {
        syncing = true;
        Select([ModeOverlay, ModeRestart], Vm.ModeIndex);
        Select([LatencyNormal, LatencySafe, LatencyLow], Vm.LatencyIndex);
        Select([ChannelBoth, ChannelLeft, ChannelRight], Vm.MicChannelIndex);
        syncing = false;
    }

    private static void Select(RadioButton[] group, int index)
    {
        for (var i = 0; i < group.Length; i++)
            group[i].IsChecked = i == index;
    }

    private static int TagOf(object sender) =>
        int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!syncing)
            Vm.ModeIndex = TagOf(sender);
    }

    private void OnLatencyChecked(object sender, RoutedEventArgs e)
    {
        if (!syncing)
            Vm.LatencyIndex = TagOf(sender);
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        if (!syncing)
            Vm.MicChannelIndex = TagOf(sender);
    }

    // ───────── папка звуков ─────────

    private void OnSoundsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateFolderStats();

    private void UpdateFolderStats()
    {
        SoundsStat.Text = Vm.Sounds.Count.ToString(CultureInfo.InvariantCulture);
        HotkeysStat.Text = Vm.Sounds.Count(s => s.HasHotkey).ToString(CultureInfo.InvariantCulture);
    }

    // ───────── функции для x:Bind ─────────

    public static string VersionCap(string version) => $"XOARD {version}";

    public static string CableStatus(bool missing) => missing ? "НЕТ VB-CABLE" : "КАБЕЛЬ НАЙДЕН";

    public static Brush CableDot(bool missing) =>
        (Brush)Application.Current.Resources[missing ? "XRecording" : "XOkDot"];

    public static string UpdateCap(string version) => $"ЕСТЬ ВЕРСИЯ {version}";

    public static Visibility AnyItems(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>«Ctrl+Alt+S» → [Ctrl] [Alt] [S]; «не назначена» → пусто.</summary>
    public static IList<string> KeyParts(string text)
    {
        if (IsUnassigned(text))
            return [];
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (text.EndsWith("++", StringComparison.Ordinal))
            parts.Add("+");
        return parts;
    }

    public static Visibility Unassigned(string text) => IsUnassigned(text) ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsUnassigned(string text) =>
        string.IsNullOrWhiteSpace(text) || text.StartsWith("не ", StringComparison.Ordinal);
}
