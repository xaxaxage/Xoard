using System.Runtime.CompilerServices;

namespace Xoard.Core.Audio.Dsp;

/// <summary>
/// Lock-free ячейка «максимум с момента последнего чтения» для float: аудиопоток поднимает
/// значение через CAS (<see cref="RaiseMax"/>), читатель забирает его и обнуляет через
/// <see cref="Interlocked.Exchange(ref float, float)"/> (<see cref="Take"/>). Ни одно значение,
/// записанное до Take, не теряется: CAS писателя, проигравший обмену, повторяется уже от нуля.
/// </summary>
internal static class AtomicFloat
{
    /// <summary>Поднимает значение в ячейке до <paramref name="value"/>, если оно больше текущего.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RaiseMax(ref float location, float value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref location, value, current);
            if (BitConverter.SingleToInt32Bits(seen) == BitConverter.SingleToInt32Bits(current))
                return;
            current = seen;
        }
    }

    /// <summary>Возвращает накопленный максимум и сбрасывает ячейку в 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Take(ref float location) => Interlocked.Exchange(ref location, 0f);
}
