using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Xoard.App.Theme;

/// <summary>
/// Форма интерфейса: скругления и рамки. Строгая — почти прямые углы и тонкие линии,
/// мягкая — как в трекерах. Привязки (<c>{Binding Card, Source={StaticResource Shape}}</c>)
/// обновляются на лету.
/// </summary>
public sealed partial class ShapeTokens : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSoft { get; set; }

    [ObservableProperty]
    public partial CornerRadius Card { get; set; }

    [ObservableProperty]
    public partial CornerRadius Sheet { get; set; }

    [ObservableProperty]
    public partial CornerRadius Control { get; set; }

    [ObservableProperty]
    public partial CornerRadius Inner { get; set; }

    [ObservableProperty]
    public partial CornerRadius Chip { get; set; }

    [ObservableProperty]
    public partial CornerRadius Pill { get; set; }

    /// <summary>Рамка карточек: в строгой форме — тонкая линия, в мягкой — без рамки.</summary>
    [ObservableProperty]
    public partial Thickness CardBorder { get; set; }

    public void Apply(bool soft)
    {
        IsSoft = soft;
        Card = new CornerRadius(soft ? 18 : 6);
        Sheet = new CornerRadius(soft ? 24 : 10);
        Control = new CornerRadius(soft ? 12 : 6);
        Inner = new CornerRadius(soft ? 9 : 4);
        Chip = new CornerRadius(soft ? 8 : 3);
        Pill = new CornerRadius(soft ? 999 : 8);
        CardBorder = new Thickness(soft ? 0 : 1);
    }
}

/// <summary>
/// Цвета интерфейса: кисти в ресурсах приложения (XBg, XInk, XPrimary, …), которые меняют цвет
/// на месте, — всё, что на них ссылается, перекрашивается сразу (с плавным переходом 0,3 с).
/// Стандартные элементы WinUI (поля, переключатели, диалоги, меню) получают те же кисти.
/// </summary>
public sealed class ThemeManager
{
    private static readonly string[] Tokens =
        ["Bg", "Surface", "Sunken", "Line", "Ink", "Muted", "Primary", "PrimarySoft", "OnPrimary", "Accent", "AccentSoft", "OnAccent", "Track", "Wave", "Scrim"];

