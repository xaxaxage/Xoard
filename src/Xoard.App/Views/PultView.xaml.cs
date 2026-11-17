using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Xoard.App.ViewModels;

namespace Xoard.App.Views;

/// <summary>
/// Раскладка «Пульт»: шапка, живая полоса повтора, сетка звуков слева, запись и громкость справа.
/// </summary>
public sealed partial class PultView : UserControl
{
    private const double MinTileWidth = 200;

    public PultView(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        ApplyAnimationSetting();

        Loaded += (_, _) =>
        {
            Vm.LiveWaveUpdated += OnLiveWave;
            Vm.PropertyChanged += OnVmChanged;
        };
        Unloaded += (_, _) =>
        {
            Vm.LiveWaveUpdated -= OnLiveWave;
            Vm.PropertyChanged -= OnVmChanged;
        };
    }

    public MainViewModel Vm { get; }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AnimationsEnabled))
            ApplyAnimationSetting();
    }

    private void OnLiveWave(float[] peaks) => LiveWave.SetPeaks(peaks);

    private void OnSoundClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SoundItem item)
            return;
        Vm.Play(item, previewOnly: false);
        if (Motion.On(Vm) && SoundsGrid.ContainerFromItem(item) is GridViewItem container)
            Motion.Pop(container);
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Vm.OnReordered();

    /// <summary>Плитки тянутся на всю ширину: столько колонок, сколько влезает по 200 px.</summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SoundsGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;
        var available = e.NewSize.Width - 2;
        var columns = Math.Max(1, (int)Math.Floor(available / MinTileWidth));
        panel.ItemWidth = Math.Floor(available / columns);
    }

    private void ApplyAnimationSetting()
    {
        // Без анимаций — без переходов появления и перестановки плиток
        SoundsGrid.ItemContainerTransitions = Motion.On(Vm)
            ? [new EntranceThemeTransition { IsStaggeringEnabled = true }, new AddDeleteThemeTransition(), new ReorderThemeTransition()]
            : [];
    }
}
