namespace Soundboard.Core.Audio.Dsp;

/// <summary>
/// Кривая громкости (решение D14): уровень ползунка 0..1 ↔ линейное усиление по кубу,
/// gain = level³. Уровень 1.0 — ровно 0 дБ, усиления выше единицы нет (без «буста»),
/// 0.5 ≈ −18 дБ. Общая для UI, именованного канала и MCP (проценты = level · 100).
/// </summary>
internal static class VolumeCurve
{
    /// <summary>Минимальный уровень (тишина).</summary>
    public const float MinLevel = 0f;

    /// <summary>Максимальный уровень (0 дБ).</summary>
    public const float MaxLevel = 1f;

    /// <summary>Приводит уровень к [0, 1]; NaN считается нулём.</summary>
    public static float ClampLevel(float level) =>
        float.IsNaN(level) ? MinLevel : Math.Clamp(level, MinLevel, MaxLevel);

    /// <summary>true, если уровень конечен и лежит в [0, 1] (для проверки аргументов API).</summary>
    public static bool IsValidLevel(float level) => level is >= MinLevel and <= MaxLevel;

    /// <summary>Линейное усиление для уровня: level³ (уровень предварительно ограничивается [0, 1]).</summary>
    public static float ToGain(float level)
    {
        var l = ClampLevel(level);
        return l * l * l;
    }

    /// <summary>Обратное преобразование: ∛gain (усиление ограничивается [0, 1]; NaN → 0).</summary>
    public static float ToLevel(float gain)
    {
        var g = float.IsNaN(gain) ? 0f : Math.Clamp(gain, 0f, 1f);
        return MathF.Cbrt(g);
    }

    /// <summary>Усиление уровня в дБ (60·log10(level)); 0 → <see cref="Decibels.Floor"/>.</summary>
    public static float ToDecibels(float level) => Decibels.FromLinear(ToGain(level));

    /// <summary>Уровень для заданного усиления в дБ (значения выше 0 дБ дают 1.0).</summary>
    public static float FromDecibels(float decibels) => ToLevel(Decibels.ToLinear(decibels));
}
