using Microsoft.UI.Xaml;

namespace Soundboard.App;

/// <summary>Точка входа приложения.</summary>
public partial class App : Application
{
    private MainWindow? window;

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
        try
        {
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("не удалось открыть окно", ex);
            throw;
        }
    }
}

/// <summary>Журнал сбоев: %LOCALAPPDATA%\Soundboard\crash.log (или SOUNDBOARD_DATA_DIR\crash.log).</summary>
internal static class CrashLog
{
    public static string Path
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("SOUNDBOARD_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundboard");
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
