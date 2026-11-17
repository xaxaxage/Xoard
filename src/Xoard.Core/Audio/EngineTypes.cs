using Xoard.Core.Audio.Devices;
using Xoard.Core.Audio.Output;
using Xoard.Core.Audio.Sounds;
using Xoard.Core.Audio.Status;

namespace Xoard.Core.Audio;

/// <summary>Идентификатор запуска звука.</summary>
public readonly record struct VoiceId(long Value)
{
    /// <inheritdoc />
    public override string ToString() => $"#{Value}";
}

/// <summary>Куда выводить звук.</summary>
[Flags]
public enum OutputMask
{
    /// <summary>Только наушники (предпрослушивание).</summary>
    Headphones = 1,

    /// <summary>Только VB-CABLE.</summary>
    Cable = 2,

    /// <summary>Наушники и VB-CABLE.</summary>
    Both = Headphones | Cable,
}

/// <summary>Регулятор громкости.</summary>
public enum LevelTarget
{
    /// <summary>Звуки в наушниках.</summary>
    Headphones,

    /// <summary>Звуки в VB-CABLE (то, что слышат друзья).</summary>
    Cable,

    /// <summary>Голос с микрофона в VB-CABLE.</summary>
    Mic,
}

/// <summary>Результат запуска.</summary>
public enum PlayStatus
{
    /// <summary>Звук запущен.</summary>
    Started,

    /// <summary>Звук не загружен (или вытеснен из кэша).</summary>
    NotLoaded,

    /// <summary>Нет ни одной работающей шины для вывода.</summary>
    NoOutput,

    /// <summary>Движок не запущен или остановлен.</summary>
    NotRunning,
}

/// <summary>Результат <see cref="IAudioEngine.Play"/>.</summary>
public readonly record struct PlayResult(PlayStatus Status, VoiceId Voice);

/// <summary>Почему звук закончился.</summary>
public enum PlaybackEndReason
{
    /// <summary>Доиграл до конца.</summary>
    Completed,

    /// <summary>Остановлен.</summary>
    Stopped,

    /// <summary>«Стоп всё».</summary>
    StopAll,

    /// <summary>Перезапущен (режим «перезапуск»).</summary>
    Restarted,

    /// <summary>Вытеснен: слишком много одновременных звуков.</summary>
    Stolen,

    /// <summary>Устройство вывода пропало.</summary>
    OutputLost,

    /// <summary>Ошибка в звуковом потоке.</summary>
    Error,
}

/// <summary>Параметры запуска.</summary>
/// <param name="Gain">Громкость этого запуска (линейная, 0..1).</param>
/// <param name="Outputs">Шины вывода.</param>
/// <param name="CableStartDelay">Задержка старта только в VB-CABLE (заготовка для push-to-talk).</param>
public sealed record PlayOptions(float Gain = 1f, OutputMask Outputs = OutputMask.Both, TimeSpan CableStartDelay = default);

/// <summary>Событие начала/окончания звука.</summary>
public sealed class PlaybackEventArgs(VoiceId voice, SoundHandle sound, PlaybackEndReason? reason, bool wasAudible) : EventArgs
{
    public VoiceId Voice { get; } = voice;

    public SoundHandle Sound { get; } = sound;

    /// <summary>Причина окончания (null для события начала).</summary>
    public PlaybackEndReason? Reason { get; } = reason;

    /// <summary>Звук успел прозвучать.</summary>
    public bool WasAudible { get; } = wasAudible;
}

/// <summary>Изменилась «активность» VB-CABLE (что-то играет в кабель) — основа будущего push-to-talk.</summary>
public sealed class ActivityChangedEventArgs(bool active) : EventArgs
{
    public bool Active { get; } = active;
}

/// <summary>Состояние шины вывода.</summary>
public enum BusState
{
    /// <summary>Нет устройства.</summary>
    Detached,

    /// <summary>Поток запускается.</summary>
    Starting,

    /// <summary>Работает.</summary>
    Running,

    /// <summary>Сбой, ждём повторной попытки.</summary>
    Recovering,
}

/// <summary>Общее состояние движка.</summary>
public enum EngineState
{
    /// <summary>Запуск.</summary>
    Starting,

    /// <summary>Всё работает.</summary>
    Running,

    /// <summary>Работает частично (нет VB-CABLE, наушников или микрофона).</summary>
    Degraded,

    /// <summary>Остановлен.</summary>
    Stopped,
}

/// <summary>Состояние шины для статуса.</summary>
public sealed record BusStatus(
    BusId Bus,
    BusState State,
    string? DeviceId,
    string? DeviceName,
    ChoiceReason? Reason,
    float Level,
    int BufferMs,
    long NearEmpty,
    long Restarts,
    long CallbackErrors,
    long AudioThreadAllocatedBytes,
    int AudioThreadPriority,
    float SessionVolume,
    bool SessionMuted,
    string? LastError);

/// <summary>Состояние передачи голоса.</summary>
public sealed record MicStatus(
    bool Enabled,
    bool Muted,
    bool Capturing,
    string? DeviceId,
    string? DeviceName,
    string? Format,
    float Level,
    double DriftPpm,
    bool Locked,
    double FillMs,
    int TargetMs,
    long Underruns,
    long Overflows,
    long Resyncs,
    long Discontinuities,
    long Restarts,
    string? LastError);

