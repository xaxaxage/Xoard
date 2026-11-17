using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Xoard.App.Controls;

/// <summary>Откуда живая волна берёт звук (повтор: последние N секунд звука компьютера).</summary>
public interface ILiveWaveSource
{
    /// <summary>Сколько отрезков записано за всё время: изменилось — пришёл новый.</summary>
    long Written { get; }

    /// <summary>Сколько отрезков показывать (длина повтора / длина отрезка).</summary>
    int Buckets { get; }

    /// <summary>Длина одного отрезка, секунд.</summary>
    double BucketSeconds { get; }

    /// <summary>Пики и средняя громкость последних отрезков (0..1, новый — в конце).</summary>
    void Copy(float[] peaks, float[] rms);

    /// <summary>Вызывается каждый кадр (досыпать тишину и т. п.).</summary>
    void Tick();
}

/// <summary>
/// Живая волна повтора, как в аудиоредакторе: светлый слой — пики, яркий — средняя громкость (RMS),
/// средняя линия и деления. Обновляется каждый кадр и между новыми отрезками плавно сдвигается
/// влево, поэтому не дёргается.
/// </summary>
public sealed partial class LiveWaveView : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ILiveWaveSource), typeof(LiveWaveView), new PropertyMetadata(null));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush), typeof(Brush), typeof(LiveWaveView), new PropertyMetadata(null, (d, e) => ((LiveWaveView)d).peak.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty RmsBrushProperty = DependencyProperty.Register(
        nameof(RmsBrush), typeof(Brush), typeof(LiveWaveView), new PropertyMetadata(null, (d, e) => ((LiveWaveView)d).rms.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(LiveWaveView), new PropertyMetadata(null, (d, e) => ((LiveWaveView)d).line.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(LiveWaveView), new PropertyMetadata(null, (d, _) => ((LiveWaveView)d).DrawTicks()));

    public static readonly DependencyProperty DivisionsProperty = DependencyProperty.Register(
        nameof(Divisions), typeof(int), typeof(LiveWaveView), new PropertyMetadata(6, (d, _) => ((LiveWaveView)d).DrawTicks()));

    private readonly Canvas ticks = new() { IsHitTestVisible = false };
    private readonly Rectangle line = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid layer = new();
    private readonly Polygon peak = new();
    private readonly Polygon rms = new();
    private readonly TranslateTransform shift = new();
    private readonly RectangleGeometry clip = new();
    private readonly Stopwatch sinceNew = Stopwatch.StartNew();
    private float[] peaks = [];
    private float[] levels = [];
    private long lastWritten = -1;
    private double step;

    public LiveWaveView()
    {
        IsHitTestVisible = false;
        Clip = clip;
        layer.RenderTransform = shift;
        layer.Children.Add(peak);
        layer.Children.Add(rms);
        Children.Add(ticks);
        Children.Add(line);
        Children.Add(layer);
        SizeChanged += (_, e) =>
        {
            clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            lastWritten = -1;   // перерисовать под новый размер
            DrawTicks();
        };
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public ILiveWaveSource? Source
    {
        get => (ILiveWaveSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
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

    /// <summary>Сколько частей между делениями (длина повтора / 5 с).</summary>
    public int Divisions
    {
        get => (int)GetValue(DivisionsProperty);
        set => SetValue(DivisionsProperty, value);
    }

    private void OnFrame(object? sender, object e)
    {
        var source = Source;
        if (source is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        source.Tick();
        var written = source.Written;
        if (written != lastWritten)
        {
            lastWritten = written;
            sinceNew.Restart();
            var n = Math.Max(2, source.Buckets);
            if (peaks.Length != n)
            {
                peaks = new float[n];
                levels = new float[n];
            }

            source.Copy(peaks, levels);
            step = ActualWidth / (n - 1);
            peak.Points = Build(peaks);
            rms.Points = Build(levels);
        }

        // Между новыми отрезками волна едет влево плавно, а не рывком на целый отрезок
        var fraction = Math.Clamp(sinceNew.Elapsed.TotalSeconds / source.BucketSeconds, 0, 1);
        shift.X = -fraction * step;
    }

    private PointCollection Build(float[] values)
    {
        var points = new PointCollection();
        var n = values.Length;
        var mid = ActualHeight / 2;
        for (var i = 0; i < n; i++)
            points.Add(new Point(step * i, mid - Amp(values[i], mid)));
        for (var i = n - 1; i >= 0; i--)
            points.Add(new Point(step * i, mid + Amp(values[i], mid)));
        return points;
    }

    /// <summary>Громкость → высота: мягкая кривая, чтобы и тихое было видно, и громкое не сливалось в брусок.</summary>
    private static double Amp(float v, double mid) => Math.Max(0.5, Math.Pow(Math.Clamp(v, 0f, 1f), 0.7) * (mid - 1));

    private void DrawTicks()
    {
        ticks.Children.Clear();
        var brush = TickBrush;
        var parts = Divisions;
        if (brush is null || parts < 1 || ActualWidth <= 0)
            return;
        for (var i = 1; i < parts; i++)
        {
            var x = Math.Round(ActualWidth * i / parts);
            var tick = new Rectangle { Width = 1, Height = ActualHeight, Fill = brush };
            Canvas.SetLeft(tick, x);
            ticks.Children.Add(tick);
        }
    }
}
