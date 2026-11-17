using Microsoft.Win32;

namespace Xoard.Core.Shell;

/// <summary>
/// Автозапуск вместе с Windows: строка в HKCU\…\Run (только для текущего пользователя, без прав
/// администратора). Если пользователь отключил программу в диспетчере задач («Автозагрузка»),
/// Windows помечает это в StartupApproved\Run — такой автозапуск считается выключенным,
/// а включение здесь снимает пометку.
/// </summary>
public sealed class Autostart
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "Xoard";

    /// <summary>Аргумент запуска: сразу в трей, без окна.</summary>
    public const string TrayArgument = "--tray";

    private readonly string runKey;
    private readonly string approvedKey;
    private readonly string valueName;

    public Autostart(string valueName = DefaultValueName, string runKey = DefaultRunKey, string approvedKey = DefaultApprovedKey)
    {
        this.valueName = valueName;
        this.runKey = runKey;
        this.approvedKey = approvedKey;
    }

    /// <summary>Команда автозапуска для этого exe.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArgument}";

    /// <summary>Записанная команда (null — автозапуска нет).</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey);
            return key?.GetValue(valueName) as string;
        }
    }

    /// <summary>Отключено в диспетчере задач (нечётный первый байт в StartupApproved).</summary>
    public bool DisabledByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(approvedKey);
            return key?.GetValue(valueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
    }

    /// <summary>Программа действительно запустится при входе в Windows.</summary>
    public bool IsEnabled => Command is not null && !DisabledByUser;

    /// <summary>Включить: запуск свёрнутым в трей.</summary>
    public void Enable(string exePath)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(runKey))
            key.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
        RemoveApproval();
    }

    public void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true))
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        RemoveApproval();
    }

    /// <summary>Программу перенесли в другую папку — поправить путь. true — путь обновлён.</summary>
    public bool UpdatePath(string exePath)
    {
        var command = Command;
        if (command is null || string.Equals(command, CommandFor(exePath), StringComparison.OrdinalIgnoreCase))
            return false;
        using var key = Registry.CurrentUser.CreateSubKey(runKey);
        key.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
        return true;
    }

    /// <summary>
    /// Программу переименовали: убрать запись со старым именем и, если тот автозапуск работал,
    /// включить этот. true — старая запись была.
    /// </summary>
    public bool MigrateFrom(string oldValueName, string exePath)
    {
        var old = new Autostart(oldValueName, runKey, approvedKey);
        if (old.Command is null)
            return false;
        var wasEnabled = !old.DisabledByUser;
        old.Disable();
        if (wasEnabled && Command is null)
            Enable(exePath);
        return true;
    }

    private void RemoveApproval()
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