    /// <summary>Ключи WinUI → наши цвета.</summary>
    private static readonly (string Key, string Token)[] WinUiKeys =
    [
        ("TextFillColorPrimaryBrush", "Ink"), ("TextFillColorSecondaryBrush", "Muted"), ("TextFillColorTertiaryBrush", "Muted"),
        ("CardBackgroundFillColorDefaultBrush", "Surface"), ("CardStrokeColorDefaultBrush", "Line"),
        ("AccentFillColorDefaultBrush", "Primary"), ("AccentFillColorSecondaryBrush", "Primary"), ("AccentFillColorTertiaryBrush", "Primary"),
        ("AccentTextFillColorPrimaryBrush", "Primary"), ("AccentTextFillColorSecondaryBrush", "Primary"), ("TextOnAccentFillColorPrimaryBrush", "OnPrimary"),
        ("AccentButtonBackground", "Primary"), ("AccentButtonBackgroundPointerOver", "Primary"), ("AccentButtonBackgroundPressed", "Primary"),
        ("AccentButtonForeground", "OnPrimary"), ("AccentButtonForegroundPointerOver", "OnPrimary"), ("AccentButtonForegroundPressed", "OnPrimary"),
        ("ButtonBackground", "Sunken"), ("ButtonBackgroundPointerOver", "Line"), ("ButtonBackgroundPressed", "Line"),
        ("ButtonForeground", "Ink"), ("ButtonForegroundPointerOver", "Ink"), ("ButtonForegroundPressed", "Ink"),
        ("ButtonBorderBrush", "Line"), ("ButtonBorderBrushPointerOver", "Line"), ("ButtonBorderBrushPressed", "Line"),
        ("ToggleSwitchFillOn", "Primary"), ("ToggleSwitchFillOnPointerOver", "Primary"), ("ToggleSwitchFillOnPressed", "Primary"),
        ("ToggleSwitchKnobFillOn", "OnPrimary"), ("ToggleSwitchKnobFillOnPointerOver", "OnPrimary"), ("ToggleSwitchKnobFillOnPressed", "OnPrimary"),
        ("ToggleSwitchStrokeOff", "Muted"), ("ToggleSwitchStrokeOffPointerOver", "Ink"), ("ToggleSwitchKnobFillOff", "Muted"),
        ("ToggleSwitchKnobFillOffPointerOver", "Ink"), ("ToggleSwitchKnobFillOffPressed", "Ink"), ("ToggleSwitchContentForeground", "Ink"),
        ("SliderTrackValueFill", "Primary"), ("SliderTrackValueFillPointerOver", "Primary"), ("SliderTrackValueFillPressed", "Primary"),
        ("SliderTrackFill", "Track"), ("SliderTrackFillPointerOver", "Track"), ("SliderTrackFillPressed", "Track"),
        ("SliderThumbBackground", "Primary"), ("SliderThumbBackgroundPointerOver", "Primary"), ("SliderThumbBackgroundPressed", "Primary"),
        ("SliderOuterThumbBackground", "Surface"),
        ("TextControlBackground", "Surface"), ("TextControlBackgroundPointerOver", "Surface"), ("TextControlBackgroundFocused", "Surface"),
        ("TextControlBorderBrush", "Line"), ("TextControlBorderBrushPointerOver", "Muted"), ("TextControlBorderBrushFocused", "Primary"),
        ("TextControlForeground", "Ink"), ("TextControlForegroundPointerOver", "Ink"), ("TextControlForegroundFocused", "Ink"),
        ("TextControlPlaceholderForeground", "Muted"), ("TextControlPlaceholderForegroundPointerOver", "Muted"), ("TextControlPlaceholderForegroundFocused", "Muted"),
        ("TextControlButtonForeground", "Muted"), ("TextControlButtonForegroundPointerOver", "Ink"),
        ("ComboBoxBackground", "Surface"), ("ComboBoxBackgroundPointerOver", "Surface"), ("ComboBoxBackgroundPressed", "Sunken"),
        ("ComboBoxBorderBrush", "Line"), ("ComboBoxBorderBrushPointerOver", "Muted"), ("ComboBoxForeground", "Ink"),
        ("ComboBoxDropDownBackground", "Surface"), ("ComboBoxDropDownBorderBrush", "Line"), ("ComboBoxDropDownGlyphForeground", "Muted"),
        ("ContentDialogBackground", "Surface"), ("ContentDialogTopOverlay", "Surface"), ("ContentDialogForeground", "Ink"), ("ContentDialogBorderBrush", "Line"),
        ("FlyoutPresenterBackground", "Surface"), ("FlyoutBorderThemeBrush", "Line"),
        ("MenuFlyoutPresenterBackground", "Surface"), ("MenuFlyoutPresenterBorderBrush", "Line"), ("MenuFlyoutItemForeground", "Ink"),
        ("MenuFlyoutItemForegroundPointerOver", "Ink"), ("MenuFlyoutItemBackgroundPointerOver", "Sunken"), ("MenuFlyoutSeparatorBackground", "Line"),
        ("ToolTipBackground", "Ink"), ("ToolTipForeground", "Bg"), ("ToolTipBorderBrush", "Ink"),
        ("ScrollBarThumbFill", "Wave"), ("ScrollBarThumbFillPointerOver", "Muted"), ("ScrollBarThumbFillPressed", "Muted"),
        ("HyperlinkButtonForeground", "Primary"), ("HyperlinkButtonForegroundPointerOver", "Primary"),
        ("ProgressBarForeground", "Primary"), ("ProgressBarBackground", "Track"),
        ("ExpanderHeaderBackground", "Surface"), ("ExpanderContentBackground", "Surface"), ("ExpanderHeaderForeground", "Ink"),
        ("ExpanderHeaderBorderBrush", "Line"), ("ExpanderContentBorderBrush", "Line"), ("ExpanderChevronForeground", "Muted"),
        ("ExpanderHeaderBackgroundPointerOver", "Surface"), ("ExpanderHeaderBackgroundPressed", "Sunken"),
        ("ToggleButtonBackground", "Surface"), ("ToggleButtonBackgroundChecked", "Primary"), ("ToggleButtonBackgroundCheckedPointerOver", "Primary"),
        ("ToggleButtonForegroundChecked", "OnPrimary"), ("ToggleButtonForegroundCheckedPointerOver", "OnPrimary"), ("ToggleButtonBorderBrush", "Line"),
        ("InfoBarInformationalSeverityBackgroundBrush", "Sunken"),
    ];

    private readonly Dictionary<string, SolidColorBrush> brushes = [];
    private readonly Dictionary<string, Color> targets = [];
    private DispatcherQueueTimer? fadeTimer;
    private Dictionary<string, Color> fadeFrom = [];
    private DateTime fadeStart;

    private ThemeManager()
    {
    }

    public static ThemeManager Current { get; private set; } = null!;

    public ShapeTokens Shape { get; } = new();

