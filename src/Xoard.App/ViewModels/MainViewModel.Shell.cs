using System.Diagnostics;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Xoard.Core.Shell;

namespace Xoard.App.ViewModels;

/// <summary>Строка проверки в окне «Как настроить Discord».</summary>
public sealed record SetupCheckView(InfoBarSeverity Severity, string Text)
{
    public static SetupCheckView Pending { get; } = new(InfoBarSeverity.Informational, "Проверяю…");

    public static SetupCheckView From(SetupCheck check) => new(check.State switch
    {
        CheckState.Ok => InfoBarSeverity.Success,
        CheckState.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error,
    }, check.Text);
}

/// <summary>Трей, автозапуск, подсказка по VB-CABLE и Discord.</summary>
public sealed partial class MainViewModel
{
    private readonly Autostart autostart = new();
    private long lastDiscordProbe;
    private bool discordRunning;

    /// <summary>Попросить окно показать «Как настроить Discord».</summary>
    public event Action? SetupGuideRequested;

    /// <summary>Окно подсказки открыто: проверки обновляются, пока его читают.</summary>
    public bool SetupGuideOpen { get; set; }

    [ObservableProperty]
    public partial bool AutostartEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutostartHint))]
    public partial string AutostartHint { get; set; } = "";

    public bool HasAutostartHint => AutostartHint.Length > 0;

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial SetupCheckView CableCheck { get; set; } = SetupCheckView.Pending;

    [ObservableProperty]
    public partial SetupCheckView DiscordCheck { get; set; } = SetupCheckView.Pending;

    [ObservableProperty]
    public partial SetupCheckView OutputCheck { get; set; } = SetupCheckView.Pending;

    private void InitShell()
    {
        suppressSettings = true;
        CloseToTray = config.CloseToTray;
        try
        {
            // Программу переименовали (раньше — Soundboard) или перенесли в другую папку:
            // автозапуск должен запускать её под новым именем и с нового места
            if (Environment.ProcessPath is { } exe)
            {
                autostart.MigrateFrom(LegacyMigration.OldName, exe);
                autostart.UpdatePath(exe);
            }
            AutostartEnabled = autostart.IsEnabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AutostartEnabled = false;
        }
        finally
        {
            suppressSettings = false;
        }

        UpdateAutostartHint();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (suppressSettings)
            return;
        config = config with { CloseToTray = value };
        ScheduleSave();
    }

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (suppressSettings)
            return;
        try
        {
            if (value && Environment.ProcessPath is { } exe)
                autostart.Enable(exe);
            else
                autostart.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            ShowToast($"Не удалось изменить автозапуск: {ex.Message}", InfoBarSeverity.Error);
            suppressSettings = true;
            AutostartEnabled = !value;
            suppressSettings = false;
        }

        UpdateAutostartHint();
    }

    private void UpdateAutostartHint()
    {
        bool disabledByUser;
        try
        {
            disabledByUser = autostart.Command is not null && autostart.DisabledByUser;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            disabledByUser = false;
        }

        AutostartHint = disabledByUser
            ? "Сейчас отключено в диспетчере задач («Автозагрузка»). Включите здесь — запуск вернётся."
            : AutostartEnabled
                ? "При входе в Windows Xoard запустится свёрнутым в трей: клавиши и звуки сразу работают."
                : "";
    }

    /// <summary>Подсказку «работает в трее» показываем один раз.</summary>
    public bool TakeTrayHint()
    {
        if (config.TrayHintShown)
            return false;
        config = config with { TrayHintShown = true };
        ScheduleSave();
        return true;
    }

    /// <summary>При первом запуске один раз открыть «Как настроить Discord».</summary>
    public bool TakeFirstRunGuide()
    {
        if (config.SetupGuideShown)
            return false;
        config = config with { SetupGuideShown = true };
        ScheduleSave();
        return true;
    }

    [RelayCommand]
    private void ShowSetupGuide() => SetupGuideRequested?.Invoke();

    /// <summary>Проверить всё сейчас (при открытии подсказки).</summary>
    public void RefreshSetupChecks()
    {
        lastDiscordProbe = 0;
        UpdateSetupChecks();
    }

    /// <summary>Таймер окна: пока подсказка открыта — раз в 2 с.</summary>
    private void TickSetupChecks()
    {
        if (SetupGuideOpen && Stopwatch.GetElapsedTime(lastDiscordProbe) >= TimeSpan.FromSeconds(2))
            UpdateSetupChecks();
    }

    private void UpdateSetupChecks()
    {
        lastDiscordProbe = Stopwatch.GetTimestamp();
        discordRunning = IsDiscordRunning();
        var s = engine.Status;
        Set(CableCheck, SetupCheckView.From(SetupChecks.Cable(s.VbCable)), v => CableCheck = v);
        Set(DiscordCheck, SetupCheckView.From(SetupChecks.Discord(s.VbCable, discordRunning)), v => DiscordCheck = v);
        Set(OutputCheck, SetupCheckView.From(SetupChecks.DefaultOutput(s.Issues)), v => OutputCheck = v);

        static void Set(SetupCheckView current, SetupCheckView next, Action<SetupCheckView> assign)
        {
            if (current != next)
                assign(next);
        }
    }

    private static bool IsDiscordRunning()
    {
        var found = false;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                found |= SetupChecks.IsDiscord(p.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // Процесс уже завершился
            }
            finally
            {
                p.Dispose();
            }
        }

        return found;
    }
}
