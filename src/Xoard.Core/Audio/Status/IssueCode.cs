namespace Xoard.Core.Audio.Status;

/// <summary>
/// Код проблемы движка. Имена стабильны (попадут в get_status по именованному каналу и MCP),
/// поэтому переименовывать их нельзя; новые коды добавляются в конец своей группы.
/// Тексты для пользователя — в <see cref="IssueText"/>.
/// </summary>
public enum IssueCode
{
    // --- VB-CABLE и форматы устройств ---

    /// <summary>VB-CABLE не установлен (нет ни одной его конечной точки).</summary>
    VbCableNotInstalled,

    /// <summary>«CABLE Input» есть, но отключён или недоступен.</summary>
    VbCableRenderDisabled,

    /// <summary>«CABLE Output» отключён или недоступен: Discord ничего не получит.</summary>
    VbCableCaptureDisabled,

    /// <summary>Устройство работает не на 48 кГц.</summary>
    DeviceFormatNot48k,

    /// <summary>Частоты CABLE Input и CABLE Output различаются (ревью, minor 9).</summary>
    DeviceRatesDiffer,

    /// <summary>VB-CABLE — устройство вывода Windows по умолчанию (для любой роли).</summary>
    DefaultOutputIsVbCable,

    // --- Наушники ---

    /// <summary>Для наушников нет подходящего устройства.</summary>
    HeadphonesUnavailable,

    /// <summary>Выбранные пользователем наушники сейчас недоступны.</summary>
    HeadphonesPreferredMissing,

    /// <summary>Пользователь выбрал VB-CABLE как наушники — отклонено.</summary>
    HeadphonesRejectedVbCable,

    /// <summary>Устройство вывода похоже на петлевое/виртуальное (ревью M4).</summary>
    HeadphonesIsLoopbackDevice,

    // --- Микрофон ---

    /// <summary>Передача голоса включена, но микрофона нет.</summary>
    MicUnavailable,

    /// <summary>Выбранный пользователем микрофон сейчас недоступен.</summary>
    MicPreferredMissing,

    /// <summary>Пользователь выбрал VB-CABLE как микрофон — отклонено.</summary>
    MicRejectedVbCable,

    /// <summary>Микрофон Windows по умолчанию — VB-CABLE, автоматически не используется.</summary>
    MicDefaultIsVbCable,

    /// <summary>Микрофон похож на петлевое устройство (Stereo Mix, Steam Streaming…; ревью M4).</summary>
    MicIsLoopbackDevice,

    /// <summary>Windows запретила доступ к микрофону (настройки конфиденциальности).</summary>
    MicAccessDenied,

    // --- Потоки ---

    /// <summary>Не удалось запустить или восстановить поток вывода.</summary>
    OutputStreamFailed,

    /// <summary>Не удалось запустить или восстановить захват микрофона.</summary>
    CaptureStreamFailed,

    /// <summary>Устройство занято другой программой в монопольном режиме.</summary>
    DeviceInUse,

    /// <summary>Исключение в звуковом потоке: блок заменён тишиной.</summary>
    AudioCallbackError,

    /// <summary>Поток перестал обрабатываться без ошибки (сторожевой таймер, ревью M1).</summary>
    StreamStalled,

    /// <summary>Windows не выдала звуковому потоку приоритет MMCSS (ревью M2).</summary>
    MmcssNotGranted,

    /// <summary>Громкость нашей сессии в CABLE Input снижена или выключена (ревью C1).</summary>
    CableSessionAttenuated,

    /// <summary>Движок уже запущен другим экземпляром (ревью C2).</summary>
    EngineAlreadyRunning,

    // --- Звуки ---

    /// <summary>Файл не удалось декодировать.</summary>
    SoundDecodeFailed,

    /// <summary>Звук длиннее разрешённого предела.</summary>
    SoundTooLong,

    /// <summary>Кэш декодированных звуков превысил бюджет памяти.</summary>
    CacheBudgetExceeded,
}