    /// <summary>Выбранная палитра (с учётом «авто»).</summary>
    public Palette Palette { get; private set; } = Palettes.All[0];

    public ElementTheme ElementTheme => Palette.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>Палитра сменилась (для заголовка окна и т. п.).</summary>
    public event Action? Changed;

    /// <summary>Положить кисти в ресурсы приложения. Вызывать до создания окна.</summary>
    public static ThemeManager Install(ResourceDictionary resources, string? paletteId, bool windowsIsDark, string? shape)
    {
        var manager = new ThemeManager();
        manager.Palette = Palettes.Resolve(paletteId, windowsIsDark);
        foreach (var token in Tokens)
        {
            var color = ToColor(Get(manager.Palette, token));
            manager.targets[token] = color;
            // Кисти объявлены в App.xaml (на них ссылаются стили) — берём их и меняем цвет
            if (resources.TryGetValue("X" + token, out var existing) && existing is SolidColorBrush brush)
                brush.Color = color;
            else
                resources["X" + token] = brush = new SolidColorBrush(color);
            manager.brushes[token] = brush;
        }

        foreach (var (key, token) in WinUiKeys)
            resources[key] = manager.brushes[token];

        var soft = shape == "soft";
        manager.Shape.Apply(soft);
        resources["Shape"] = manager.Shape;
        // Стандартные элементы берут скругления при загрузке — после смены формы они обновятся при следующем запуске
        resources["ControlCornerRadius"] = new CornerRadius(soft ? 8 : 4);
        resources["OverlayCornerRadius"] = new CornerRadius(soft ? 12 : 6);
        Current = manager;
        return manager;
    }

    public Color this[string token] => targets[token];

    public void ApplyPalette(string? paletteId, bool windowsIsDark, bool animate, DispatcherQueue dispatcher)
    {
        Palette = Palettes.Resolve(paletteId, windowsIsDark);
        foreach (var token in Tokens)
            targets[token] = ToColor(Get(Palette, token));

        fadeTimer?.Stop();
        if (!animate)
        {
            foreach (var token in Tokens)
                brushes[token].Color = targets[token];
        }
        else
        {
            fadeFrom = Tokens.ToDictionary(t => t, t => brushes[t].Color);
            fadeStart = DateTime.UtcNow;
            fadeTimer ??= CreateFadeTimer(dispatcher);
            fadeTimer.Start();
        }

        Changed?.Invoke();
    }

    public void ApplyShape(string? shape) => Shape.Apply(shape == "soft");

    private DispatcherQueueTimer CreateFadeTimer(DispatcherQueue dispatcher)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - fadeStart).TotalMilliseconds / 300.0, 0, 1);
            var k = 1 - Math.Pow(1 - t, 3);
            foreach (var token in Tokens)
                brushes[token].Color = Lerp(fadeFrom[token], targets[token], k);
            if (t >= 1)
                timer.Stop();
        };
        return timer;
    }

    private static Color Lerp(Color a, Color b, double k) => Color.FromArgb(
        (byte)Math.Round(a.A + ((b.A - a.A) * k)),
        (byte)Math.Round(a.R + ((b.R - a.R) * k)),
        (byte)Math.Round(a.G + ((b.G - a.G) * k)),
        (byte)Math.Round(a.B + ((b.B - a.B) * k)));

    private static string Get(Palette p, string token) => token switch
    {
        "Bg" => p.Bg,
        "Surface" => p.Surface,
        "Sunken" => p.Sunken,
        "Line" => p.Line,
        "Ink" => p.Ink,
        "Muted" => p.Muted,
        "Primary" => p.Primary,
        "PrimarySoft" => p.PrimarySoft,
        "OnPrimary" => p.OnPrimary,
        "Accent" => p.Accent,
        "AccentSoft" => p.AccentSoft,
        "OnAccent" => p.OnAccent,
        "Track" => p.Track,
        "Wave" => p.Wave,
        _ => p.Scrim,
    };

    /// <summary>«#RRGGBB» или «#AARRGGBB».</summary>
    public static Color ToColor(string hex)
    {
        var h = hex.TrimStart('#');
        var argb = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (h.Length == 6)
            argb |= 0xFF000000;
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    /// <summary>Тёмная ли сейчас тема Windows (для «авто»).</summary>
    public static bool WindowsIsDark()
    {
        var bg = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return bg.R + bg.G + bg.B < 3 * 128;
    }

    /// <summary>Кисть палитры для кода (диалоги, создаваемые на лету).</summary>
    public Brush Brush(string token) => brushes[token];

    /// <summary>Прозрачная кисть.</summary>
    public static Brush Transparent { get; } = new SolidColorBrush(Colors.Transparent);
}
