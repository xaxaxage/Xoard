using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Xoard.App.ViewModels;

namespace Xoard.App.Views;

/// <summary>
/// Раскладка «Лента»: одна колонка, как «Сегодня» в трекере — большая живая волна повтора,
/// звуки списком, капсула с громкостью и кнопка записи снизу.
/// </summary>
public sealed partial class StreamView : UserControl
{
    public StreamView(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        ApplyAnimationSetting();
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
        };
        Unloaded += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
        };
    }

    public MainViewModel Vm { get; }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AnimationsEnabled))
            ApplyAnimationSetting();
    }

    private void OnSoundClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SoundItem item)
            Vm.Play(item, previewOnly: false);
    }

    /// <summary>«…» в строке открывает то же меню, что и правая кнопка.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: FrameworkElement { ContextFlyout: { } menu } } button)
            menu.ShowAt(button);
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Vm.OnReordered();

    private void ApplyAnimationSetting() =>
        SoundsList.ItemContainerTransitions = Motion.On(Vm)
            ? [new EntranceThemeTransition { IsStaggeringEnabled = true }, new AddDeleteThemeTransition(), new ReorderThemeTransition()]
            : [];
}
