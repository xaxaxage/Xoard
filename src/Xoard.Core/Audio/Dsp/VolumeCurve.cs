namespace Xoard.Core.Audio.Dsp;

/// <summary>
/// Кривая громкости: уровень ползунка 0..2 (проценты = level · 100) ↔ линейное усиление.
/// До 100 % — по кубу (gain = level³, 0.5 ≈ −18 дБ), 100 % — ровно 0 дБ, выше — усиление
/// (gain = level², 200 % ≈ +12 дБ; от перегруза защищает ограничитель на −1 dBFS).
/// Общая для UI, именованного канала и MCP.
/// </summary>
internal static class VolumeCurve
{
    /// <summary>Минимальный уровень (тишина).</summary>
    public const float MinLevel = 0f;

    /// <summary>Уровень без усиления (0 дБ, 100 %).</summary>
    public const float UnityLevel = 1f;

    /// <summary>Максимальный уровень (200 %, ≈ +12 дБ).</summary>
    public const float MaxLevel = 2f;

    /// <summary>Приводит уровень к [0, 2]; NaN считается нулём.</summary>
    public static float ClampLevel(float level) =>
        float.IsNaN(level) ? MinLevel : Math.Clamp(level, MinLevel, MaxLevel);

    /// <summary>true, если уровень конечен и лежит в [0, 2] (для проверки аргументов API).</summary>
    public static bool IsValidLevel(float level) => level is >= MinLevel and <= MaxLevel;

    /// <summary>Линейное усиление для уровня (уровень предварительно ограничивается [0, 2]).</summary>
    public static float ToGain(float level)
    {
        var l = ClampLevel(level);
        return l <= UnityLevel ? l * l * l : l * l;
    }

    /// <summary>Обратное преобразование (усиление ограничивается [0, 4]; NaN → 0).</summary>
    public static float ToLevel(float gain)
    {
        var g = float.IsNaN(gain) ? 0f : Math.Clamp(gain, 0f, MaxLevel * MaxLevel);
        return g <= 1f ? MathF.Cbrt(g) : MathF.Sqrt(g);
    }

    /// <summary>Усиление уровня в дБ; 0 → <see cref="Decibels.Floor"/>.</summary>
    public static float ToDecibels(float level) => Decibels.FromLinear(ToGain(level));

    /// <summary>Уровень для заданного усиления в дБ (выше +12 дБ — 2.0).</summary>
    public static float FromDecibels(float decibels) => ToLevel(Decibels.ToLinear(decibels));
}
