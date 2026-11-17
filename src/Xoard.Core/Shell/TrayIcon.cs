using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Xoard.Core.Shell;

/// <summary>Пункт меню значка в трее.</summary>
public enum TrayCommand
{
    /// <summary>Клик по значку или «Открыть Xoard».</summary>
    Open,
    StopAll,
    ToggleMute,
    Exit,
}

/// <summary>
/// Значок в области уведомлений (Shell_NotifyIcon) на своём потоке с окном сообщений —
/// как у горячих клавиш: не зависит от потока интерфейса. События приходят на потоке значка.
/// Если Проводник перезапустился (или ещё не запустился при входе в Windows), значок
/// добавляется заново по сообщению «TaskbarCreated».
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 1;
    private const uint WmWork = WmApp + 2;
    private const uint WmQuit = WmApp + 3;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint IconId = 1;

    private const int MenuOpen = 1;
    private const int MenuStopAll = 2;
    private const int MenuMute = 3;
    private const int MenuExit = 4;

    private readonly string? iconPath;
    private readonly Native.WndProc wndProc;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly ConcurrentQueue<Action> work = new();
    private IntPtr hwnd;
    private IntPtr icon;
    private IntPtr largeIcon;
    private bool ownsIcon;
    private uint taskbarCreated;
    private string tooltip;
    private volatile bool added;
    private volatile bool micMuted;
    private bool disposed;

    /// <param name="tooltip">Подсказка при наведении.</param>
    /// <param name="iconPath">Файл .ico (null — стандартный значок приложения).</param>
    public TrayIcon(string tooltip, string? iconPath)
    {
        this.tooltip = tooltip;
        this.iconPath = iconPath;
        wndProc = WindowProc;
        thread = new Thread(Run) { IsBackground = true, Name = "Xoard.Tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Пункт меню выбран (на потоке значка).</summary>
    public event EventHandler<TrayCommand>? Command;

    /// <summary>Значок сейчас виден в трее.</summary>
    public bool IsAdded => added;

    /// <summary>Галочка у пункта «Выключить голос».</summary>
    public bool MicMuted
    {
        get => micMuted;
        set => micMuted = value;
    }

    /// <summary>Сменить подсказку при наведении.</summary>
    public void SetTooltip(string text) => Post(() =>
    {
        tooltip = text;
        if (added)
        {
            var data = NewData(Native.NifTip | Native.NifShowTip);
            Native.ShellNotifyIconW(Native.NimModify, ref data);
        }
    });

    /// <summary>Уведомление у значка (в Windows 10/11 — обычное уведомление).</summary>
    public void ShowBalloon(string title, string text) => Post(() =>
    {
        if (!added)
            return;
        var data = NewData(Native.NifInfo);
        data.SetInfo(title, text);
        data.InfoFlags = Native.NiifUser | Native.NiifNoSound | (largeIcon != IntPtr.Zero ? Native.NiifLargeIcon : 0);
        data.BalloonIcon = largeIcon != IntPtr.Zero ? largeIcon : icon;
        Native.ShellNotifyIconW(Native.NimModify, ref data);
    });

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (hwnd != IntPtr.Zero)
        {
            Native.PostMessageW(hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (!thread.Join(TimeSpan.FromSeconds(2)))
                return;
        }

        ready.Dispose();
    }

    private void Post(Action action)
    {
        if (disposed || hwnd == IntPtr.Zero)
            return;
        work.Enqueue(action);
        Native.PostMessageW(hwnd, WmWork, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        var instance = Native.GetModuleHandleW(null);
        var className = Marshal.StringToHGlobalUni("Xoard.Tray." + Guid.NewGuid().ToString("N"));
        try
        {
            var wc = new Native.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<Native.WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                Instance = instance,
                ClassName = className,
            };

            if (Native.RegisterClassExW(ref wc) != 0)
            {
                // Обычное скрытое окно (не HWND_MESSAGE): только оно получает рассылку «TaskbarCreated»
                hwnd = Native.CreateWindowExW(Native.WsExToolWindow, className, IntPtr.Zero, Native.WsPopup,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            }

            if (hwnd != IntPtr.Zero)
            {
                taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
                LoadIcon();
                Add();
            }

            ready.Set();
            if (hwnd == IntPtr.Zero)
                return;

            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(in msg);
                Native.DispatchMessageW(in msg);
            }
        }
        finally
        {
            if (ownsIcon && icon != IntPtr.Zero)
                Native.DestroyIcon(icon);
            if (largeIcon != IntPtr.Zero)
                Native.DestroyIcon(largeIcon);
            Native.UnregisterClassW(className, instance);
            Marshal.FreeHGlobal(className);
            ready.Set();
        }
    }

    private void LoadIcon()
    {
        if (iconPath is not null && File.Exists(iconPath))
        {
            var dpi = Native.GetDpiForSystem();
            var size = Native.GetSystemMetricsForDpi(Native.SmCxSmIcon, dpi);
            icon = Native.LoadImageW(IntPtr.Zero, iconPath, Native.ImageIcon, size, size, Native.LrLoadFromFile);
            ownsIcon = icon != IntPtr.Zero;
            var large = Native.GetSystemMetricsForDpi(Native.SmCxIcon, dpi);
            largeIcon = Native.LoadImageW(IntPtr.Zero, iconPath, Native.ImageIcon, large, large, Native.LrLoadFromFile);
        }

        if (icon == IntPtr.Zero)
            icon = Native.LoadIconW(IntPtr.Zero, Native.IdiApplication);
    }

    private void Add()
    {
        var data = NewData(Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifShowTip);
        data.CallbackMessage = WmTray;
        if (!Native.ShellNotifyIconW(Native.NimAdd, ref data))
        {
            // Значок мог остаться от прошлого раза (Проводник перезапускался) — заменим
            Native.ShellNotifyIconW(Native.NimDelete, ref data);
            if (!Native.ShellNotifyIconW(Native.NimAdd, ref data))
            {
                added = false;   // панели задач пока нет — добавим по «TaskbarCreated»
                return;
            }
        }

        data.TimeoutOrVersion = Native.NotifyIconVersion4;
        Native.ShellNotifyIconW(Native.NimSetVersion, ref data);
        added = true;
    }

    private Native.NotifyIconData NewData(uint flags)
    {
        var data = new Native.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<Native.NotifyIconData>(),
            Hwnd = hwnd,
            Id = IconId,
            Flags = flags,
            Icon = icon,
        };
        data.SetTip(tooltip);
        return data;
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTray)
        {
            // NOTIFYICON_VERSION_4: в младшем слове lParam — событие, в wParam — координаты
            var e = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (e)
            {
                case NinSelect or NinKeySelect or WmLButtonUp:
                    Raise(TrayCommand.Open);
                    break;
                case WmContextMenu:
                    var x = unchecked((short)(wParam.ToInt64() & 0xFFFF));
                    var y = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    ShowMenu(x, y);
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == taskbarCreated && taskbarCreated != 0)
        {
            Add();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case WmWork:
                while (work.TryDequeue(out var action))
                    action();
                return IntPtr.Zero;

            case WmQuit:
                if (added)
                {
                    var data = NewData(0);
                    Native.ShellNotifyIconW(Native.NimDelete, ref data);
                    added = false;
                }

                Native.DestroyWindow(window);
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            Native.AppendMenuW(menu, Native.MfString, MenuOpen, "Открыть Xoard");
            Native.AppendMenuW(menu, Native.MfString, MenuStopAll, "Стоп всё");
            Native.AppendMenuW(menu, Native.MfString | (micMuted ? Native.MfChecked : 0), MenuMute, "Выключить голос");
            Native.AppendMenuW(menu, Native.MfSeparator, 0, null);
            Native.AppendMenuW(menu, Native.MfString, MenuExit, "Выход");
            Native.SetMenuDefaultItem(menu, MenuOpen, 0);

            // Без этого меню не закрывается кликом мимо него (документированная особенность)
            Native.SetForegroundWindow(hwnd);
            var id = Native.TrackPopupMenuEx(menu, Native.TpmReturnCmd | Native.TpmRightButton | Native.TpmNoNotify, x, y, hwnd, IntPtr.Zero);
            Native.PostMessageW(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (id)
            {
                case MenuOpen:
                    Raise(TrayCommand.Open);
                    break;
                case MenuStopAll:
                    Raise(TrayCommand.StopAll);
                    break;
                case MenuMute:
                    Raise(TrayCommand.ToggleMute);
                    break;
                case MenuExit:
                    Raise(TrayCommand.Exit);
                    break;
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private void Raise(TrayCommand command)
    {
        try
        {
            Command?.Invoke(this, command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ошибка обработчика не должна ломать поток значка
            System.Diagnostics.Debug.WriteLine($"Tray handler failed: {ex}");
        }
    }

    private static partial class Native
    {
        public const uint NimAdd = 0;
        public const uint NimModify = 1;
        public const uint NimDelete = 2;
        public const uint NimSetVersion = 4;
        public const uint NifMessage = 0x01;
        public const uint NifIcon = 0x02;
        public const uint NifTip = 0x04;
        public const uint NifInfo = 0x10;
        public const uint NifShowTip = 0x80;
        public const uint NiifUser = 0x04;
        public const uint NiifNoSound = 0x10;
        public const uint NiifLargeIcon = 0x20;
        public const uint NotifyIconVersion4 = 4;
        public const uint WsPopup = 0x80000000;
        public const uint WsExToolWindow = 0x00000080;
        public const uint MfString = 0x0000;
        public const uint MfChecked = 0x0008;
        public const uint MfSeparator = 0x0800;
        public const uint TpmRightButton = 0x0002;
        public const uint TpmNoNotify = 0x0080;
        public const uint TpmReturnCmd = 0x0100;
        public const uint ImageIcon = 1;
        public const uint LrLoadFromFile = 0x10;
        public const int SmCxIcon = 11;
        public const int SmCxSmIcon = 49;
        public static readonly IntPtr IdiApplication = 32512;

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

        /// <summary>NOTIFYICONDATAW (полная версия, Vista+).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct NotifyIconData
        {
            public uint Size;
            public IntPtr Hwnd;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr Icon;
            public fixed char Tip[128];
            public uint State;
            public uint StateMask;
            public fixed char Info[256];
            public uint TimeoutOrVersion;
            public fixed char InfoTitle[64];
            public uint InfoFlags;
            public Guid GuidItem;
            public IntPtr BalloonIcon;

            public void SetTip(string text)
            {
                fixed (char* p = Tip)
                    Copy(text, p, 128);
            }

            public void SetInfo(string title, string text)
            {
                fixed (char* p = InfoTitle)
                    Copy(title, p, 64);
                fixed (char* p = Info)
                    Copy(text, p, 256);
            }

            private static void Copy(string text, char* target, int capacity)
            {
                var n = Math.Min(text.Length, capacity - 1);
                for (var i = 0; i < n; i++)
                    target[i] = text[i];
                target[n] = '\0';
            }
        }

        [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

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

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint RegisterWindowMessageW(string name);

        [LibraryImport("user32.dll")]
        public static partial IntPtr CreatePopupMenu();

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AppendMenuW(IntPtr menu, uint flags, nint id, string? text);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

        [LibraryImport("user32.dll")]
        public static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyMenu(IntPtr menu);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(IntPtr hwnd);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

        [LibraryImport("user32.dll")]
        public static partial IntPtr LoadIconW(IntPtr instance, IntPtr name);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyIcon(IntPtr icon);

        [LibraryImport("user32.dll")]
        public static partial uint GetDpiForSystem();

        [LibraryImport("user32.dll")]
        public static partial int GetSystemMetricsForDpi(int index, uint dpi);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr GetModuleHandleW(string? moduleName);
    }
}
