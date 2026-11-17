using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Xoard.App.ViewModels;
using Xoard.Core.Recording;

namespace Xoard.App.Views;

/// <summary>
/// Редактор клипа: новая запись или повтор (ещё не файл) — сохранить выделенное, целиком или
/// выбросить; звук из меню «Обрезать…» — сохранить обрезку или отменить.
/// </summary>
public sealed partial class ClipEditor : UserControl
{
    private const double HandleGrab = 8;
    private const double MinSelection = 0.05;

    private readonly MainViewModel vm;
    private readonly ClipDraft draft;
    private readonly Action close;
    private readonly double duration;
    private readonly Polygon wave = new();
    private readonly Polygon selectedWave = new();
    private readonly Rectangle selection = new() { Opacity = 0.55 };
    private readonly Rectangle startHandle = new() { Width = 2 };
    private readonly Rectangle endHandle = new() { Width = 2 };
    private readonly Border startGrip = new() { Width = 12, Height = 14 };
    private readonly Border endGrip = new() { Width = 12, Height = 14 };
    private WavePeak[] peaks = [];
    private double selStart;
    private double selEnd;
    private Drag drag;
    private double anchor;
    private bool busy;

    private enum Drag
    {
        None,
        Start,
        End,
        New,
    }

    public ClipEditor(MainViewModel vm, ClipDraft draft, Action close)
    {
        this.vm = vm;
        this.draft = draft;
        this.close = close;
        duration = Math.Max(0.001, draft.Clip.Seconds);
        InitializeComponent();

        var res = Application.Current.Resources;
        wave.Fill = (Brush)res["XWave"];
        selectedWave.Fill = (Brush)res["XPrimary"];
        selection.Fill = (Brush)res["XPrimarySoft"];
        startHandle.Fill = endHandle.Fill = (Brush)res["XPrimary"];
        startGrip.Background = endGrip.Background = (Brush)res["XPrimary"];
        WaveCanvas.Children.Add(selection);
        WaveCanvas.Children.Add(wave);
        WaveCanvas.Children.Add(selectedWave);
        WaveCanvas.Children.Add(startHandle);
        WaveCanvas.Children.Add(endHandle);
        WaveCanvas.Children.Add(startGrip);
        WaveCanvas.Children.Add(endGrip);

        TitleText.Text = draft.Title;
        SubtitleText.Text = draft.Subtitle;
        if (draft.IsTrim)
        {
            NamePanel.Visibility = Visibility.Collapsed;
            SaveWholeButton.Visibility = Visibility.Collapsed;
            DiscardButton.Content = "Отмена";
            DiscardButton.Foreground = (Brush)res["XMuted"];
            SaveButton.Content = "Сохранить обрезку";
            HintText.Text = "Выделите кусок, который нужно оставить. Оригинал уйдёт в корзину Windows — его можно вернуть оттуда.";
        }
        else
        {
            NameBox.Text = draft.Clip.SuggestedName;
        }

        (selStart, selEnd) = WithoutSilentEdges();
        Loaded += (_, _) =>
        {
            if (!draft.IsTrim)
            {
                NameBox.Focus(FocusState.Programmatic);
                NameBox.SelectAll();
            }
        };
    }

    /// <summary>Esc: в обрезке — отмена; новую запись случайно не выбрасываем.</summary>
    public bool CanCancelWithEscape => draft.IsTrim;

    public void Cancel() => Close();

    // ───────── рисование ─────────

