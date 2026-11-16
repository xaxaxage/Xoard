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
using Soundboard.Core.Hotkeys;
using Soundboard.Core.Library;
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
        Vm.HotkeyRequested += item => _ = AssignHotkeyAsync(item);

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
    private async Task AssignHotkeyAsync(SoundItem? item)
    {
        var current = Vm.CurrentHotkey(item);
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
            Title = item is null ? "Клавиша «Стоп всё»" : $"Клавиша для «{item.Name}»",
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
            var message = Vm.SetHotkey(item, captured);
            if (message is not null)
            {
                error.Text = message;
                args.Cancel = true;
            }
        };
        dialog.SecondaryButtonClick += (_, _) => Vm.SetHotkey(item, null);

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

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ───────── навигация и оформление ─────────

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var settings = args.IsSettingsSelected;
        SoundsPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
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
