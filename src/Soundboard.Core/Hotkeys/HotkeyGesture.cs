using System.Globalization;

namespace Soundboard.Core.Hotkeys;

/// <summary>Клавиши-модификаторы (значения совпадают с MOD_* у RegisterHotKey).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// Сочетание клавиш: модификаторы + виртуальный код клавиши Windows (VK_*).
/// Текстовый вид — «Ctrl+Alt+F1», «Num5», «Shift+Q»; так же оно хранится в настройках.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> Codes = Names
        .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Сочетание можно зарегистрировать: есть основная клавиша, и это не сам модификатор.</summary>
    public bool IsValid => VirtualKey is > 0 and < 0xFF && !IsModifierKey(VirtualKey) && Names.ContainsKey(VirtualKey);

    /// <summary>Клавиша — модификатор (Shift, Ctrl, Alt, Win, в том числе левые/правые).</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>Имя клавиши по коду (null — клавиша не поддерживается).</summary>
    public static string? KeyName(int vk) => Names.GetValueOrDefault(vk);

    /// <summary>Разбор текста «Ctrl+Alt+F1». Регистр не важен; «Control», «Ctl» тоже понимаются.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // «Num+» сама содержит плюс — отделяем её от модификаторов вручную
        var trimmed = text.Trim();
        var parts = trimmed.EndsWith("Num+", StringComparison.OrdinalIgnoreCase)
            ? [.. trimmed[..^4].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), "Num+"]
            : trimmed.Split('+', StringSplitOptions.TrimEntries);

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => (HotkeyModifiers?)null,
            };
            if (m is null || (modifiers & m) != 0)
                return false;
            modifiers |= m.Value;
        }

        if (!Codes.TryGetValue(parts[^1], out var vk))
            return false;
        gesture = new HotkeyGesture(modifiers, vk);
        return gesture.IsValid;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Control) != 0)
            parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0)
            parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0)
            parts.Add("Shift");
        if ((Modifiers & HotkeyModifiers.Win) != 0)
            parts.Add("Win");
        parts.Add(KeyName(VirtualKey) ?? $"0x{VirtualKey.ToString("X2", CultureInfo.InvariantCulture)}");
        return string.Join("+", parts);
    }

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++)
            names[c] = c.ToString();
        for (var d = 0; d <= 9; d++)
        {
            names[0x30 + d] = d.ToString(CultureInfo.InvariantCulture);
            names[0x60 + d] = $"Num{d}";
        }

        for (var f = 1; f <= 24; f++)
            names[0x6F + f] = $"F{f}";

        (int Vk, string Name)[] others =
        [
            (0x6A, "Num*"), (0x6B, "Num+"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/"),
            (0x20, "Space"), (0x0D, "Enter"), (0x09, "Tab"), (0x08, "Backspace"), (0x1B, "Esc"),
            (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "PageUp"), (0x22, "PageDown"),
            (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
            (0x13, "Pause"), (0x91, "ScrollLock"),
            (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
            (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
            (0xB3, "MediaPlayPause"), (0xB2, "MediaStop"), (0xB0, "MediaNext"), (0xB1, "MediaPrev"),
        ];
        foreach (var (vk, name) in others)
            names[vk] = name;
        return names;
    }
}
