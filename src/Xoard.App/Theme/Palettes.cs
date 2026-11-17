namespace Xoard.App.Theme;

/// <summary>
/// Палитра: цвета интерфейса. Те же, что в трекерах (Гавань, Матча, …), плюс две свои — в цветах
/// логотипа. Все цвета текста подобраны с контрастом не ниже AA.
/// </summary>
public sealed record Palette(
    string Id,
    string Name,
    bool IsDark,
    string Bg,
    string Surface,
    string Sunken,
    string Line,
    string Ink,
    string Muted,
    string Primary,
    string PrimarySoft,
    string OnPrimary,
    string Accent,
    string AccentSoft,
    string OnAccent,
    string Track,
    string Wave,
    string Scrim);

public static class Palettes
{
    /// <summary>«Авто»: днём Xoard, при тёмной теме Windows — Xoard ночь.</summary>
    public const string AutoId = "auto";

    public static IReadOnlyList<Palette> All { get; } =
    [
        new("xoard", "Xoard", false, "#F5F3EA", "#FFFFFF", "#EBE8DC", "#E0DCCB", "#141414", "#5A5850", "#141414", "#FFF6B0", "#FFEF03", "#FFE600", "#FFF8C2", "#141414", "#E8E4D3", "#C4BE9F", "#5C141414"),
        new("xoard-night", "Xoard ночь", true, "#111111", "#1A1A1A", "#222222", "#2E2E2E", "#F4F2E8", "#B3B0A3", "#FFEF03", "#2E2C12", "#141414", "#FFEF03", "#2E2C12", "#141414", "#2A2A2A", "#5C5930", "#8C000000"),
        new("harbor", "Гавань", false, "#F7F3EE", "#FFFFFF", "#EFE8E0", "#E3DBD0", "#16262B", "#5B6468", "#0F4C5C", "#E3EEF0", "#FFFFFF", "#FB8B24", "#FDEBDD", "#2A1405", "#EBE1D5", "#9DBCC3", "#5716262B"),
        new("matcha", "Матча", false, "#F1F4EC", "#FFFFFF", "#E6EBDF", "#D9E0D0", "#1B2A1E", "#56635A", "#2D6A4F", "#E2EFE7", "#FFFFFF", "#E9A23B", "#FBEFD9", "#2A1A03", "#E3E9DA", "#9FC2AF", "#571B2A1E"),
        new("berry", "Ягода", false, "#FBF2F4", "#FFFFFF", "#F3E4E8", "#ECD8DE", "#2B1625", "#6B5663", "#7B1E57", "#F6E3EE", "#FFFFFF", "#FF7A85", "#FFE6E8", "#2B0A0D", "#F1DFE5", "#CF9DBB", "#572B1625"),
        new("ocean", "Океан", false, "#EEF3F8", "#FFFFFF", "#E2EAF2", "#D6E0EB", "#122238", "#52627A", "#1D4E89", "#E1EAF6", "#FFFFFF", "#FF6F59", "#FFE5E0", "#2B0D07", "#DFE8F2", "#9CB6D8", "#57122238"),
        new("lavender", "Лаванда", false, "#F5F3FA", "#FFFFFF", "#EBE7F4", "#DFD9EC", "#221D33", "#5F5A72", "#5B4B9A", "#EBE7F7", "#FFFFFF", "#F2A65A", "#FCEFDF", "#2A1704", "#E8E3F3", "#B5AAD9", "#57221D33"),
        new("graphite", "Графит", false, "#F2F2F0", "#FFFFFF", "#E7E7E4", "#DCDCD8", "#151515", "#5C5C5A", "#222222", "#EBEBE8", "#FFFFFF", "#FFB000", "#FFF1CC", "#2A1D00", "#E5E5E2", "#B4B4AE", "#57151515"),
        new("night", "Ночь", true, "#0E1719", "#152024", "#1F2D31", "#28393E", "#E8EFEE", "#9FB0B4", "#6CC0CF", "#1C3A41", "#06181C", "#FB8B24", "#3A2614", "#1A0B02", "#22343A", "#33626C", "#99040A0C"),
        new("espresso", "Эспрессо", true, "#17120F", "#211A16", "#2C241F", "#3A2F28", "#F2E9E1", "#BFAE9F", "#E2A86B", "#3A2C20", "#1F140A", "#FF7B54", "#40231A", "#2B0C03", "#30271F", "#715943", "#99080503"),
        new("oled", "OLED", true, "#000000", "#0E0E0E", "#1C1C1C", "#2A2A2A", "#F2F2F2", "#A8A8A8", "#8AB4F8", "#172235", "#08142A", "#FDD663", "#2E2810", "#241C00", "#1E1E1E", "#3A5178", "#A6000000"),
    ];

    /// <summary>Палитра по id; «авто» — по теме Windows.</summary>
    public static Palette Resolve(string? id, bool windowsIsDark)
    {
        if (string.IsNullOrEmpty(id) || id == AutoId)
            return Find(windowsIsDark ? "xoard-night" : "xoard")!;
        return Find(id) ?? Find("xoard")!;
    }

    public static Palette? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