    private void OnWaveSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var buckets = Math.Max(16, (int)e.NewSize.Width);
        peaks = AudioClips.Peaks(draft.Clip.Stereo, buckets);
        wave.Points = BuildWave(0, peaks.Length);
        DrawRuler();
        Redraw();
    }

    private PointCollection BuildWave(int from, int to)
    {
        var points = new PointCollection();
        var w = WaveCanvas.ActualWidth;
        var h = WaveCanvas.ActualHeight;
        if (peaks.Length == 0 || to <= from || w <= 0)
            return points;
        var mid = h / 2;
        var scale = (mid - 4) / Math.Max(0.05, peaks.Max(p => Math.Max(-p.Min, p.Max)));
        for (var i = from; i < to; i++)
            points.Add(new Point(i * w / peaks.Length, mid - Math.Max(0.5, peaks[i].Max * scale)));
        for (var i = to - 1; i >= from; i--)
            points.Add(new Point(i * w / peaks.Length, mid - Math.Min(-0.5, peaks[i].Min * scale)));
        return points;
    }

    private void Redraw()
    {
        var w = WaveCanvas.ActualWidth;
        var h = WaveCanvas.ActualHeight;
        if (w <= 0)
            return;
        var x0 = selStart / duration * w;
        var x1 = selEnd / duration * w;
        Canvas.SetLeft(selection, x0);
        selection.Width = Math.Max(1, x1 - x0);
        selection.Height = h;
        Canvas.SetLeft(startHandle, x0 - 1);
        Canvas.SetLeft(endHandle, x1 - 1);
        startHandle.Height = endHandle.Height = h;
        Canvas.SetLeft(startGrip, x0 - 6);
        Canvas.SetLeft(endGrip, x1 - 6);
        selectedWave.Points = BuildWave((int)Math.Floor(selStart / duration * peaks.Length), (int)Math.Ceiling(selEnd / duration * peaks.Length));

        var whole = IsWhole;
        SelectionText.Text = whole
            ? $"Выделено всё · {Seconds(duration)} с"
            : $"{Clock(selStart)} – {Clock(selEnd)} · выделено {Seconds(selEnd - selStart)} с";
        LengthText.Text = $"длина {Clock(duration)}";
        if (!draft.IsTrim)
        {
            SaveButton.Content = whole ? "Сохранить" : "Сохранить выделенное";
            SaveWholeButton.Visibility = whole ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void DrawRuler()
    {
        Ruler.Children.Clear();
        var w = Ruler.ActualWidth > 0 ? Ruler.ActualWidth : WaveCanvas.ActualWidth;
        if (w <= 0)
            return;
        double[] steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60];
        var step = steps.FirstOrDefault(s => duration / s <= 12, 60);
        var mono = (FontFamily)Application.Current.Resources["XFontMono"];
        var muted = (Brush)Application.Current.Resources["XMuted"];
        var line = (Brush)Application.Current.Resources["XLine"];
        for (var t = 0.0; t <= duration + 1e-6; t += step)
        {
            var x = t / duration * w;
            var tick = new Rectangle { Width = 1, Height = 5, Fill = line };
            Canvas.SetLeft(tick, x);
            Ruler.Children.Add(tick);
            if (x > w - 30)
                continue;
            var label = new TextBlock { Text = Seconds(t, step < 1 ? 2 : 0), FontFamily = mono, FontSize = 11, Foreground = muted };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 2);
            Ruler.Children.Add(label);
        }
    }

    // ───────── мышь ─────────

    private double TimeAt(PointerRoutedEventArgs e) =>
        Math.Clamp(e.GetCurrentPoint(WaveCanvas).Position.X / Math.Max(1, WaveCanvas.ActualWidth) * duration, 0, duration);

    private void OnWavePressed(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(WaveCanvas).Position.X;
        var w = Math.Max(1, WaveCanvas.ActualWidth);
        drag = Math.Abs(x - (selStart / duration * w)) <= HandleGrab ? Drag.Start
             : Math.Abs(x - (selEnd / duration * w)) <= HandleGrab ? Drag.End
             : Drag.New;
        anchor = TimeAt(e);
        WaveCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnWaveMoved(object sender, PointerRoutedEventArgs e)
    {
        if (drag == Drag.None)
            return;
        var t = TimeAt(e);
        switch (drag)
        {
            case Drag.Start:
                selStart = Math.Min(t, selEnd - MinSelection);
                break;
            case Drag.End:
                selEnd = Math.Max(t, selStart + MinSelection);
                break;
            default:
                if (Math.Abs(t - anchor) < MinSelection)
                    return;
                selStart = Math.Min(anchor, t);
                selEnd = Math.Max(anchor, t);
                break;
        }

        selStart = Math.Max(0, selStart);
        selEnd = Math.Min(duration, selEnd);
        Redraw();
    }

    private void OnWaveReleased(object sender, PointerRoutedEventArgs e)
    {
        drag = Drag.None;
        WaveCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnWaveCaptureLost(object sender, PointerRoutedEventArgs e) => drag = Drag.None;

    // ───────── действия ─────────

    private bool IsWhole => selStart <= 0.001 && selEnd >= duration - 0.001;

    private void OnPreview(object sender, RoutedEventArgs e) => vm.PreviewClip(draft.Clip, selStart, selEnd);

    private void OnStopPreview(object sender, RoutedEventArgs e) => vm.StopPreview();

    private void OnResetSelection(object sender, RoutedEventArgs e)
    {
        selStart = 0;
        selEnd = duration;
        Redraw();
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (!draft.IsTrim)
            vm.ShowToast("Клип выброшен — файл не сохранялся.", InfoBarSeverity.Informational);
        Close();
    }

    private async void OnSaveWhole(object sender, RoutedEventArgs e) => await SaveAsync(0, duration);

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync(selStart, selEnd);

    private async Task SaveAsync(double start, double end)
    {
        if (busy)
            return;
        busy = true;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            string? error;
            if (draft.TrimTarget is { } target)
                error = IsWhole ? null : await vm.TrimAsync(target, start, end);
            else
                error = await vm.SaveClipAsync(draft, NameBox.Text, start, end);

            if (error is null)
            {
                Close();
                return;
            }

            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            busy = false;
        }
    }

    private void Close()
    {
        vm.StopPreview();
        close();
    }

    /// <summary>Сначала выделено всё, кроме тишины по краям (с запасом 0,1 с).</summary>
    private (double Start, double End) WithoutSilentEdges()
    {
        const int buckets = 2000;
        var p = AudioClips.Peaks(draft.Clip.Stereo, buckets);
        var first = Array.FindIndex(p, x => Math.Max(-x.Min, x.Max) > 0.02f);
        var last = Array.FindLastIndex(p, x => Math.Max(-x.Min, x.Max) > 0.02f);
        if (first < 0)
            return (0, duration);
        var start = Math.Max(0, (first / (double)buckets * duration) - 0.1);
        var end = Math.Min(duration, ((last + 1) / (double)buckets * duration) + 0.1);
        return end - start < MinSelection ? (0, duration) : (start, end);
    }

    private static string Clock(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60):00}:{seconds % 60:00.000}");

    private static string Seconds(double seconds, int digits = 2) =>
        seconds.ToString(digits == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}
