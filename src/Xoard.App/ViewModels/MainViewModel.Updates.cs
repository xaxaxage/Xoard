using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Xoard.App.Updates;

namespace Xoard.App.ViewModels;

/// <summary>
/// Обновления: проверка (при запуске и раз в 6 часов), скачивание и установка — только по кнопке.
/// Плюс путь к серверу для Claude Desktop и ссылка на установщик для друзей.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Updater updater = new();
    private DispatcherQueueTimer? updateTimer;

    /// <summary>Обновление скачано: окно аккуратно закрывается и вызывает <see cref="ApplyPendingUpdate"/>.</summary>
    public event Action? UpdateReadyToApply;

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerTitle), nameof(UpdateButtonText))]
    public partial string UpdateVersion { get; set; } = "";

    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool UpdateBannerOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateIdle))]
    public partial bool UpdateBusy { get; set; }

    [ObservableProperty]
    public partial double UpdateProgress { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "";

    public bool UpdateIdle => !UpdateBusy;

    public string AppVersion => CurrentAppVersion();

    public string AppVersionText => updater.IsInstalled ? $"Xoard {AppVersion}" : $"Xoard {AppVersion} · сборка из исходников";

    public string UpdateBannerTitle => $"Вышла версия {UpdateVersion}";

    public string UpdateButtonText => $"Обновить до {UpdateVersion}";

    /// <summary>Путь к серверу для Claude Desktop (постоянный, не меняется от обновлений) или null.</summary>
    public string? McpPath { get; private set; }

    public string McpPathText => McpPath ?? "Сервер для Claude появится в установленной версии.";

    public bool HasMcpPath => McpPath is not null;

    private string CurrentAppVersion()
    {
        // Установленная версия — от Velopack; иначе — из сборки (без хвоста «+коммит»)
        var info = typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return updater.CurrentVersion ?? (plus > 0 ? info[..plus] : info);
    }

    private void InitUpdates()
    {
        suppressSettings = true;
        CheckForUpdates = config.CheckForUpdates;
        suppressSettings = false;

        McpPath = McpServerCopy.Sync(Path.GetDirectoryName(store.Path)!);

        if (!updater.IsInstalled)
        {
            UpdateStatusText = "Обновления работают в установленной версии (через XoardApp-win-Setup.exe).";
            return;
        }

        UpdateStatusText = "Проверка ещё не делалась.";
        updateTimer = dispatcher.CreateTimer();
        updateTimer.Interval = TimeSpan.FromSeconds(8);   // первая проверка — вскоре после запуска
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            if (CheckForUpdates)
                _ = CheckUpdatesAsync(manual: false);
        };
        updateTimer.Start();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (suppressSettings)
            return;
        config = config with { CheckForUpdates = value };
        ScheduleSave();
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (!updater.IsInstalled)
        {
            if (manual)
                ShowToast(UpdateStatusText, InfoBarSeverity.Informational);
            return;
        }

        if (UpdateBusy)
            return;
        UpdateStatusText = "Проверяю…";
        try
        {
            var version = await updater.CheckAsync().ConfigureAwait(true);
            if (version is null)
            {
                UpdateStatusText = $"У вас последняя версия ({AppVersion}).";
                if (manual)
                    ShowToast(UpdateStatusText, InfoBarSeverity.Success);
                return;
            }

            UpdateVersion = version;
            UpdateAvailable = true;
            UpdateBannerOpen = true;
            UpdateStatusText = $"Доступна версия {version}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateStatusText = $"Не удалось проверить обновления: {ex.Message}";
            if (manual)
                ShowToast(UpdateStatusText, InfoBarSeverity.Warning);
        }
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => CheckUpdatesAsync(manual: true);

    /// <summary>Скачать и поставить найденную версию; программа перезапустится.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (UpdateBusy || !UpdateAvailable)
            return;
        UpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatusText = $"Скачиваю версию {UpdateVersion}…";
        try
        {
            await updater.DownloadAsync(p => dispatcher.TryEnqueue(() => UpdateProgress = p)).ConfigureAwait(true);
            UpdateStatusText = "Устанавливаю и перезапускаю…";
            UpdateReadyToApply?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateBusy = false;
            UpdateStatusText = $"Не удалось скачать обновление: {ex.Message}";
            ShowToast(UpdateStatusText, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void LaterUpdate() => UpdateBannerOpen = false;

    /// <summary>Поставить скачанное обновление и перезапустить (процесс завершится).</summary>
    public void ApplyPendingUpdate() => updater.ApplyAndRestart();

    [RelayCommand]
    private void CopyMcpPath()
    {
        if (McpPath is null)
            return;
        CopyText(McpPath);
        ShowToast("Путь скопирован — вставьте его в claude_desktop_config.json (см. README).", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private void CopySetupLink()
    {
        CopyText(Updater.SetupUrl);
        ShowToast("Ссылка на установщик скопирована — отправьте её другу.", InfoBarSeverity.Success);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
