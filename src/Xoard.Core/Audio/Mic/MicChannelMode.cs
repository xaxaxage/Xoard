namespace Xoard.Core.Audio.Mic;

/// <summary>
/// Как многоканальный микрофон сводится в моно перед дублированием в L = R.
/// </summary>
public enum MicChannelMode
{
    /// <summary>Среднее всех каналов (по умолчанию). Для стерео-пары капсюлей в противофазе может гасить сигнал.</summary>
    Average = 0,

    /// <summary>Только первый канал (левый).</summary>
    Left = 1,

    /// <summary>Только второй канал (правый); у моно-устройства — единственный канал.</summary>
    Right = 2,
}
