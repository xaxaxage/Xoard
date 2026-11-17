using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Soundboard.App.ViewModels;
using Soundboard.Core.Config;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Hotkeys;
using Soundboard.Core.Library;
using Soundboard.Core.Recording;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Soundboard.App;

/// <summary>Главное окно: сетка звуков, микшер, настройки.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private bool closing;

    public MainWindow()
    {
        InitializeComponent();
        Vm = new MainViewModel(DispatcherQueue);
        Vm.RenameRequested += item => _ = RenameAsync(item);
        Vm.HotkeyRequested += target => _ = AssignHotkeyAsync(target);
        Vm.TrimRequested += item => _ = TrimAsync(item);

        SystemBackdrop = new MicaBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        RestorePlacement(Vm.SavedWindow);
        // Пункт «Настройки» появляется только после загрузки шаблона NavigationView
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "Настройки";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, "Настройки");
            }
        };
        ApplyAnimationSetting();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AnimationsEnabled))
                ApplyAnimationSetting();
        };

        AppWindow.Closing += OnClosing;
        Root.Loaded += async (_, _) => await Vm.InitializeAsync();
    }

    public MainViewModel Vm { get; }

    /// <summary>Показать ошибку (из обработчика необработанных исключений).</summary>
    public void ShowError(string message) => Vm.ShowToast(message, InfoBarSeverity.Error, sticky: true);

    private bool AnimationsOn => Vm.AnimationsEnabled && uiSettings.AnimationsEnabled;

    // ───────── звуки ─────────

    private void OnSoundClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SoundItem item)
            return;
        Vm.Play(item, previewOnly: false);
        if (AnimationsOn && SoundsGrid.ContainerFromItem(item) is GridViewItem container)
            Pop(container);
    }

    /// <summary>Короткое «нажатие» плитки: 1 → 0,94 → 1 за 0,18 с.</summary>
    private static void Pop(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0.35f, new Vector3(0.94f, 0.94f, 1));
        animation.InsertKeyFrame(1f, Vector3.One);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Scale", animation);
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Vm.OnReordered();

    private async void OnAddSoundsClick(object sender, RoutedEventArgs e)
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
            e.DragUIOverride.Caption = "Добавить в саундборд";
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
            Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var selectionRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = height,
            Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0.28,
        };
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
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

    // ───────── навигация и оформление ─────────

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var settings = args.IsSettingsSelected;
        var recording = !settings && ReferenceEquals(args.SelectedItem, RecordNavItem);
        SoundsPanel.Visibility = settings || recording ? Visibility.Collapsed : Visibility.Visible;
        RecordingPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAnimationSetting()
    {
        // Без анимаций — без переходов появления/перестановки плиток
        SoundsGrid.ItemContainerTransitions = AnimationsOn
            ? [new EntranceThemeTransition { IsStaggeringEnabled = true }, new AddDeleteThemeTransition(), new ReorderThemeTransition()]
            : [];
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < 400 || placement.Height < 300)
        {
            AppWindow.Resize(new SizeInt32(1040, 720));
            return;
        }

        // Окно должно попасть на существующий экран
        var area = DisplayArea.GetFromRect(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height), DisplayAreaFallback.None);
        if (area is null)
            AppWindow.Resize(new SizeInt32(placement.Width, placement.Height));
        else
            AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter p)
            p.Maximize();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing)
            return;

        // Сначала сохраняем и аккуратно останавливаем звук, потом закрываемся
        args.Cancel = true;
        closing = true;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            Vm.SetWindowPlacement(new WindowPlacement(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false));
        else if (maximized)
            Vm.SetWindowPlacement((Vm.SavedWindow ?? new WindowPlacement(100, 100, 1040, 720, false)) with { Maximized = true });

        await Vm.DisposeAsync();
        Close();
    }
}
