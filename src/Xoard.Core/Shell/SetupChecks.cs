using Xoard.Core.Audio;
using Xoard.Core.Audio.Status;

namespace Xoard.Core.Shell;

/// <summary>Результат одной проверки настройки.</summary>
public enum CheckState
{
    Ok,
    Warning,
    Error,
}

/// <summary>Одна строка в окне «Как настроить Discord».</summary>
public sealed record SetupCheck(CheckState State, string Text);

/// <summary>
/// Проверки для подсказки по Discord: VB-CABLE установлен, Discord слушает CABLE Output,
/// звук Windows по умолчанию идёт не в кабель.
/// </summary>
public static class SetupChecks
{
    /// <summary>Discord и его варианты: Discord, DiscordPTB, DiscordCanary, DiscordDevelopment.</summary>
    public static bool IsDiscord(string processName) =>
        processName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase);

    public static SetupCheck Cable(VbCableStatus cable) => cable.Installed
        ? new(CheckState.Ok, $"VB-CABLE установлен: звуки идут в «{cable.RenderName ?? "CABLE Input"}».")
        : new(CheckState.Error, "VB-CABLE не установлен — без него друзья не услышат звуки.");

    /// <param name="discordRunning">Процесс Discord запущен (проверяется отдельно, раз в несколько секунд).</param>
    public static SetupCheck Discord(VbCableStatus cable, bool discordRunning)
    {
        var captureName = cable.CaptureName ?? "CABLE Output";
        if (!cable.Installed)
            return new(CheckState.Warning, "Discord: сначала установите VB-CABLE.");
        if (cable.ListeningApps.Any(a => IsDiscord(a.ProcessName)))
            return new(CheckState.Ok, $"Discord слушает «{captureName}» — друзья услышат звуки.");
        return discordRunning
            ? new(CheckState.Warning, $"Discord запущен, но не слушает «{captureName}». Выберите его устройством ввода (шаг 1) — " +
                                      "Discord начинает слушать, когда вы в голосовом канале или в «Проверке микрофона».")
            : new(CheckState.Warning, "Discord не запущен — проверка появится, когда вы зайдёте в голосовой канал.");
    }

    public static SetupCheck DefaultOutput(IReadOnlyList<EngineIssue> issues) =>
        issues.Any(i => i.Code == IssueCode.DefaultOutputIsVbCable)
            ? new(CheckState.Error, "Звук Windows по умолчанию идёт в CABLE Input: друзья услышат весь звук компьютера и себя же. " +
                                    "Параметры → Система → Звук → Вывод — выберите наушники или колонки.")
            : new(CheckState.Ok, "Звук Windows идёт в наушники, а не в кабель — эха не будет.");
}
