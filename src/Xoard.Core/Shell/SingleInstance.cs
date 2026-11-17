using System.Runtime.InteropServices;

namespace Xoard.Core.Shell;

/// <summary>
/// Одна копия приложения на пользователя. Вторая копия не открывает своё окно, а просит первую
/// показаться (именованное событие) и завершается. Нужна из-за трея: окно скрыто, и пользователь
/// снова запускает программу из меню «Пуск».
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle wait;

    private SingleInstance(EventWaitHandle signal)
    {
        this.signal = signal;
        wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Другая копия попросила показать окно (на потоке пула).</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Стать единственной копией. null — копия уже запущена: ей отправлена просьба показать окно,
    /// этой копии нужно завершиться.
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var eventName = $@"Local\{name}";
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out var createdNew);
        if (createdNew)
            return new SingleInstance(signal);

        // Разрешаем первой копии выйти на передний план: этот запуск сделал пользователь
        Native.AllowSetForegroundWindow(Native.AsfwAny);
        signal.Set();
        signal.Dispose();
        return null;
    }

    public void Dispose()
    {
        wait.Unregister(null);
        signal.Dispose();
    }

    private static partial class Native
    {
        public const uint AsfwAny = unchecked((uint)-1);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AllowSetForegroundWindow(uint processId);
    }
}
