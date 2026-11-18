using System.Runtime.CompilerServices;

namespace Xoard.Core.Audio.Mic;

/// <summary>
/// Обработка голоса до смешивания со звуками (поток захвата): шумоподавление и выравнивание громкости
/// по речи — то, что Discord делает в своих профилях, но без вреда для звуков (их здесь нет).
/// Третья часть — «глушить паузы»: когда подавитель уверен, что речи нет (дыхание, клавиатура между
/// фразами), голос плавно уходит в тишину и открывается заранее, ещё до начала слова, — для этого
/// он задержан ещё на <see cref="LookaheadFrames"/> кадра (подсказка подавителя смотрит вперёд).
/// Работает кадрами по 10 мс моно; голос задержан на <see cref="LatencyFrames"/> (кадр накопления
/// + кадр подавителя + запас для паузы) и при выключенных переключателях — поэтому их смена не рвёт
/// звук: очищенный и исходный сигналы выровнены, переходы и изменение усиления идут плавно за кадр.
/// Без подавителя (нет rnnoise.dll) голос проходит как есть, без задержки.
/// <see cref="Configure"/> и чтение <see cref="GainDb"/> — с любого потока; остальное — поток захвата
/// (<see cref="Reset"/> — пока захват остановлен).
/// </summary>
internal sealed class VoiceProcessor
{
    /// <summary>Размер кадра обработки.</summary>
    public const int FrameSize = RnNoise.FrameSize;

    /// <summary>На сколько кадров обработки «глушение пауз» смотрит вперёд (30 мс), чтобы не съедать начало слов.</summary>
    public const int LookaheadFrames = 3;

    /// <summary>Задержка голоса при работающем подавителе, отсчётов (50 мс).</summary>
    public const int LatencyFrames = (2 + LookaheadFrames) * FrameSize;

    /// <summary>
    /// Пауза открывается, если подавитель уверен в речи хотя бы на столько в <see cref="GateFramesNeeded"/>
    /// кадрах окна. Замер RNNoise: речь держится выше этого десятками кадров подряд, а щелчок клавиатуры
    /// или вдох дают одиночный выброс в 1–2 кадра.
    /// </summary>
    public const float GateOpenProbability = 0.85f;

    /// <summary>Пока голос открыт, он держится, если уверенность в речи не ниже этого (тоже в нескольких кадрах).</summary>
    public const float GateKeepProbability = 0.6f;

    /// <summary>Сколько кадров окна (уходящий и следующие за ним) должны быть речью, чтобы голос открылся или держался.</summary>
    public const int GateFramesNeeded = 2;

    /// <summary>Сколько кадров голос остаётся открытым после последней речи (200 мс — концы слов не режутся).</summary>
    public const int GateHoldFrames = 20;

    /// <summary>За сколько кадров голос уходит в тишину после удержания (80 мс).</summary>
    public const int GateReleaseFrames = 8;

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

    /// <summary>Громкость меряется по кадрам, где подавитель уверен в речи хотя бы на столько (два кадра подряд).</summary>
    public const float SpeechProbability = 0.85f;

    private const float Scale = 32768f;
    private const double SilencePower = 1e-7;   // −70 dBFS: тише — не речь, а остатки шума

    private readonly IVoiceDenoiser? denoiser;
    private readonly float[] input = new float[FrameSize];      // накапливаемый кадр (моно, шкала ±32768)
    private readonly float[] original = new float[FrameSize];   // вход прошлого кадра — выровнен с выходом подавителя
    private readonly float[] clean = new float[FrameSize];      // выход подавителя
    private readonly float[] output = new float[FrameSize];     // готовый кадр (±1), отдаётся по отсчёту
    private readonly float[][] pending = [.. Enumerable.Range(0, LookaheadFrames + 1).Select(_ => new float[FrameSize])];
    private readonly float[] speechHistory = new float[LookaheadFrames + 2];   // уверенность в речи: кадр выхода и всё, что после него
    private long frameIndex;
    private float gate = 1f;        // 1 — голос открыт, 0 — пауза заглушена
    private int holdLeft;
    private int gateOn = 1;
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

    /// <summary>Включает шумоподавление, выравнивание громкости и глушение пауз. Можно звать с любого потока.</summary>
    public void Configure(bool noiseSuppression, bool autoLevel, bool gatePauses)
    {
        Volatile.Write(ref denoiseOn, noiseSuppression ? 1 : 0);
        Volatile.Write(ref autoLevelOn, autoLevel ? 1 : 0);
        Volatile.Write(ref gateOn, gatePauses ? 1 : 0);
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
        foreach (var frame in pending)
            Array.Clear(frame);
        Array.Clear(speechHistory);
        frameIndex = 0;
        holdLeft = 0;
        gate = Volatile.Read(ref gateOn) != 0 ? 0f : 1f;
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
        var processed = pending[(int)(frameIndex % pending.Length)];
        for (var k = 0; k < FrameSize; k++)
        {
            var t = (k + 1) * step;
            var mix = mix0 + ((mix1 - mix0) * t);
            var source = original[k] + ((clean[k] - original[k]) * mix);
            processed[k] = source * (lin0 + ((lin1 - lin0) * t));
        }

        cleanMix = mix1;
        gainDb = gain1;
        Volatile.Write(ref publishedGainDb, gain1);
        input.AsSpan().CopyTo(original);

        // Уходит кадр, обработанный LookaheadFrames кадров назад: о нём и о речи после него уже всё известно
        Array.Copy(speechHistory, 1, speechHistory, 0, speechHistory.Length - 1);
        speechHistory[^1] = speech;
        var leaving = pending[(int)((frameIndex + 1) % pending.Length)];
        var gate0 = gate;
        var gate1 = NextGate();
        for (var k = 0; k < FrameSize; k++)
            output[k] = leaving[k] * (gate0 + ((gate1 - gate0) * (k + 1) * step));
        gate = gate1;
        frameIndex++;
    }

    /// <summary>
    /// Открыт ли голос: речь в уходящем кадре или в ближайших после него (тогда голос открывается
    /// заранее), удержание после речи, затем плавный уход в тишину.
    /// </summary>
    private float NextGate()
    {
        if (Volatile.Read(ref gateOn) == 0)
        {
            holdLeft = 0;
            return 1f;
        }

        int strong = 0, weak = 0;
        foreach (var p in speechHistory)
        {
            if (p >= GateOpenProbability)
                strong++;
            if (p >= GateKeepProbability)
                weak++;
        }

        if (strong >= GateFramesNeeded || (gate > 0 && weak >= GateFramesNeeded))
            holdLeft = GateHoldFrames;
        if (holdLeft > 0)
        {
            holdLeft--;
            return 1f;
        }

        return Math.Max(0f, gate - (1f / GateReleaseFrames));
    }

    /// <summary>Следующее усиление: громкость речи меряется по очищенному голосу, в паузах усиление держится.</summary>
    private float NextGain(float speech)
    {
        // Учимся только на устойчивой речи: этот кадр и прошлый (его звук сейчас в clean) — речь наверняка.
        // Одиночные выбросы (щелчок, вдох) иначе подтягивали бы усиление вверх.
        if (speech >= SpeechProbability && speechHistory[^1] >= SpeechProbability)
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
