using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using Xoard.Core.Shell;

namespace Xoard.App;

/// <summary>
/// Точка входа. Сначала Velopack: установка, обновление и удаление вызывают этот же exe с особыми
/// аргументами — он делает своё и завершается. Потом обычный запуск WinUI.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // Перед удалением программы убираем автозапуск: иначе Windows будет запускать несуществующий файл
            .OnBeforeUninstallFastCallback(_ => TryDisableAutostart())
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    /// <summary>
    /// Убрать автозапуск, только если он ведёт в эту установку (папка «…\XoardApp»): запись может
    /// принадлежать другой копии Xoard, её трогать нельзя.
    /// </summary>
    private static void TryDisableAutostart()
    {
        try
        {
            var autostart = new Autostart();
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath ?? ""));
            if (!string.IsNullOrEmpty(root) && autostart.Command is { } command
                && command.Contains(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                autostart.Disable();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Не помешает удалению
        }
    }
}
