using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Xoard.App.ViewModels;
using Xoard.Core.Config;
using Xoard.Core.Audio.Sounds;
using Xoard.Core.Hotkeys;
using Xoard.Core.Library;
using Xoard.Core.Recording;
using Xoard.Core.Shell;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Xoard.App;

/// <summary>Главное окно: сетка звуков, микшер, настройки.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Значок трея убирается в ExitAsync — единственном пути выхода из программы.")]
public sealed partial class MainWindow : Window
{
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private readonly TrayIcon tray;
    private readonly bool startedHidden;
    private bool closing;
    private bool shown;
    private bool maximizeOnShow;

    /// <param name="startHidden">Автозапуск: окно не показывать, работать в трее.</param>
    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        startedHidden = startHidden;
        shown = !startHidden;
        Vm = new MainViewModel(DispatcherQueue);
        Vm.RenameRequested += item => _ = RenameAsync(item);
        Vm.HotkeyRequested += target => _ = AssignHotkeyAsync(target);
        Vm.TrimRequested += item => _ = TrimAsync(item);
        Vm.SetupGuideRequested += () => _ = ShowSetupGuideAsync();
        Vm.AddSoundsRequested += () => _ = PickSoundsAsync();

        // Раскладка и настройки; цвета — из палитры (тема Windows только для стандартных мелочей)
        ShowBoard();
        SheetHost.Content = new Views.SettingsSheet(Vm);
        ApplyPaletteToWindow();
        Theme.ThemeManager.Current.Changed += ApplyPaletteToWindow;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Xoard.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Значок в трее: клик — окно, правая кнопка — меню (события приходят с потока значка)
        tray = new TrayIcon("Xoard", File.Exists(iconPath) ? iconPath : null) { MicMuted = Vm.MicMuted };
        tray.Command += (_, command) => DispatcherQueue.TryEnqueue(() => OnTrayCommand(command));

