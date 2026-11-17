using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Xoard.Core.Hotkeys;

/// <summary>Результат регистрации сочетания.</summary>
public enum HotkeyResult
{
    /// <summary>Сочетание работает.</summary>
    Registered,

    /// <summary>Сочетание уже занято (другой программой или этим же приложением).</summary>
    InUse,

    /// <summary>Сочетание не годится (только модификаторы, неизвестная клавиша).</summary>
    Invalid,

    /// <summary>Другая ошибка Windows.</summary>
    Failed,
}

/// <summary>Нажата зарегистрированная горячая клавиша.</summary>
public sealed class HotkeyPressedEventArgs(string name, HotkeyGesture gesture) : EventArgs
{
    /// <summary>Имя, под которым сочетание регистрировалось.</summary>
    public string Name { get; } = name;

    public HotkeyGesture Gesture { get; } = gesture;
}

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey — без низкоуровневых перехватчиков клавиатуры
/// (их не любят античиты). Работают и в полноэкранных играх, когда окно Xoard не активно.
/// Невидимое окно сообщений живёт на своём потоке: Windows требует регистрировать сочетание
/// из потока, создавшего окно. Событие <see cref="Pressed"/> вызывается на этом потоке — обработчик
/// должен быть быстрым (движок звука потокобезопасен, интерфейс — через свой диспетчер).
/// </summary>
public sealed partial class HotkeyManager : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmApp = 0x8000;
    private const uint WmQuit = WmApp + 1;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Thread thread;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly Native.WndProc wndProc;
    private readonly Dictionary<string, (int Id, HotkeyGesture Gesture)> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> byId = [];
    private readonly ManualResetEventSlim ready = new();
    private IntPtr hwnd;
    private IntPtr className;
    private int nextId = 1;
    private int threadId;
    private bool disposed;

    public HotkeyManager()
    {
        wndProc = WindowProc;
        thread = new Thread(Run) { Name = "Xoard.Hotkeys", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)) || hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось создать окно для горячих клавиш.");
    }

    /// <summary>Нажата одна из зарегистрированных клавиш (вызывается на потоке клавиш).</summary>
    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    /// <summary>Зарегистрировать (или перерегистрировать) сочетание под именем.</summary>
    public HotkeyResult Register(string name, HotkeyGesture gesture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!gesture.IsValid)
            return HotkeyResult.Invalid;

        return Invoke(() =>
        {
            RemoveOnThread(name);
            var id = nextId++;
            if (!Native.RegisterHotKey(hwnd, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)gesture.VirtualKey))
            {
                return Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered ? HotkeyResult.InUse : HotkeyResult.Failed;
            }

            byName[name] = (id, gesture);
            byId[id] = name;
            return HotkeyResult.Registered;
        });
    }

    /// <summary>Снять сочетание.</summary>
    public void Unregister(string name) => Invoke(() =>
    {
        RemoveOnThread(name);
        return true;
    });

    /// <summary>Снять все сочетания (например, пока пользователь назначает новое).</summary>
    public void UnregisterAll() => Invoke(() =>
    {
        foreach (var name in byName.Keys.ToList())
            RemoveOnThread(name);
        return true;
    });

    /// <summary>Зарегистрированные сочетания.</summary>
    public IReadOnlyDictionary<string, HotkeyGesture> Registered =>
        Invoke(() => (IReadOnlyDictionary<string, HotkeyGesture>)byName.ToDictionary(kv => kv.Key, kv => kv.Value.Gesture, StringComparer.Ordinal));

    /// <summary>Для тестов: имитировать нажатие (то же сообщение WM_HOTKEY, что шлёт Windows).</summary>
    internal bool SimulatePress(string name)
    {
        var id = Invoke(() => byName.TryGetValue(name, out var e) ? e.Id : 0);
        return id != 0 && Native.PostMessageW(hwnd, WmHotkey, id, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Native.PostMessageW(hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    private void Run()
    {
        threadId = Environment.CurrentManagedThreadId;
        var instance = Native.GetModuleHandleW(null);
        className = Marshal.StringToHGlobalUni("Xoard.Hotkeys." + Guid.NewGuid().ToString("N"));
        var wc = new Native.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            Instance = instance,
            ClassName = className,
        };

        if (Native.RegisterClassExW(ref wc) != 0)
            hwnd = Native.CreateWindowExW(0, className, IntPtr.Zero, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
        ready.Set();
        if (hwnd == IntPtr.Zero)
            return;

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(in msg);
            Native.DispatchMessageW(in msg);
        }

        Native.UnregisterClassW(className, instance);
        Marshal.FreeHGlobal(className);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmHotkey:
                if (byId.TryGetValue(unchecked((int)wParam.ToInt64()), out var name) && byName.TryGetValue(name, out var entry))
                {
                    try
                    {
                        Pressed?.Invoke(this, new HotkeyPressedEventArgs(name, entry.Gesture));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Ошибка обработчика не должна ломать поток клавиш
                        System.Diagnostics.Debug.WriteLine($"Hotkey handler failed: {ex}");
                    }
                }

                return IntPtr.Zero;

            case WmApp:
                while (work.TryDequeue(out var action))
                    action();
                return IntPtr.Zero;

            case WmQuit:
                foreach (var key in byName.Keys.ToList())
                    RemoveOnThread(key);
                Native.DestroyWindow(window);
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    private void RemoveOnThread(string name)
    {
        if (!byName.Remove(name, out var entry))
            return;
        byId.Remove(entry.Id);
        Native.UnregisterHotKey(hwnd, entry.Id);
    }

    private T Invoke<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId == threadId)
            return func();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        Native.PostMessageW(hwnd, WmApp, IntPtr.Zero, IntPtr.Zero);
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Поток горячих клавиш не отвечает.");
        return tcs.Task.Result;
    }

    private static partial class Native
    {
        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public IntPtr WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public IntPtr MenuName;
            public IntPtr ClassName;
            public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial ushort RegisterClassExW(ref WndClassEx wc);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterClassW(IntPtr className, IntPtr instance);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial IntPtr CreateWindowExW(uint exStyle, IntPtr className, IntPtr windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        public static partial int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TranslateMessage(in Msg msg);

        [LibraryImport("user32.dll")]
        public static partial IntPtr DispatchMessageW(in Msg msg);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        public static partial void PostQuitMessage(int exitCode);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterHotKey(IntPtr hwnd, int id);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr GetModuleHandleW(string? moduleName);
    }
}
