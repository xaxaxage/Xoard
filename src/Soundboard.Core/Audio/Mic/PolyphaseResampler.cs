using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Soundboard.Core.Audio.Mic;

/// <summary>
/// Ресэмплер с переменным коэффициентом для компенсации дрейфа часов микрофона (решение D3).
/// <para>
/// Оконный sinc: <see cref="Taps"/> отводов, <see cref="Phases"/> фаз с линейной интерполяцией между соседними
/// фазами, окно Кайзера β = 8, срез 0,45·fs, единичное усиление на DC для каждой фазы. Фильтр фиксирован,
/// потому что коэффициент всегда близок к 1 (±0,2 %); микрофон не на 48 кГц переводит в 48 кГц Windows.
/// </para>
/// <para>
/// Модель «вытягивания»: вход пишется через <see cref="Write"/> (интерливный, <see cref="Channels"/> каналов),
/// выход запрашивается через <see cref="Process"/> с коэффициентом <c>ratio</c> = входных кадров на один
/// выходной (ratio &gt; 1 — потребляем быстрее). Сколько входа дописать перед <see cref="Process"/>, говорит
/// <see cref="InputFramesNeeded"/>. История хранится раздельно по каналам; скалярные произведения на
/// <see cref="Vector{T}"/>. После конструктора аллокаций нет ни при каких размерах блоков.
/// </para>
/// <para>Не потокобезопасен: все методы вызываются из одного потока (поток рендера кабеля).</para>
/// </summary>
internal sealed class PolyphaseResampler
{
    /// <summary>Число отводов фильтра.</summary>
    public const int Taps = 32;

    /// <summary>Число фаз в таблице (между ними — линейная интерполяция).</summary>
    public const int Phases = 512;

    /// <summary>β окна Кайзера.</summary>
    public const double KaiserBeta = 8.0;

    /// <summary>Частота среза как доля частоты дискретизации.</summary>
    public const double CutoffFraction = 0.45;

    /// <summary>Минимально допустимый коэффициент (фильтр рассчитан на работу около 1).</summary>
    public const double MinRatio = 0.95;

    /// <summary>Максимально допустимый коэффициент.</summary>
    public const double MaxRatio = 1.05;

    /// <summary>Размер выходного блока по умолчанию (кадров) — с запасом над любым буфером WASAPI.</summary>
    public const int DefaultMaxOutputFrames = 8192;

    /// <summary>Сколько входных кадров «вперёд» нужно фильтру для выходного кадра (половина окна + 1).</summary>
    public const int LookAheadFrames = Half + 1;

    private const int Half = Taps / 2;

    /// <summary>
    /// Нулевая предыстория: столько кадров истории лежит перед позицией первого выходного кадра
    /// после <see cref="Reset"/>; входной кадр 0 хранится по индексу <see cref="Prefill"/>.
    /// </summary>
    private const int Prefill = Half - 1;

    /// <summary>Коэффициенты: (Phases + 1) строк по Taps.</summary>
    private static readonly float[] Coefficients = BuildCoefficients();

    /// <summary>Разности соседних строк: Phases строк по Taps (row[p + 1] − row[p]).</summary>
    private static readonly float[] Deltas = BuildDeltas(Coefficients);

    private readonly int channels;
    private readonly int maxOutputFrames;
    private readonly int capacity;
    private readonly float[][] history;
    private readonly float[] coefficientScratch = new float[Taps];

    /// <summary>Кадров в истории (включая нулевую предысторию).</summary>
    private int count;

    /// <summary>Позиция следующего выходного кадра в координатах истории (дробная).</summary>
    private double position;

    /// <summary>Кадров, сдвинутых из начала истории с момента сброса.</summary>
    private long discarded;

    /// <summary>Входных кадров записано с момента сброса (включая тишину).</summary>
    private long totalWritten;

    /// <summary>Создаёт ресэмплер.</summary>
    /// <param name="channels">Число интерливных каналов (1–8).</param>
    /// <param name="maxOutputFrames">Максимальный выходной блок одного <see cref="Process"/>, кадров.</param>
    public PolyphaseResampler(int channels = AudioFormat.Channels, int maxOutputFrames = DefaultMaxOutputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOutputFrames, 1 << 20);

