using System.Runtime.CompilerServices;

namespace Xoard.Core.Audio.Dsp;

/// <summary>
/// Сглаженное усиление без «застёжки» (zipper noise): линейная рампа длиной
/// <see cref="RampFrames"/> кадров к целевому значению.
/// <list type="bullet">
/// <item>Цель задаётся из любого потока (<see cref="SetTarget"/>, Volatile-запись) и читается
/// аудиопотоком один раз в начале блока.</item>
/// <item>Новая цель перезапускает рампу от текущего значения (полная длина), так что
/// скачков нет даже при смене цели посреди рампы.</item>
/// <item>Рампа заканчивается точно на цели (кадр N получает её ровно), дальше коэффициент
/// постоянный. Установившееся 1.0 — побитово точный проход (буфер не трогается),
/// установившийся 0 — точные нули.</item>
/// <item>Результат не зависит от разбиения на блоки (детерминирован для тестов).</item>
/// </list>
/// <see cref="Apply"/>, <see cref="Reset"/>, <see cref="IsSettled"/> — только из потока-владельца
/// (аудиопоток; без потока — управляющий). Без аллокаций и блокировок.
/// </summary>
internal sealed class SmoothedGain
{
    private readonly int rampFrames;
    private readonly int channels;

    // Пишется из любого потока, читается аудиопотоком.
    private float target;

    // Публикуется аудиопотоком раз в блок (для статуса); владелец читает напрямую.
    private float current;

    // Состояние рампы — только поток-владелец.
    private float rampStart;
    private float rampEnd;
    private int rampPosition;

    /// <summary>Создаёт усиление, установившееся на <paramref name="initialGain"/>.</summary>
    /// <param name="rampFrames">Длина рампы в кадрах; 0 — мгновенная смена.</param>
    /// <param name="initialGain">Начальное (и целевое) усиление, линейное, ≥ 0.</param>
    /// <param name="channels">Число чередующихся каналов в обрабатываемых буферах.</param>
    public SmoothedGain(int rampFrames, float initialGain = 1f, int channels = AudioFormat.Channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rampFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rampFrames, Fade.MaxRampFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ValidateGain(initialGain);

        this.rampFrames = rampFrames;
        this.channels = channels;
        target = initialGain;
        current = initialGain;
        rampStart = initialGain;
        rampEnd = initialGain;
        rampPosition = rampFrames;
    }

    /// <summary>Создаёт усиление с рампой заданной длительности (в кадрах формата движка, 48 кГц).</summary>
    public static SmoothedGain FromMilliseconds(double rampMilliseconds, float initialGain = 1f, int channels = AudioFormat.Channels) =>
        new(Fade.FramesFor(rampMilliseconds), initialGain, channels);

    /// <summary>Длина рампы в кадрах.</summary>
    public int RampFrames => rampFrames;

    /// <summary>Число каналов в обрабатываемых буферах.</summary>
    public int Channels => channels;

    /// <summary>Целевое усиление (любой поток).</summary>
    public float Target => Volatile.Read(ref target);

    /// <summary>
    /// Усиление последнего обработанного кадра. Из потока-владельца — точное значение,
    /// из других потоков — значение на конец последнего блока.
    /// </summary>
    public float Current => Volatile.Read(ref current);

    /// <summary>true, если рампа завершена и цель с тех пор не менялась (поток-владелец).</summary>
    public bool IsSettled => rampPosition >= rampFrames && rampEnd == Volatile.Read(ref target);

    /// <summary>
    /// Задаёт новую цель (любой поток, без блокировок). Применится со следующего блока.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Значение отрицательное или не конечное.</exception>
    public void SetTarget(float gain)
    {
        ValidateGain(gain);
        Volatile.Write(ref target, gain);
    }

    /// <summary>
    /// Мгновенно устанавливает и цель, и текущее значение (без рампы). Только поток-владелец,
    /// не параллельно с <see cref="Apply"/>.
    /// </summary>
    public void Reset(float gain)
    {
        ValidateGain(gain);
        Volatile.Write(ref target, gain);
        rampStart = gain;
        rampEnd = gain;
        rampPosition = rampFrames;
        Volatile.Write(ref current, gain);
    }

    /// <summary>
    /// Применяет усиление к блоку чередующихся сэмплов на месте (аудиопоток).
    /// Длина блока должна быть кратна <see cref="Channels"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(Span<float> buffer)
    {
        SampleOps.EnsureWholeFrames(buffer.Length, channels, nameof(buffer));

        var t = Volatile.Read(ref target);
        if (t != rampEnd)
        {
            // Новая цель: рампа заново от текущего значения.
            rampStart = current;
            rampEnd = t;
            rampPosition = 0;
        }

        float gain;
        var offset = 0;
        if (rampPosition < rampFrames)
        {
            var done = Fade.ApplyLinearRamp(buffer, channels, rampStart, rampEnd, rampPosition, rampFrames);
            rampPosition += done;
            gain = Fade.GainAt(rampStart, rampEnd, rampPosition, rampFrames);
            offset = done * channels;
        }
        else
        {
            gain = rampEnd;
        }

        if (offset < buffer.Length)
            ApplyConstant(buffer[offset..], gain);

        if (BitConverter.SingleToInt32Bits(gain) != BitConverter.SingleToInt32Bits(current))
            Volatile.Write(ref current, gain);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyConstant(Span<float> buffer, float gain)
    {
        if (gain == 1f)
            return; // побитово точный проход
        if (gain == 0f)
            buffer.Clear(); // точные нули, в том числе поверх NaN
        else
            SampleOps.Scale(buffer, gain);
    }

    private static void ValidateGain(float gain)
    {
        if (!float.IsFinite(gain) || gain < 0f)
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Усиление должно быть конечным и неотрицательным.");
    }
}
