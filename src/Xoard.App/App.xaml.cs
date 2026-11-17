using Microsoft.UI.Xaml;
using Xoard.Core.Shell;

namespace Xoard.App;

/// <summary>Точка входа приложения.</summary>
public partial class App : Application
{
    private MainWindow? window;
    private SingleInstance? instance;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("необработанное исключение", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("исключение в фоновой задаче", e.Exception);
            e.SetObserved();
        };

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Последний рубеж: пишем в журнал и показываем ошибку в окне, а не падаем молча
            CrashLog.Write("ошибка интерфейса", e.Exception);
            e.Handled = true;
            window?.ShowError($"Непредвиденная ошибка: {e.Exception.Message}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Одна копия: повторный запуск (например, из «Пуска», пока окно в трее) показывает уже открытую
        instance = SingleInstance.TryAcquire(InstanceName());
        if (instance is null)
        {
            Exit();
            return;
        }

        try
        {
            // --tray — автозапуск с Windows: сразу в трей, без окна
            var hidden = Environment.GetCommandLineArgs().Skip(1)
                .Any(a => string.Equals(a, Autostart.TrayArgument, StringComparison.OrdinalIgnoreCase));
            var w = new MainWindow(startHidden: hidden);
            window = w;
            instance.ActivationRequested += (_, _) => w.DispatcherQueue.TryEnqueue(w.ShowFromTray);
            if (!hidden)
                w.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("не удалось открыть окно", ex);
            throw;
        }
    }

    /// <summary>Имя «одной копии»: своё для проверок с XOARD_DATA_DIR, чтобы не мешать настоящей.</summary>
    private static string InstanceName()
    {
        var dataDir = Environment.GetEnvironmentVariable("XOARD_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            return "Xoard.App";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToUpperInvariant()));
        return "Xoard.App." + Convert.ToHexString(hash, 0, 8);
    }
}

/// <summary>Журнал сбоев: %LOCALAPPDATA%\Xoard\crash.log (или XOARD_DATA_DIR\crash.log).</summary>
internal static class CrashLog
{
    public static string Path
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XOARD_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xoard");
            return System.IO.Path.Combine(dir, "crash.log");
        }
    }

    public static void Write(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Журнал не важнее приложения
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
