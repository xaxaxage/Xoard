namespace Soundboard.Core.Audio.Mic;

/// <summary>
/// Регулятор дрейфа часов микрофона и VB-CABLE (чистая логика, без потоков).
/// Вход — ошибка заполнения буфера (кадров сверх целевого), выход — коэффициент ресемплера
/// r = входных кадров на выходной (r &gt; 1 — потребляем быстрее). Это ПИ-регулятор второго порядка:
/// интегратор запоминает разницу частот (ppm), пропорциональная часть гасит отклонения.
/// </summary>
/// <remarks>
/// Динамика: de/dt = fs·(δ − (r − 1)), r − 1 = Kp·e + ∫Ki·e dt ⇒ s² + fs·Kp·s + fs·Ki = 0,
/// ωn = √(fs·Ki), ζ = fs·Kp / (2ωn). Первые 10 с после (пере)синхронизации — быстрая полоса
/// 0,1 Гц (захват за ~10 с), затем медленная 0,02 Гц (минимум «плавания» высоты тона).
/// Ошибка сглаживается (τ = 100 мс), чтобы джиттер прихода пакетов не дёргал коэффициент.
/// </remarks>
internal sealed class DriftController
{
    /// <summary>Максимальная поправка: ±2000 ppm (0,2 %).</summary>
    public const double MaxDeviation = 2000e-6;

    private const double Damping = 0.7;
    private const double FastBandwidthHz = 0.1;
    private const double SlowBandwidthHz = 0.02;
    private const double FastPhaseSeconds = 10;
    private const double ErrorSmoothingSeconds = 0.1;
    private const double LockThresholdFrames = 48;   // 1 мс
    private const double LockHoldSeconds = 5;

    private readonly int sampleRate;
    private double integral;          // поправка коэффициента от интегратора (доля, не ppm)
    private double smoothedError;
    private double sinceReset;
    private double lockedFor;

    public DriftController(int sampleRate = AudioFormat.SampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        this.sampleRate = sampleRate;
    }

    /// <summary>Последний выданный коэффициент.</summary>
    public double Ratio { get; private set; } = 1.0;

    /// <summary>Оценка разницы частот, ppm (положительная — микрофон «спешит»).</summary>
    public double EstimatedPpm => integral * 1e6;

    /// <summary>Сглаженная ошибка заполнения, кадров.</summary>
    public double SmoothedErrorFrames => smoothedError;

    /// <summary>Регулятор вошёл в захват: |ошибка| &lt; 1 мс не менее 5 с подряд.</summary>
    public bool Locked => lockedFor >= LockHoldSeconds;

    /// <summary>Сброс после (пере)синхронизации. <paramref name="warmStartPpm"/> — известная разница частот.</summary>
    public void Reset(double warmStartPpm = 0)
    {
        integral = Math.Clamp(warmStartPpm * 1e-6, -MaxDeviation, MaxDeviation);
        smoothedError = 0;
        sinceReset = 0;
        lockedFor = 0;
        Ratio = 1.0 + integral;
    }

    /// <summary>
    /// Шаг регулятора на один блок рендера.
    /// </summary>
    /// <param name="errorFrames">Заполнение минус целевое, кадров.</param>
    /// <param name="blockFrames">Размер блока рендера, кадров.</param>
    /// <returns>Коэффициент ресемплера.</returns>
    public double Update(double errorFrames, int blockFrames)
    {
        if (blockFrames <= 0 || double.IsNaN(errorFrames))
            return Ratio;

        var dt = blockFrames / (double)sampleRate;
        smoothedError += (errorFrames - smoothedError) * (dt / (dt + ErrorSmoothingSeconds));
        var e = smoothedError;

        var bandwidth = sinceReset < FastPhaseSeconds ? FastBandwidthHz : SlowBandwidthHz;
        var wn = 2 * Math.PI * bandwidth;
        var kp = 2 * Damping * wn / sampleRate;
        var ki = wn * wn / sampleRate;

        var proportional = kp * e;
        var candidate = integral + (ki * e * dt);
        var raw = proportional + candidate;
        var clamped = Math.Clamp(raw, -MaxDeviation, MaxDeviation);

        // Анти-насыщение: не копим интеграл, если выход упёрся в предел в ту же сторону
        if (raw == clamped || Math.Sign(raw - clamped) != Math.Sign(ki * e))
            integral = Math.Clamp(candidate, -MaxDeviation, MaxDeviation);

        sinceReset += dt;
        lockedFor = Math.Abs(e) < LockThresholdFrames ? lockedFor + dt : 0;
        Ratio = 1.0 + clamped;
        return Ratio;
    }
}