        RestorePlacement(Vm.SavedWindow);
        Vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.SettingsOpen):
                    ShowSettings(Vm.SettingsOpen);
                    break;
                case nameof(MainViewModel.LayoutId):
                    ShowBoard();
                    break;
                case nameof(MainViewModel.MicMuted):
                    tray.MicMuted = Vm.MicMuted;
                    break;
                case nameof(MainViewModel.StatusText):
                    tray.SetTooltip($"Xoard · {Vm.StatusText}");
                    break;
            }
        };

        AppWindow.Closing += OnClosing;

        // Запуск не ждёт показа окна: при автозапуске окно скрыто, а звук и клавиши нужны сразу
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Vm.InitializeAsync();
            if (startedHidden && Vm.VbCableMissing)
                tray.ShowBalloon("Нет VB-CABLE", "Друзья не услышат звуки. Откройте Xoard — там ссылка на установку.");
        });
        Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = ShowFirstRunGuideAsync());
    }

    public MainViewModel Vm { get; }

    /// <summary>Показать ошибку (из обработчика необработанных исключений).</summary>
    public void ShowError(string message) => Vm.ShowToast(message, InfoBarSeverity.Error, sticky: true);

    private bool AnimationsOn => Vm.AnimationsEnabled && uiSettings.AnimationsEnabled;

    // ───────── палитра, настройки, уведомления ─────────

    /// <summary>Цвета заголовка окна и тема стандартных элементов — под палитру.</summary>
    private void ApplyPaletteToWindow()
    {
        var theme = Theme.ThemeManager.Current;
        Root.RequestedTheme = theme.ElementTheme;
        SetupGuide.RequestedTheme = theme.ElementTheme;
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = theme["Bg"];
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = theme["Bg"];
        bar.ForegroundColor = bar.ButtonForegroundColor = theme["Ink"];
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = theme["Muted"];
        bar.ButtonHoverBackgroundColor = theme["Sunken"];
        bar.ButtonHoverForegroundColor = theme["Ink"];
        bar.ButtonPressedBackgroundColor = theme["Line"];
        bar.ButtonPressedForegroundColor = theme["Ink"];
    }

    /// <summary>Раскладка главного окна: Пульт, Док или Лента (меняется на лету из настроек).</summary>
    private void ShowBoard() => BoardHost.Content = Vm.LayoutId switch
    {
        "dock" => new Views.DockView(Vm),
        "stream" => new Views.StreamView(Vm),
        _ => new Views.PultView(Vm),
    };

    private void ShowSettings(bool open)
    {
        SettingsLayer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            Views.Motion.SlideIn(SheetHost, AnimationsOn);
    }

    private void OnScrimTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => Vm.SettingsOpen = false;

    private void OnToastTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => Vm.ToastOpen = false;

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && Vm.SettingsOpen)
        {
            Vm.SettingsOpen = false;
            e.Handled = true;
        }
    }

    // ───────── звуки ─────────

    private async Task PickSoundsAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (var ext in SoundLibrary.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            Vm.Import(files.Select(f => f.Path).ToList());
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Добавить в Xoard";
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 0)
            Vm.Import(paths);
    }

    private async Task RenameAsync(SoundItem item)
    {
        var box = new TextBox { Text = item.Name, SelectionStart = 0, SelectionLength = item.Name.Length };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Theme.ThemeManager.Current.ElementTheme,
            Title = "Переименовать звук",
            Content = new StackPanel { Spacing = 8, Children = { box, error } },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = Vm.Rename(item, box.Text);
            if (message is not null)
            {
                error.Text = message;
                args.Cancel = true;
            }
        };
        box.TextChanged += (_, _) => error.Text = "";
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Запись сочетания: пока диалог открыт, все горячие клавиши сняты, иначе нажатие уже
    /// назначенного сочетания сработало бы вместо записи.
    /// </summary>
    private async Task AssignHotkeyAsync(HotkeyTarget target)
    {
        var current = Vm.CurrentHotkey(target);
        HotkeyGesture? captured = null;

        var shown = new TextBlock
        {
            Text = current ?? "—",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Theme.ThemeManager.Current.ElementTheme,
            Title = target.Title,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 360,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Нажмите сочетание клавиш, например Ctrl+Alt+1, F9 или Num5. Esc — отмена.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new Border
                    {
                        Padding = new Thickness(12),
                        CornerRadius = new CornerRadius(8),
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        Child = shown,
                    },
                    error,
                },
            },
            PrimaryButtonText = "Сохранить",
            SecondaryButtonText = current is null ? "" : "Убрать клавишу",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        dialog.PreviewKeyDown += (_, e) =>
        {
            var key = (int)e.Key;
            if (e.Key == Windows.System.VirtualKey.Escape || HotkeyGesture.IsModifierKey(key))
                return;   // Esc закрывает диалог; одиночные модификаторы ждём дальше

            var modifiers = HotkeyModifiers.None;
            if (IsDown(Windows.System.VirtualKey.Control))
                modifiers |= HotkeyModifiers.Control;
            if (IsDown(Windows.System.VirtualKey.Menu))
                modifiers |= HotkeyModifiers.Alt;
            if (IsDown(Windows.System.VirtualKey.Shift))
                modifiers |= HotkeyModifiers.Shift;
            if (IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows))
                modifiers |= HotkeyModifiers.Win;

            var gesture = new HotkeyGesture(modifiers, key);
            e.Handled = true;
            if (!gesture.IsValid)
            {
                error.Text = "Эту клавишу назначить нельзя — выберите другую.";
                return;
            }

            captured = gesture;
            shown.Text = gesture.ToString();
            error.Text = "";
            dialog.IsPrimaryButtonEnabled = true;
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = Vm.SetHotkey(target, captured);
            if (message is not null)
            {
                error.Text = message;
                args.Cancel = true;
            }
        };
        dialog.SecondaryButtonClick += (_, _) => Vm.SetHotkey(target, null);

        Vm.SuspendHotkeys();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            Vm.ResumeHotkeys();
        }
    }

    /// <summary>
    /// Обрезка: форма волны, выделение мышкой, прослушать выделенное (только себе), сохранить.
    /// Сначала выделено всё, кроме тишины по краям.
    /// </summary>
    private async Task TrimAsync(SoundItem item)
    {
        DecodedSound sound;
        try
        {
            sound = await Vm.DecodeForEditorAsync(item);
        }
        catch (SoundLoadException ex)
        {
            Vm.ShowToast(ex.Message, InfoBarSeverity.Error);
            return;
        }

        const double width = 720;
        const double height = 140;
        var duration = sound.Duration.TotalSeconds;
        if (duration <= 0)
            return;

        var peaks = AudioClips.Peaks(sound.Samples, (int)width);
        var mid = height / 2;
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(0, mid), IsClosed = true, IsFilled = true };
        for (var x = 0; x < peaks.Length; x++)
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, mid - (peaks[x].Max * mid * 0.95)) });
        for (var x = peaks.Length - 1; x >= 0; x--)
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, mid - (peaks[x].Min * mid * 0.95)) });

        var wave = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Fill = (Brush)Application.Current.Resources["XWave"],
        };
        var selectionRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = height,
            Fill = (Brush)Application.Current.Resources["XPrimary"],
            Opacity = 0.22,
        };
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.Resources["XBg"],
            Children = { wave, selectionRect },
        };

        // Начальное выделение: без тишины по краям (с запасом 0,1 с)
        var first = Array.FindIndex(peaks, p => Math.Max(Math.Abs(p.Min), Math.Abs(p.Max)) > 0.02f);
        var last = Array.FindLastIndex(peaks, p => Math.Max(Math.Abs(p.Min), Math.Abs(p.Max)) > 0.02f);
        double selStart = first < 0 ? 0 : Math.Max(0, (first / width * duration) - 0.1);
        double selEnd = last < 0 ? duration : Math.Min(duration, ((last + 1) / width * duration) + 0.1);

        var info = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };

        void Redraw()
        {
            Canvas.SetLeft(selectionRect, selStart / duration * width);
            selectionRect.Width = Math.Max(1, (selEnd - selStart) / duration * width);
            info.Text = $"Начало {selStart:0.00} с · конец {selEnd:0.00} с · останется {selEnd - selStart:0.00} из {duration:0.00} с";
        }

        double anchor = 0;
        var dragging = false;
        canvas.PointerPressed += (_, e) =>
        {
            dragging = true;
            canvas.CapturePointer(e.Pointer);
            anchor = Math.Clamp(e.GetCurrentPoint(canvas).Position.X, 0, width) / width * duration;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;
            var t = Math.Clamp(e.GetCurrentPoint(canvas).Position.X, 0, width) / width * duration;
            if (Math.Abs(t - anchor) < duration / width * 2)
                return;
            selStart = Math.Min(anchor, t);
            selEnd = Math.Max(anchor, t);
            Redraw();
        };
        canvas.PointerReleased += (_, e) =>
        {
            dragging = false;
            canvas.ReleasePointerCapture(e.Pointer);
        };

        var preview = new Button { Content = "Прослушать выделенное" };
        preview.Click += (_, _) => Vm.PreviewSlice(sound, selStart, selEnd);
        var reset = new Button { Content = "Выделить всё" };
        reset.Click += (_, _) =>
        {
            selStart = 0;
            selEnd = duration;
            Redraw();
        };
        Redraw();

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Theme.ThemeManager.Current.ElementTheme,
            Title = $"Обрезка «{item.Name}»",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Выделите мышкой кусок, который нужно оставить.", TextWrapping = TextWrapping.Wrap },
                    new Border { CornerRadius = new CornerRadius(8), Child = canvas },
                    info,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { preview, reset } },
                    error,
                },
            },
            PrimaryButtonText = "Сохранить обрезку",
            CloseButtonText = "Оставить как есть",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (selStart <= 0.001 && selEnd >= duration - 0.001)
                    return;   // выделено всё — нечего резать
                var message = await Vm.TrimAsync(item, selStart, selEnd);
                if (message is not null)
                {
                    error.Text = message;
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ───────── окно ─────────

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < 400 || placement.Height < 300)
        {
            AppWindow.Resize(new SizeInt32(1180, 800));
            return;
        }

        // Окно должно попасть на существующий экран
        var area = DisplayArea.GetFromRect(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height), DisplayAreaFallback.None);
        if (area is null)
            AppWindow.Resize(new SizeInt32(placement.Width, placement.Height));
        else
            AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        // Развернуть = показать окно, поэтому для скрытого запуска — при первом показе
        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter p)
        {
            if (startedHidden)
                maximizeOnShow = true;
            else
                p.Maximize();
        }
    }

    private void RememberPlacement()
    {
        if (!AppWindow.IsVisible)
            return;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            Vm.SetWindowPlacement(new WindowPlacement(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false));
        else if (maximized)
            Vm.SetWindowPlacement((Vm.SavedWindow ?? new WindowPlacement(100, 100, 1040, 720, false)) with { Maximized = true });
    }

    // ───────── трей ─────────

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                ShowFromTray();
                break;
            case TrayCommand.StopAll:
                Vm.StopAllCommand.Execute(null);
                break;
            case TrayCommand.ToggleMute:
                Vm.MicMuted = !Vm.MicMuted;
                break;
            case TrayCommand.Exit:
                _ = ExitAsync();
                break;
        }
    }

    /// <summary>Показать окно (из трея или по повторному запуску программы).</summary>
    public void ShowFromTray()
    {
        if (closing)
            return;
        if (!shown)
        {
            shown = true;
            Activate();
            if (maximizeOnShow && AppWindow.Presenter is OverlappedPresenter p)
                p.Maximize();
            return;
        }

        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing)
            return;
        args.Cancel = true;

        // ✕ — в трей: звук, клавиши и Claude продолжают работать
        if (Vm.CloseToTray && tray.IsAdded)
        {
            RememberPlacement();
            sender.Hide();
            if (Vm.TakeTrayHint())
                tray.ShowBalloon("Xoard работает в трее", "Звуки и клавиши доступны. Открыть — клик по значку, выход — правой кнопкой → «Выход».");
            return;
        }

        await ExitAsync();
    }

    /// <summary>Выход из программы: сохранить настройки, аккуратно остановить звук, закрыть окно.</summary>
    private async Task ExitAsync()
    {
        if (closing)
            return;
        closing = true;
        RememberPlacement();
        AppWindow.Hide();
        tray.Dispose();
        await Vm.DisposeAsync();
        Close();
        Application.Current.Exit();
    }

    // ───────── настройка VB-CABLE и Discord ─────────

    private Task ShowFirstRunGuideAsync() => Vm.TakeFirstRunGuide() ? ShowSetupGuideAsync() : Task.CompletedTask;

    private async Task ShowSetupGuideAsync()
    {
        if (closing || Root.XamlRoot is null)
            return;
        Vm.RefreshSetupChecks();
        try
        {
            await SetupGuide.ShowAsync();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80000019))
        {
            // Уже открыт другой диалог — подсказку покажем в другой раз
        }
    }

    private void OnSetupGuideOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => Vm.SetupGuideOpen = true;

    private void OnSetupGuideClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Vm.SetupGuideOpen = false;
}
