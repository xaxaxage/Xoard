namespace Soundboard.Core.Audio.Dsp;

/// <summary>
/// Перевод между линейной амплитудой и децибелами (dBFS для сэмплов, dB для усиления).
/// Тишина и всё тише <see cref="Floor"/> отображается в <see cref="Floor"/>, а не в −∞:
/// значения попадают в статус и JSON (именованный канал, MCP), где бесконечность недопустима.
/// </summary>
internal static class Decibels
{
    /// <summary>Нижняя граница шкалы, дБ. Соответствует амплитуде 1e-6.</summary>
    public const float Floor = -120f;

    /// <summary>Амплитуда, соответствующая <see cref="Floor"/>.</summary>
    public const float FloorLinear = 1e-6f;

    /// <summary>
    /// 20·log10(|amplitude|), но не ниже <see cref="Floor"/>. Ноль, отрицательный ноль и NaN
    /// дают <see cref="Floor"/>; +∞ даёт +∞.
    /// </summary>
    public static float FromLinear(float amplitude)
    {
        var a = MathF.Abs(amplitude);
        return a > FloorLinear ? 20f * MathF.Log10(a) : Floor;
    }

    /// <summary>
    /// 10^(dB/20). Значения не выше <see cref="Floor"/> (в том числе −∞) и NaN дают ровно 0,
    /// поэтому <c>ToLinear(Floor)</c> — точная тишина.
    /// </summary>
    public static float ToLinear(float decibels) =>
        decibels > Floor ? MathF.Pow(10f, decibels / 20f) : 0f;
}
