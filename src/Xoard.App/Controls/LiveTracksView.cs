using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Xoard.App.Controls;

/// <summary>Дорожка живой полосы: подпись и источник волны.</summary>
public sealed record LiveTrack(string Title, ILiveWaveSource Source);

/// <summary>
/// Живая полоса повтора по дорожкам: несколько <see cref="LiveWaveView"/> друг под другом,
/// у каждой слева подпись моноширинным шрифтом (Discord / Игра / Голос).
/// </summary>
public sealed partial class LiveTracksView : Grid
{
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<LiveTrack>), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Build()));

    public static readonly DependencyProperty DivisionsProperty = DependencyProperty.Register(
        nameof(Divisions), typeof(int), typeof(LiveTracksView), new PropertyMetadata(6, (d, _) => ((LiveTracksView)d).Sync()));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush), typeof(Brush), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Sync()));

    public static readonly DependencyProperty RmsBrushProperty = DependencyProperty.Register(
        nameof(RmsBrush), typeof(Brush), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Sync()));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Sync()));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Sync()));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(LiveTracksView), new PropertyMetadata(null, (d, _) => ((LiveTracksView)d).Sync()));

    private readonly List<(TextBlock Label, LiveWaveView Wave)> lanes = [];

    public LiveTracksView()
    {
        IsHitTestVisible = false;
        RowSpacing = 3;
    }

    public IReadOnlyList<LiveTrack>? Tracks
    {
        get => (IReadOnlyList<LiveTrack>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    /// <summary>Сколько частей между делениями (длина повтора / 5 с).</summary>
    public int Divisions
    {
        get => (int)GetValue(DivisionsProperty);
        set => SetValue(DivisionsProperty, value);
    }

    public Brush? PeakBrush
    {
        get => (Brush?)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public Brush? RmsBrush
    {
        get => (Brush?)GetValue(RmsBrushProperty);
        set => SetValue(RmsBrushProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        RowDefinitions.Clear();
        lanes.Clear();
        var tracks = Tracks ?? [];
        var mono = Application.Current.Resources.TryGetValue("XFontMono", out var font) ? (FontFamily)font : null;
        for (var i = 0; i < tracks.Count; i++)
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var lane = new Grid { ColumnSpacing = 8 };
            lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = tracks[i].Title.ToUpperInvariant(),
                FontSize = 10,
                CharacterSpacing = 40,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (mono is not null)
                label.FontFamily = mono;
            var wave = new LiveWaveView { Source = tracks[i].Source };
            SetColumn(wave, 1);
            lane.Children.Add(label);
            lane.Children.Add(wave);
            SetRow(lane, i);
            Children.Add(lane);
            lanes.Add((label, wave));
        }

        Sync();
    }

    private void Sync()
    {
        foreach (var (label, wave) in lanes)
        {
            if (LabelBrush is { } labelBrush)
                label.Foreground = labelBrush;
            wave.Divisions = Divisions;
            wave.PeakBrush = PeakBrush;
            wave.RmsBrush = RmsBrush;
            wave.LineBrush = LineBrush;
            wave.TickBrush = TickBrush;
        }
    }
}