/// <summary>Что сейчас играет.</summary>
public sealed record PlayingVoiceInfo(VoiceId Id, string SoundKey, string Name, double PositionSec, double DurationSec, OutputMask Buses);

/// <summary>VB-CABLE в статусе.</summary>
public sealed record VbCableStatus(bool Installed, string? RenderName, string? CaptureName, IReadOnlyList<ListeningApp> ListeningApps);

/// <summary>Снимок состояния движка (неизменяемый; читать можно с любого потока).</summary>
public sealed record EngineStatus(
    DateTimeOffset Timestamp,
    EngineState State,
    VbCableStatus VbCable,
    BusStatus Headphones,
    BusStatus Cable,
    MicStatus Mic,
    PlaybackMode Mode,
    IReadOnlyList<PlayingVoiceInfo> Playing,
    IReadOnlyList<AudioEndpointInfo> Outputs,
    IReadOnlyList<AudioEndpointInfo> Inputs,
    IReadOnlyList<EngineIssue> Issues)
{
    internal static EngineStatus Initial { get; } = new(
        DateTimeOffset.MinValue,
        EngineState.Stopped,
        new VbCableStatus(false, null, null, []),
        EmptyBus(BusId.Headphones),
        EmptyBus(BusId.Cable),
        new MicStatus(false, false, false, null, null, null, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, null),
        PlaybackMode.Overlap,
        [],
        [],
        [],
        []);

    internal static BusStatus EmptyBus(BusId id) =>
        new(id, BusState.Detached, null, null, null, 0, 0, 0, 0, 0, 0, int.MinValue, 1f, false, null);
}

/// <summary>Пиковые уровни с прошлого чтения, dBFS.</summary>
public sealed record MeterSnapshot(
    float HeadphonesPeakDb,
    float CablePeakDb,
    float CableLimiterDb,
    float MicInPeakDb,
    float MicOutPeakDb);

/// <summary>Технические параметры движка.</summary>
public sealed record AudioEngineOptions
{
    public int HeadphonesBufferMs { get; init; } = 30;

    public int CableBufferMs { get; init; } = 20;

    public int MicTargetFillMs { get; init; } = 30;

    public int StopFadeMs { get; init; } = 10;

    public int RestartFadeMs { get; init; } = 5;

    public int LevelRampMs { get; init; } = 20;

    public int MaxVoices { get; init; } = 32;

    public long CacheBudgetBytes { get; init; } = SoundCache.DefaultBudgetBytes;

    /// <summary>Сколько держать «активность» кабеля после окончания звука.</summary>
    public TimeSpan ActivityHold { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Поднять приоритет процесса до «выше среднего» (ревью M2).</summary>
    public bool RaiseProcessPriority { get; init; } = true;

    /// <summary>Параметры для профиля задержки.</summary>
    public static AudioEngineOptions For(LatencyProfile profile) => profile switch
    {
        LatencyProfile.Safe => new() { HeadphonesBufferMs = 60, CableBufferMs = 50, MicTargetFillMs = 50 },
        LatencyProfile.Low => new() { HeadphonesBufferMs = 20, CableBufferMs = 15, MicTargetFillMs = 20 },
        _ => new(),
    };
}

/// <summary>
/// Звуковой движок Xoard. Все методы потокобезопасны и не блокируют надолго.
/// События приходят на отдельном потоке уведомлений: обработчики не должны блокировать.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    /// <summary>Запуск. Завершается и в «частичном» режиме (например, без VB-CABLE) — см. <see cref="Status"/>.</summary>
    Task StartAsync(AudioSettings settings, CancellationToken ct = default);

    /// <summary>Загрузить (декодировать) звук в память.</summary>
    Task<SoundHandle> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>Добавить звук из памяти (тестовые сигналы).</summary>
    SoundHandle AddSound(string name, DecodedSound sound);

    /// <summary>Запустить звук.</summary>
    PlayResult Play(SoundHandle sound, PlayOptions? options = null);

    /// <summary>Остановить один запуск.</summary>
    void StopVoice(VoiceId voice);

    /// <summary>Остановить все запуски звука.</summary>
    void StopSound(SoundHandle sound);

    /// <summary>Остановить всё (с коротким затуханием).</summary>
    void StopAll();

    /// <summary>Наложение или перезапуск.</summary>
    PlaybackMode PlaybackMode { get; set; }

    /// <summary>Громкость: положение ползунка 0..1.</summary>
    void SetLevel(LevelTarget target, float level);

    /// <summary>Мягко выключить/включить голос.</summary>
    void SetMicMuted(bool muted);

    /// <summary>Применить новые настройки (устройства, передача голоса, профиль).</summary>
    Task ApplySettingsAsync(AudioSettings newSettings);

    /// <summary>Перечитать устройства.</summary>
    Task RefreshDevicesAsync();

    /// <summary>Пересобрать потоки (проверка восстановления).</summary>
    Task RestartStreamsAsync();

    /// <summary>Текущие настройки.</summary>
    AudioSettings Settings { get; }

    /// <summary>Последний снимок состояния.</summary>
    EngineStatus Status { get; }

    /// <summary>Пиковые уровни с прошлого вызова.</summary>
    MeterSnapshot ReadMeters();

    event EventHandler<EngineStatus>? StatusChanged;

    event EventHandler<PlaybackEventArgs>? PlaybackStarted;

    event EventHandler<PlaybackEventArgs>? PlaybackEnded;

    event EventHandler<ActivityChangedEventArgs>? CableActivityChanged;
}
