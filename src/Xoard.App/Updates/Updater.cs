using Velopack;
using Velopack.Sources;

namespace Xoard.App.Updates;

/// <summary>
/// Обновления через Velopack: новые версии лежат в GitHub Releases (xaxaxage/Xoard).
/// Работает только в установленной версии (через XoardApp-win-Setup.exe); в сборке из исходников
/// <see cref="IsInstalled"/> = false и проверок нет. XOARD_UPDATE_SOURCE — другая папка или адрес
/// с выпусками (для проверок).
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/xaxaxage/Xoard";

    /// <summary>Постоянная ссылка на установщик последней версии — её можно отправить другу.</summary>
    public const string SetupUrl = RepoUrl + "/releases/latest/download/XoardApp-win-Setup.exe";

    private readonly UpdateManager manager;
    private UpdateInfo? pending;

    public Updater()
    {
        var custom = Environment.GetEnvironmentVariable("XOARD_UPDATE_SOURCE");
        IUpdateSource source = string.IsNullOrWhiteSpace(custom)
            ? new GithubSource(RepoUrl, null, false)
            : custom.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new SimpleWebSource(custom)
                : new SimpleFileSource(new DirectoryInfo(custom));
        manager = new UpdateManager(source);
    }

    /// <summary>Программа установлена через Setup (иначе обновляться некуда).</summary>
    public bool IsInstalled => manager.IsInstalled;

    /// <summary>Установленная версия (null — сборка из исходников).</summary>
    public string? CurrentVersion => manager.IsInstalled ? manager.CurrentVersion?.ToString() : null;

    /// <summary>Проверить, есть ли новая версия. Возвращает её номер или null.</summary>
    public async Task<string?> CheckAsync()
    {
        pending = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return pending?.TargetFullRelease.Version.ToString();
    }

    /// <summary>Скачать найденную версию (прогресс 0–100).</summary>
    public Task DownloadAsync(Action<int> progress)
    {
        var update = pending ?? throw new InvalidOperationException("Сначала проверьте обновления.");
        return manager.DownloadUpdatesAsync(update, progress);
    }

    /// <summary>Поставить скачанное и перезапустить программу (процесс завершается).</summary>
    public void ApplyAndRestart()
    {
        var update = pending ?? throw new InvalidOperationException("Нет скачанного обновления.");
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
