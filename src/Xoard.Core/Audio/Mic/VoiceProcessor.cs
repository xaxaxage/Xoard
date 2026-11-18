using System.Runtime.CompilerServices;

namespace Xoard.Core.Audio.Mic;

/// <summary>
/// Обработка голоса до смешивания со звуками (поток захвата): шумоподавление и выравнивание громкости
/// по речи — то, что Discord делает в своих профилях, но без вреда для звуков (их здесь нет).
/// Работает кадрами по 10 мс моно; голос задержан на <see cref="LatencyFrames"/> (кадр накопления
/// + кадр подавителя) и при выключенных переключателях — поэтому их смена не рвёт звук: очищенный и
/// исходный сигналы выровнены, переход между ними и изменение усиления идут плавно за кадр.
/// Без подавителя (нет rnnoise.dll) голос проходит как есть, без задержки.
/// <see cref="Configure"/> и чтение <see cref="GainDb"/> — с любого потока; остальное — поток захвата
/// (<see cref="Reset"/> — пока захват остановлен).
/// </summary>
internal sealed class VoiceProcessor
{
    /// <summary>Размер кадра обработки.</summary>
    public const int FrameSize = RnNoise.FrameSize;

    /// <summary>Задержка голоса при работающем подавителе, кадров (20 мс).</summary>
    public const int LatencyFrames = 2 * FrameSize;

    /// <summary>
    /// Громкость речи на 100 %, dBFS: средняя мощность кадров с речью. Примерно так звучит
    /// хорошо настроенный микрофон в Discord (пики около −6…−3 dBFS).
    /// </summary>
    public const float TargetDb = -18f;

    /// <summary>Наибольшее усиление тихого микрофона, дБ.</summary>
    public const float MaxBoostDb = 30f;

    /// <summary>Наибольшее ослабление слишком громкого микрофона, дБ.</summary>
    public const float MaxCutDb = -12f;

    /// <summary>Скорость изменения усиления, дБ за кадр (20 дБ/с).</summary>
    public const float SlewDbPerFrame = 0.2f;

    /// <summary>
    /// Сколько кадров речи усредняет оценка громкости: сначала — простое среднее (быстро находит
    /// уровень нового микрофона), потом — скользящее за ~1,5 с речи.
    /// </summary>
    public const int SettleFrames = 150;

    /// <summary>Кадр считается речью, если подавитель уверен в этом хотя бы на столько.</summary>
    public const float SpeechProbability = 0.6f;

    private const float Scale = 32768f;
    private const double SilencePower = 1e-7;   // −70 dBFS: тише — не речь, а остатки шума

    private readonly IVoiceDenoiser? denoiser;
    private readonly float[] input = new float[FrameSize];      // накапливаемый кадр (моно, шкала ±32768)
    private readonly float[] original = new float[FrameSize];   // вход прошлого кадра — выровнен с выходом подавителя
    private readonly float[] clean = new float[FrameSize];      // выход подавителя
    private readonly float[] output = new float[FrameSize];     // готовый кадр (±1), отдаётся по отсчёту
    private int fill;
    private float cleanMix = 1f;    // 0 — исходный голос, 1 — очищенный
    private float gainDb;
    private float publishedGainDb;
    private double levelPower;
    private int speechFrames;
    private string? deviceKey;
    private int denoiseOn = 1;
    private int autoLevelOn = 1;

    public VoiceProcessor(IVoiceDenoiser? denoiser) => this.denoiser = denoiser;

    /// <summary>Подавитель есть (иначе голос идёт без обработки).</summary>
    public bool Available => denoiser is not null;

    /// <summary>Текущее усиление выравнивания, дБ (0 — выключено или ещё не было речи).</summary>
    public float GainDb => Volatile.Read(ref publishedGainDb);

    /// <summary>Включает шумоподавление и выравнивание громкости. Можно звать с любого потока.</summary>
    public void Configure(bool noiseSuppression, bool autoLevel)
    {
        Volatile.Write(ref denoiseOn, noiseSuppression ? 1 : 0);
        Volatile.Write(ref autoLevelOn, autoLevel ? 1 : 0);
    }

    /// <summary>
    /// Новый поток с микрофона (захват остановлен): буферы и подавитель сбрасываются; оценка громкости
    /// сохраняется, если микрофон тот же (<paramref name="device"/> — его id).
    /// </summary>
    public void Reset(string? device)
    {
        fill = 0;
        Array.Clear(input);
        Array.Clear(original);
        Array.Clear(clean);
        Array.Clear(output);
        cleanMix = Volatile.Read(ref denoiseOn) != 0 ? 1f : 0f;
        denoiser?.Reset();
        if (!string.Equals(device, deviceKey, StringComparison.Ordinal))
        {
            deviceKey = device;
            levelPower = 0;
            speechFrames = 0;
            gainDb = 0;
            Volatile.Write(ref publishedGainDb, 0f);
        }
    }

    /// <summary>Обрабатывает блок (чередующиеся каналы <see cref="AudioFormat.Channels"/>) на месте.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> block)
    {
        if (denoiser is null)
            return;

        const int channels = AudioFormat.Channels;
        const float toScale = Scale / channels;
        for (var i = 0; i + channels <= block.Length; i += channels)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += block[i + c];

            var y = output[fill];
            for (var c = 0; c < channels; c++)
                block[i + c] = y;

            input[fill] = sum * toScale;
            if (++fill == FrameSize)
            {
                ProcessFrame();
                fill = 0;
            }
        }
    }

    private void ProcessFrame()
    {
        var speech = denoiser!.Process(input, clean);

        var mix0 = cleanMix;
        var mix1 = Volatile.Read(ref denoiseOn) != 0 ? 1f : 0f;
        var gain0 = gainDb;
        var gain1 = Volatile.Read(ref autoLevelOn) != 0 ? NextGain(speech) : Step(gainDb, 0f);
        var lin0 = Dsp.Decibels.ToLinear(gain0) / Scale;
        var lin1 = Dsp.Decibels.ToLinear(gain1) / Scale;

        // Плавно за кадр: переход между исходным и очищенным голосом и новое усиление
        const float step = 1f / FrameSize;
        for (var k = 0; k < FrameSize; k++)
        {
            var t = (k + 1) * step;
            var mix = mix0 + ((mix1 - mix0) * t);
            var source = original[k] + ((clean[k] - original[k]) * mix);
            output[k] = source * (lin0 + ((lin1 - lin0) * t));
        }

        cleanMix = mix1;
        gainDb = gain1;
        Volatile.Write(ref publishedGainDb, gain1);
        input.AsSpan().CopyTo(original);
    }

    /// <summary>Следующее усиление: громкость речи меряется по очищенному голосу, в паузах усиление держится.</summary>
    private float NextGain(float speech)
    {
        if (speech >= SpeechProbability)
        {
            var power = MeanSquare(clean) / (Scale * Scale);
            if (power > SilencePower)
            {
                speechFrames = Math.Min(speechFrames + 1, SettleFrames);
                levelPower += (power - levelPower) / speechFrames;
            }
        }

        if (speechFrames == 0)
            return gainDb;

        var levelDb = (float)(10 * Math.Log10(levelPower));
        return Step(gainDb, Math.Clamp(TargetDb - levelDb, MaxCutDb, MaxBoostDb));
    }

    private static float Step(float from, float to) => from + Math.Clamp(to - from, -SlewDbPerFrame, SlewDbPerFrame);

    private static double MeanSquare(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var x in frame)
            sum += x * x;
        return sum / frame.Length;
    }
}
