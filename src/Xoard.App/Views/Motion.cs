using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Xoard.App.ViewModels;

namespace Xoard.App.Views;

/// <summary>Тихие анимации: только когда они включены в настройках и в Windows.</summary>
internal static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    public static bool On(MainViewModel vm) => vm.AnimationsEnabled && Ui.AnimationsEnabled;

    /// <summary>Короткое «нажатие» плитки: 1 → 0,95 → 1 за 0,18 с.</summary>
    public static void Pop(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0.35f, new Vector3(0.95f, 0.95f, 1));
        animation.InsertKeyFrame(1f, Vector3.One);
        animation.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Scale", animation);
    }

    /// <summary>
    /// Панель въезжает со сдвига (<paramref name="dx"/>, <paramref name="dy"/>) за 0,22 с — по умолчанию справа —
    /// или просто появляется, если анимации выключены.
    /// </summary>
    public static void SlideIn(UIElement element, bool animate, float dx = 36, float dy = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!animate)
        {
            visual.Offset = Vector3.Zero;
            visual.Opacity = 1;
            return;
        }

        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(dx, dy, 0));
        offset.InsertKeyFrame(1f, Vector3.Zero, ease);
        offset.Duration = TimeSpan.FromMilliseconds(220);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Offset", offset);
        visual.StartAnimation("Opacity", fade);
    }
}
