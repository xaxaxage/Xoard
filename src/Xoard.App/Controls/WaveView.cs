using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Xoard.App.Controls;

/// <summary>
/// Острая волна, как в аудиоредакторе: симметричный многоугольник по пикам (0..1) от средней
/// линии. Уже проигранная часть (<see cref="Progress"/>) закрашивается другим цветом.
/// Для живой волны пики обновляются через <see cref="SetPeaks"/> без лишних привязок.
/// </summary>
public sealed partial class WaveView : Grid
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveView), new PropertyMetadata(null, (d, _) => ((WaveView)d).Redraw()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveView), new PropertyMetadata(0.0, (d, _) => ((WaveView)d).RedrawPlayed()));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveView), new PropertyMetadata(null, (d, e) => ((WaveView)d).full.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveView), new PropertyMetadata(null, (d, e) => ((WaveView)d).played.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(WaveView), new PropertyMetadata(null, (d, e) => ((WaveView)d).line.Fill = (Brush)e.NewValue));

    private readonly Polygon full = new();
    private readonly Polygon played = new();
    private readonly Rectangle line = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center };
    private float[]? peaks;

    public WaveView()
    {
        IsHitTestVisible = false;
        Children.Add(line);
        Children.Add(full);
        Children.Add(played);
        SizeChanged += (_, _) => Redraw();
    }

    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>Сколько проиграно (0..1); 0 — нет закрашенной части.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush? WaveBrush
    {
        get => (Brush?)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public Brush? PlayedBrush
    {
        get => (Brush?)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    /// <summary>Средняя линия (тишина); без кисти не рисуется.</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>Новые пики для живой волны (без привязки).</summary>
    public void SetPeaks(float[] values)
    {
        peaks = values;
        full.Points = Build(values, 0, 1);
    }

    private void Redraw()
    {
        peaks = Peaks ?? peaks;
        full.Points = Build(peaks, 0, 1);
        RedrawPlayed();
    }

    private void RedrawPlayed()
    {
        var p = Progress;
        played.Points = p > 0 ? Build(peaks, 0, Math.Min(1, p)) : [];
    }

    private PointCollection Build(float[]? values, double from, double to)
    {
        var points = new PointCollection();
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Length < 2 || w <= 0 || h <= 0)
            return points;

        var n = values.Length;
        var mid = h / 2;
        var a = Math.Max(0, (int)Math.Floor(from * (n - 1)));
        var b = Math.Min(n - 1, (int)Math.Ceiling(to * (n - 1)));
        var top = new Point[b - a + 1];
        var bottom = new Point[b - a + 1];
        for (var i = a; i <= b; i++)
        {
            var x = w * i / (n - 1);
            var amp = Math.Max(0.5, Math.Clamp(values[i], 0f, 1f) * (mid - 0.5));
            top[i - a] = new Point(x, mid - amp);
            bottom[i - a] = new Point(x, mid + amp);
        }

        foreach (var pt in top)
            points.Add(pt);
        for (var i = bottom.Length - 1; i >= 0; i--)
            points.Add(bottom[i]);
        return points;
    }
}