        this.channels = channels;
        this.maxOutputFrames = maxOutputFrames;

        // Худший случай после Process: позиция ≈ Prefill + 1, затем запрос maxOutputFrames при MaxRatio
        // требует floor(pos + (N − 1)·MaxRatio) + LookAhead + 1 кадров; плюс запас на «лишний» вход.
        capacity = (int)Math.Ceiling(maxOutputFrames * MaxRatio) + Taps + LookAheadFrames + 8;
        history = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            history[c] = new float[capacity];
        }

        Reset();
    }

    /// <summary>Число каналов.</summary>
    public int Channels => channels;

    /// <summary>Максимальный выходной блок одного вызова <see cref="Process"/>, кадров.</summary>
    public int MaxOutputFrames => maxOutputFrames;

    /// <summary>Сколько ещё входных кадров помещается в историю прямо сейчас.</summary>
    public int WritableFrames => capacity - count;

    /// <summary>Всего входных кадров записано с последнего сброса (включая тишину).</summary>
    public long TotalInputFrames => totalWritten;

    /// <summary>
    /// Абсолютная позиция (во входных кадрах с последнего сброса) следующего выходного кадра.
    /// Растёт ровно на Σ N·ratio.
    /// </summary>
    public double ConsumedInputFrames => discarded + position - Prefill;

    /// <summary>
    /// Входные кадры, записанные, но ещё «не проигранные» (включая дробную фазу и упреждение фильтра):
    /// <see cref="TotalInputFrames"/> − <see cref="ConsumedInputFrames"/>. Для оценки заполнения:
    /// эффективная позиция чтения = кольцо.ReadCount − PendingInputFrames.
    /// </summary>
    public double PendingInputFrames => count - position;

    /// <summary>Сбрасывает историю в нули (как при создании) и обнуляет счётчики позиции.</summary>
    public void Reset()
    {
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(history[c], 0, Prefill);
        }

        count = Prefill;
        position = Prefill;
        discarded = 0;
        totalWritten = 0;
    }

    /// <summary>
    /// Сколько входных кадров нужно дописать, чтобы <see cref="Process"/> выдал <paramref name="outputFrames"/>
    /// кадров с коэффициентом <paramref name="ratio"/>. 0 — входа уже достаточно.
    /// </summary>
    public int InputFramesNeeded(int outputFrames, double ratio)
    {
        if (outputFrames <= 0)
        {
            return 0;
        }

        ValidateRatio(ratio);
        long last = (long)(position + ((outputFrames - 1) * ratio));
        long need = last + LookAheadFrames - count;
        return need > 0 ? (int)need : 0;
    }

    /// <summary>Дописывает интерливный вход (длина кратна <see cref="Channels"/>).</summary>
    /// <exception cref="InvalidOperationException">Вход не помещается в историю (нарушен контракт вызывающего).</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length % channels != 0)
        {
            throw new ArgumentException("Длина входа должна быть кратна числу каналов.", nameof(interleaved));
        }

        int frames = interleaved.Length / channels;
        if (frames == 0)
        {
            return;
        }

        EnsureWritable(frames);
        if (channels == 2)
        {
            var left = history[0].AsSpan(count, frames);
            var right = history[1].AsSpan(count, frames);
            for (int f = 0; f < frames; f++)
            {
                left[f] = interleaved[2 * f];
                right[f] = interleaved[(2 * f) + 1];
            }
        }
        else if (channels == 1)
        {
            interleaved.CopyTo(history[0].AsSpan(count, frames));
        }
        else
        {
            for (int c = 0; c < channels; c++)
            {
                var dst = history[c].AsSpan(count, frames);
                for (int f = 0, s = c; f < frames; f++, s += channels)
                {
                    dst[f] = interleaved[s];
                }
            }
        }

        count += frames;
        totalWritten += frames;
    }

    /// <summary>Дописывает <paramref name="frames"/> кадров тишины (для недобора при недогрузке кольца).</summary>
    public void WriteSilence(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        EnsureWritable(frames);
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(history[c], count, frames);
        }

        count += frames;
        totalWritten += frames;
    }

    /// <summary>
    /// Выдаёт <c>output.Length / Channels</c> кадров с коэффициентом <paramref name="ratio"/>.
    /// Если входа не хватает, недостающее дополняется тишиной (никогда не бросает на аудиопотоке из-за
    /// нехватки данных).
    /// </summary>
    /// <returns>Сколько кадров тишины пришлось дописать (0 в нормальной работе).</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Process(Span<float> output, double ratio)
    {
        if (output.Length % channels != 0)
        {
            throw new ArgumentException("Длина выхода должна быть кратна числу каналов.", nameof(output));
        }

        int frames = output.Length / channels;
        if (frames == 0)
        {
            return 0;
        }

        if (frames > maxOutputFrames)
        {
            throw new ArgumentException($"Выходной блок {frames} кадров больше максимума {maxOutputFrames}.", nameof(output));
        }

        int missing = InputFramesNeeded(frames, ratio);
        if (missing > 0)
        {
            WriteSilence(missing);
        }

        switch (channels)
        {
            case 2:
                ProcessStereo(output, frames, ratio);
                break;
            case 1:
                ProcessMono(output, frames, ratio);
                break;
            default:
                ProcessGeneric(output, frames, ratio);
                break;
        }

        position += frames * ratio;
        Compact();
        return missing;
    }

    private static void ValidateRatio(double ratio)
    {
        if (!(ratio >= MinRatio && ratio <= MaxRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, $"Коэффициент ресэмплинга вне [{MinRatio}; {MaxRatio}].");
        }
    }

    private void EnsureWritable(int frames)
    {
        if (frames > capacity - count)
        {
            throw new InvalidOperationException(
                $"Вход ресэмплера переполнен: {frames} кадров при свободных {capacity - count}. Пишите не больше InputFramesNeeded.");
        }
    }

    /// <summary>Сдвигает историю так, чтобы перед позицией осталась ровно нулевая предыстория.</summary>
    private void Compact()
    {
        int drop = (int)position - Prefill;
        if (drop <= 0)
        {
            return;
        }

        Debug.Assert(drop <= count, "Сдвиг истории не может превышать её длину.");
        int keep = count - drop;
        for (int c = 0; c < channels; c++)
        {
            Array.Copy(history[c], drop, history[c], 0, keep);
        }

        count = keep;
        position -= drop;
        discarded += drop;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessStereo(Span<float> output, int frames, double ratio)
    {
        int vec = Vector<float>.Count;
        ref float table = ref MemoryMarshal.GetArrayDataReference(Coefficients);
        ref float delta = ref MemoryMarshal.GetArrayDataReference(Deltas);
        ref float left = ref MemoryMarshal.GetArrayDataReference(history[0]);
        ref float right = ref MemoryMarshal.GetArrayDataReference(history[1]);
        double pos = position;

        for (int n = 0; n < frames; n++)
        {
            double t = pos + (n * ratio);
            int i = (int)t;
            double phase = (t - i) * Phases;
            int p = (int)phase;
            var a = new Vector<float>((float)(phase - p));
            nuint row = (nuint)(p * Taps);
            nuint start = (nuint)(i - Prefill);
            Debug.Assert(i - Prefill >= 0 && i + Half < count, "Окно фильтра вне истории.");

            var accL = Vector<float>.Zero;
            var accR = Vector<float>.Zero;
            for (nuint k = 0; k < Taps; k += (nuint)vec)
            {
                var c = Vector.LoadUnsafe(ref table, row + k) + (a * Vector.LoadUnsafe(ref delta, row + k));
                accL += Vector.LoadUnsafe(ref left, start + k) * c;
                accR += Vector.LoadUnsafe(ref right, start + k) * c;
            }

            output[2 * n] = Vector.Sum(accL);
            output[(2 * n) + 1] = Vector.Sum(accR);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessMono(Span<float> output, int frames, double ratio)
    {
        int vec = Vector<float>.Count;
        ref float table = ref MemoryMarshal.GetArrayDataReference(Coefficients);
        ref float delta = ref MemoryMarshal.GetArrayDataReference(Deltas);
        ref float mono = ref MemoryMarshal.GetArrayDataReference(history[0]);
        double pos = position;

        for (int n = 0; n < frames; n++)
        {
            double t = pos + (n * ratio);
            int i = (int)t;
            double phase = (t - i) * Phases;
            int p = (int)phase;
            var a = new Vector<float>((float)(phase - p));
            nuint row = (nuint)(p * Taps);
            nuint start = (nuint)(i - Prefill);
            Debug.Assert(i - Prefill >= 0 && i + Half < count, "Окно фильтра вне истории.");

            var acc = Vector<float>.Zero;
            for (nuint k = 0; k < Taps; k += (nuint)vec)
            {
                var c = Vector.LoadUnsafe(ref table, row + k) + (a * Vector.LoadUnsafe(ref delta, row + k));
                acc += Vector.LoadUnsafe(ref mono, start + k) * c;
            }

            output[n] = Vector.Sum(acc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessGeneric(Span<float> output, int frames, double ratio)
    {
        int vec = Vector<float>.Count;
        ref float table = ref MemoryMarshal.GetArrayDataReference(Coefficients);
        ref float delta = ref MemoryMarshal.GetArrayDataReference(Deltas);
        ref float coef = ref MemoryMarshal.GetArrayDataReference(coefficientScratch);
        double pos = position;

        for (int n = 0; n < frames; n++)
        {
            double t = pos + (n * ratio);
            int i = (int)t;
            double phase = (t - i) * Phases;
            int p = (int)phase;
            var a = new Vector<float>((float)(phase - p));
            nuint row = (nuint)(p * Taps);
            nuint start = (nuint)(i - Prefill);
            Debug.Assert(i - Prefill >= 0 && i + Half < count, "Окно фильтра вне истории.");

            for (nuint k = 0; k < Taps; k += (nuint)vec)
            {
                var c = Vector.LoadUnsafe(ref table, row + k) + (a * Vector.LoadUnsafe(ref delta, row + k));
                c.StoreUnsafe(ref coef, k);
            }

            for (int ch = 0; ch < channels; ch++)
            {
                ref float h = ref MemoryMarshal.GetArrayDataReference(history[ch]);
                var acc = Vector<float>.Zero;
                for (nuint k = 0; k < Taps; k += (nuint)vec)
                {
                    acc += Vector.LoadUnsafe(ref h, start + k) * Vector.LoadUnsafe(ref coef, k);
                }

                output[(n * channels) + ch] = Vector.Sum(acc);
            }
        }
    }

    /// <summary>
    /// Таблица фаз: строка p соответствует дробной задержке μ = p / Phases; отвод k стоит на расстоянии
    /// d = k − Prefill − μ входных кадров от выходной позиции. Строка Phases (μ = 1) равна строке 0,
    /// сдвинутой на один отвод, — интерполяция непрерывна при переходе через целую позицию.
    /// </summary>
    private static float[] BuildCoefficients()
    {
        const double fc = 2 * CutoffFraction; // доля от Найквиста
        double i0Beta = BesselI0(KaiserBeta);
        var table = new float[(Phases + 1) * Taps];
        var row = new double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double mu = p / (double)Phases;
            double sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double d = k - Prefill - mu;
                double u = d / Half;
                double window = Math.Abs(u) >= 1 ? 0 : BesselI0(KaiserBeta * Math.Sqrt(1 - (u * u))) / i0Beta;
                double x = Math.PI * fc * d;
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;
                row[k] = fc * sinc * window;
                sum += row[k];
            }

            for (int k = 0; k < Taps; k++)
            {
                table[(p * Taps) + k] = (float)(row[k] / sum); // единичное усиление на DC для каждой фазы
            }
        }

        return table;
    }

    private static float[] BuildDeltas(float[] coefficients)
    {
        var deltas = new float[Phases * Taps];
        for (int i = 0; i < deltas.Length; i++)
        {
            deltas[i] = coefficients[i + Taps] - coefficients[i];
        }

        return deltas;
    }

    /// <summary>Модифицированная функция Бесселя первого рода нулевого порядка (ряд).</summary>
    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 64; k++)
        {
            term *= (half / k) * (half / k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
