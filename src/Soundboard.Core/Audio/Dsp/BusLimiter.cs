using System.Runtime.CompilerServices;
using NAudio.Effects;
using NAudio.Wave;

namespace Soundboard.Core.Audio.Dsp;

/// <summary>
/// Защита конца шины (решение D4): <see cref="LimiterEffect"/> из NAudio (потолок −1 dBFS,
/// look-ahead 1.5 мс = 72 кадра при 48 кГц, release 80 мс, true-peak выключен) плюс
/// страховка от NaN/∞ и жёсткое ограничение ±1. Порядок обработки блока:
/// <list type="number">
/// <item>Блок с хотя бы одним NaN/∞ обнуляется целиком и учитывается в
/// <see cref="NonFiniteBlocks"/> — до лимитера, чтобы не отравить его состояние
/// (∞ дал бы нулевое усиление на весь look-ahead, NaN застрял бы в линии задержки).</item>
/// <item>Лимитер (сигнал задерживается на <see cref="LatencyFrames"/> кадров).</item>
/// <item>Повторная проверка конечности (страховка: при сбое — тишина, сброс лимитера,
/// <see cref="LimiterFaults"/>) и ограничение ±1 (<see cref="ClampedSamples"/>; при исправном
/// лимитере всегда 0).</item>
/// </list>
/// Ослабление лимитера публикуется раз в блок: <see cref="GainReductionDb"/> (последний блок)
/// и максимум с последнего чтения (<see cref="TakeMaxGainReductionDb"/>).
/// <see cref="Process"/> и <see cref="Reset"/> — только поток-владелец (аудиопоток шины);
/// свойства-счётчики читаются из любого потока. Без аллокаций после конструктора.
/// </summary>
internal sealed class BusLimiter
{
    /// <summary>Потолок по умолчанию, dBFS.</summary>
    public const float DefaultCeilingDb = -1f;

    /// <summary>Look-ahead по умолчанию, мс.</summary>
    public const float DefaultLookaheadMs = 1.5f;

    /// <summary>Release по умолчанию, мс.</summary>
    public const float DefaultReleaseMs = 80f;

    /// <summary>
    /// Ослабление меньше этого порога публикуется как 0: после отпускания коэффициент лимитера
    /// во float застревает на 1 − 2⁻²⁴ (≈5e-7 дБ), это не работа лимитера.
    /// </summary>
    public const float GainReductionEpsilonDb = 1e-3f;

    private readonly LimiterEffect limiter;
    private readonly int channels;

    private float gainReductionDb;
    private float maxGainReductionDb;
    private long blocks;
    private long nonFiniteBlocks;
    private long limiterFaults;
    private long clampedSamples;

    /// <summary>Создаёт и настраивает лимитер.</summary>
    /// <param name="ceilingDb">Потолок, dBFS, в диапазоне [−24, 0].</param>
    /// <param name="lookaheadMs">Look-ahead, мс, в диапазоне (0, 20].</param>
    /// <param name="releaseMs">Release, мс, в диапазоне [1, 500].</param>
    /// <param name="format">Формат блоков; по умолчанию <see cref="AudioFormat.WaveFormat"/>.</param>
    public BusLimiter(
        float ceilingDb = DefaultCeilingDb,
        float lookaheadMs = DefaultLookaheadMs,
        float releaseMs = DefaultReleaseMs,
        WaveFormat? format = null)
    {
        if (!(ceilingDb is >= -24f and <= 0f))
            throw new ArgumentOutOfRangeException(nameof(ceilingDb), ceilingDb, "Потолок должен быть в диапазоне [−24, 0] dBFS.");
        if (!(lookaheadMs is > 0f and <= 20f))
            throw new ArgumentOutOfRangeException(nameof(lookaheadMs), lookaheadMs, "Look-ahead должен быть в диапазоне (0, 20] мс.");
        if (!(releaseMs is >= 1f and <= 500f))
            throw new ArgumentOutOfRangeException(nameof(releaseMs), releaseMs, "Release должен быть в диапазоне [1, 500] мс.");

        format ??= AudioFormat.WaveFormat;
        if (format.Channels < 1 || format.SampleRate <= 0)
            throw new ArgumentException("Формат должен иметь хотя бы один канал и положительную частоту.", nameof(format));

        // Свойства задаются до Configure: OnConfigure по ним строит линию задержки и release.
        limiter = new LimiterEffect
        {
            CeilingDb = ceilingDb,
            LookaheadMs = lookaheadMs,
            ReleaseMs = releaseMs,
            TruePeak = false,
        };
        limiter.Configure(format);

        channels = format.Channels;
        CeilingDb = ceilingDb;
        CeilingLinear = MathF.Pow(10f, ceilingDb / 20f);
    }

    /// <summary>Потолок, dBFS.</summary>
    public float CeilingDb { get; }

    /// <summary>Потолок, линейная амплитуда (−1 dBFS ≈ 0.891).</summary>
    public float CeilingLinear { get; }

    /// <summary>Задержка, вносимая лимитером, в кадрах (72 при 48 кГц и 1.5 мс).</summary>
    public int LatencyFrames => limiter.LatencySamples;

    /// <summary>Число каналов в обрабатываемых буферах.</summary>
    public int Channels => channels;

    /// <summary>Ослабление лимитера на конец последнего блока, дБ (≥ 0; любой поток).</summary>
    public float GainReductionDb => Volatile.Read(ref gainReductionDb);

    /// <summary>Сколько блоков обработано (любой поток).</summary>
    public long Blocks => Interlocked.Read(ref blocks);

    /// <summary>Сколько блоков пришли с NaN/∞ и были обнулены (любой поток).</summary>
    public long NonFiniteBlocks => Interlocked.Read(ref nonFiniteBlocks);

    /// <summary>Сколько раз выход лимитера оказался нечисловым (не должно случаться; любой поток).</summary>
    public long LimiterFaults => Interlocked.Read(ref limiterFaults);

    /// <summary>Сколько сэмплов пришлось ограничить ±1 после лимитера (ожидается 0; любой поток).</summary>
    public long ClampedSamples => Interlocked.Read(ref clampedSamples);

    /// <summary>
    /// Обрабатывает блок чередующихся сэмплов на месте (аудиопоток). Длина кратна
    /// <see cref="Channels"/>. На выходе все сэмплы конечны и не превышают ±1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> buffer)
    {
        if (buffer.IsEmpty)
            return;
        SampleOps.EnsureWholeFrames(buffer.Length, channels, nameof(buffer));

        if (!SampleOps.IsAllFinite(buffer))
        {
            buffer.Clear();
            Interlocked.Increment(ref nonFiniteBlocks);
        }

        limiter.Process(buffer);

        if (!SampleOps.IsAllFinite(buffer))
        {
            buffer.Clear();
            limiter.Reset();
            Interlocked.Increment(ref limiterFaults);
        }
        else if (SampleOps.PeakAbs(buffer) > 1f)
        {
            Interlocked.Add(ref clampedSamples, SampleOps.ClampUnit(buffer));
        }

        var gr = limiter.GainReductionDb;
        if (!(gr >= GainReductionEpsilonDb))
            gr = 0f; // также отсекает NaN
        Volatile.Write(ref gainReductionDb, gr);
        AtomicFloat.RaiseMax(ref maxGainReductionDb, gr);
        Interlocked.Increment(ref blocks);
    }

    /// <summary>
    /// Максимальное ослабление (по концам блоков) с момента предыдущего вызова, дБ; сбрасывает
    /// накопление (любой поток, читатель один).
    /// </summary>
    public float TakeMaxGainReductionDb() => AtomicFloat.Take(ref maxGainReductionDb);

    /// <summary>
    /// Сбрасывает состояние лимитера (линию задержки, огибающую) и показание ослабления;
    /// счётчики сохраняются. Только когда <see cref="Process"/> не выполняется (например,
    /// после прогрева шины на управляющем потоке).
    /// </summary>
    public void Reset()
    {
        limiter.Reset();
        Volatile.Write(ref gainReductionDb, 0f);
        Volatile.Write(ref maxGainReductionDb, 0f);
    }
}
