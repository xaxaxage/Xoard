using Soundboard.Core.Audio.Devices;

namespace Soundboard.Core.Audio.Status;

/// <summary>
/// Тексты проблем для пользователя (по-русски) — общие для Poc, приложения и MCP, чтобы везде
/// была одна формулировка. Здесь же серьёзность по умолчанию и ссылки на помощь.
/// </summary>
public static class IssueText
{
    private const string SoundPanel = "Панель управления → Звук (mmsys.cpl)";

    /// <summary>Создаёт проблему с текстом, серьёзностью и ссылкой по умолчанию для кода.</summary>
    /// <param name="code">Код проблемы.</param>
    /// <param name="subject">О чём речь: имя устройства или звука (подставляется в «кавычках»).</param>
    /// <param name="detail">Уточнение — законченное предложение, добавляется в конец текста.</param>
    /// <param name="deviceId">Id конечной точки, если проблема о ней.</param>
    /// <param name="hResult">HRESULT ошибки Windows, если есть.</param>
    /// <param name="severity">Другая серьёзность вместо <see cref="DefaultSeverity"/>.</param>
    public static EngineIssue Create(
        IssueCode code,
        string? subject = null,
        string? detail = null,
        string? deviceId = null,
        int? hResult = null,
        IssueSeverity? severity = null) =>
        new(code, severity ?? DefaultSeverity(code), Message(code, subject, detail), deviceId, HelpUrl(code), hResult);

    /// <summary>Полный текст проблемы.</summary>
    /// <param name="code">Код проблемы.</param>
    /// <param name="subject">Имя устройства или звука; <c>null</c> — общая формулировка.</param>
    /// <param name="detail">Уточнение (законченное предложение) в конец текста.</param>
    public static string Message(IssueCode code, string? subject = null, string? detail = null)
    {
        var text = BaseMessage(code, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim());
        return string.IsNullOrWhiteSpace(detail) ? text : text + " " + detail.Trim();
    }

    /// <summary>Короткий заголовок для списков и уведомлений.</summary>
    public static string Title(IssueCode code) => code switch
    {
        IssueCode.VbCableNotInstalled => "VB-CABLE не установлен",
        IssueCode.VbCableRenderDisabled => "CABLE Input отключён",
        IssueCode.VbCableCaptureDisabled => "CABLE Output отключён",
        IssueCode.DeviceFormatNot48k => "Частота устройства не 48 кГц",
        IssueCode.DeviceRatesDiffer => "Частоты VB-CABLE различаются",
        IssueCode.DefaultOutputIsVbCable => "VB-CABLE — вывод по умолчанию",
        IssueCode.HeadphonesUnavailable => "Нет наушников",
        IssueCode.HeadphonesPreferredMissing => "Выбранные наушники недоступны",
        IssueCode.HeadphonesRejectedVbCable => "VB-CABLE нельзя выбрать как наушники",
        IssueCode.HeadphonesIsLoopbackDevice => "Наушники — виртуальное устройство",
        IssueCode.MicUnavailable => "Нет микрофона",
        IssueCode.MicPreferredMissing => "Выбранный микрофон недоступен",
        IssueCode.MicRejectedVbCable => "VB-CABLE нельзя выбрать как микрофон",
        IssueCode.MicDefaultIsVbCable => "Микрофон по умолчанию — VB-CABLE",
        IssueCode.MicIsLoopbackDevice => "Микрофон — петлевое устройство",
        IssueCode.MicAccessDenied => "Нет доступа к микрофону",
        IssueCode.OutputStreamFailed => "Ошибка вывода звука",
        IssueCode.CaptureStreamFailed => "Ошибка захвата микрофона",
        IssueCode.DeviceInUse => "Устройство занято",
        IssueCode.AudioCallbackError => "Ошибка обработки звука",
        IssueCode.StreamStalled => "Звуковой поток завис",
        IssueCode.MmcssNotGranted => "Нет приоритета для звука",
        IssueCode.CableSessionAttenuated => "Громкость в VB-CABLE снижена",
        IssueCode.EngineAlreadyRunning => "Движок уже запущен",
        IssueCode.SoundDecodeFailed => "Звук не читается",
        IssueCode.SoundTooLong => "Звук слишком длинный",
        IssueCode.CacheBudgetExceeded => "Мало памяти для звуков",
        _ => code.ToString(),
    };

    /// <summary>
    /// Серьёзность по умолчанию. Error — не работает основное (друзья не слышат звуки или голос),
    /// Warning — работа ухудшена или есть риск, Info — к сведению.
    /// </summary>
    public static IssueSeverity DefaultSeverity(IssueCode code) => code switch
    {
        IssueCode.VbCableNotInstalled
            or IssueCode.VbCableRenderDisabled
            or IssueCode.VbCableCaptureDisabled
            or IssueCode.MicUnavailable
            or IssueCode.MicAccessDenied
            or IssueCode.OutputStreamFailed
            or IssueCode.CaptureStreamFailed
            or IssueCode.DeviceInUse
            or IssueCode.EngineAlreadyRunning => IssueSeverity.Error,
        _ => IssueSeverity.Warning,
    };

    /// <summary>Ссылка с помощью для кода или <c>null</c>.</summary>
    public static string? HelpUrl(IssueCode code) => code switch
    {
        IssueCode.VbCableNotInstalled
            or IssueCode.VbCableRenderDisabled
            or IssueCode.VbCableCaptureDisabled => VbCable.DownloadUrl,
        _ => null,
    };

    private static string BaseMessage(IssueCode code, string? s) => code switch
    {
        IssueCode.VbCableNotInstalled =>
            "VB-CABLE не найден: звуки и ваш голос не попадут в Discord, пока звуки слышны только вам. "
            + $"Установите бесплатный драйвер VB-CABLE ({VbCable.DownloadUrl}) и перезагрузите компьютер.",

        IssueCode.VbCableRenderDisabled =>
            $"Устройство вывода {Q(s, "CABLE Input")} (VB-CABLE) отключено или недоступно, поэтому звуки и голос не попадут в Discord. "
            + $"Включите его: {SoundPanel} → «Воспроизведение» → правый щелчок → «Включить» "
            + "(если его не видно, отметьте «Показать отключённые устройства»). "
            + $"Если не помогает, переустановите VB-CABLE: {VbCable.DownloadUrl}",

        IssueCode.VbCableCaptureDisabled =>
            $"Устройство ввода {Q(s, "CABLE Output")} (VB-CABLE) отключено или недоступно: Discord не сможет выбрать его "
            + "микрофоном и не услышит ни звуков, ни вашего голоса. "
            + $"Включите его: {SoundPanel} → «Запись» → правый щелчок → «Включить». "
            + $"Если не помогает, переустановите VB-CABLE: {VbCable.DownloadUrl}",

        IssueCode.DeviceFormatNot48k =>
            $"Устройство {Q(s, "вывода")} работает не на частоте 48 000 Гц. Windows преобразует звук сама, "
            + $"но надёжнее выставить «48000 Гц»: {SoundPanel} → устройство → «Свойства» → «Дополнительно».",

        IssueCode.DeviceRatesDiffer =>
            "У «CABLE Input» и «CABLE Output» разные частоты дискретизации, из-за этого звук в Discord может искажаться. "
            + $"Выставьте обоим «48000 Гц»: {SoundPanel} → устройство → «Свойства» → «Дополнительно».",

        IssueCode.DefaultOutputIsVbCable =>
            "VB-CABLE выбран устройством вывода Windows по умолчанию: звук других программ (игр, браузера, самого Discord) "
            + "уйдёт друзьям в Discord, а вы его не услышите. Сделайте устройством по умолчанию наушники: "
            + $"Параметры → Система → Звук → «Вывод» (а в {SoundPanel} — ещё и «Устройство связи по умолчанию»).",

        IssueCode.HeadphonesUnavailable =>
            "Нет подходящего устройства для наушников: звуки услышат только друзья в Discord, а вы — нет. "
            + "Подключите наушники или выберите их в настройках.",

        IssueCode.HeadphonesPreferredMissing =>
            $"Выбранные наушники{QSpace(s)} сейчас недоступны (отключены или отсоединены). "
            + "Когда они появятся, звук вернётся на них автоматически.",

        IssueCode.HeadphonesRejectedVbCable =>
            $"{Q(s, "Выбранное устройство")} — это VB-CABLE, его нельзя выбрать наушниками: звук ушёл бы в Discord, "
            + "а не вам. Выберите настоящие наушники или колонки.",

        IssueCode.HeadphonesIsLoopbackDevice =>
            $"Устройство вывода {Q(s, "для наушников")} похоже на виртуальное или петлевое: его звук может снова попасть "
            + "в микрофон или в Discord, и друзья услышат звуки дважды или с эхом. Лучше выберите настоящие наушники.",

        IssueCode.MicUnavailable =>
            "Микрофон не выбран или недоступен: друзья в Discord не услышат ваш голос (звуки при этом работают). "
            + "Подключите микрофон или выберите его в настройках.",

        IssueCode.MicPreferredMissing =>
            $"Выбранный микрофон{QSpace(s)} сейчас недоступен (отключён или отсоединён). "
            + "Когда он появится, программа переключится на него автоматически.",

        IssueCode.MicRejectedVbCable =>
            $"{Q(s, "Выбранное устройство")} — это VB-CABLE, его нельзя выбрать микрофоном: получилась бы петля и эхо. "
            + "Выберите настоящий микрофон.",

        IssueCode.MicDefaultIsVbCable =>
            $"Микрофон Windows по умолчанию — VB-CABLE{QParen(s)}, поэтому программа не берёт его как ваш микрофон "
            + "(для Discord такой выбор нормален). Выберите настоящий микрофон в настройках программы.",

        IssueCode.MicIsLoopbackDevice =>
            $"Микрофон {QTrail(s)}похож на петлевое или виртуальное устройство: если он передаёт звук компьютера, "
            + "друзья в Discord услышат эхо и звуки дважды. Выберите настоящий микрофон.",

        IssueCode.MicAccessDenied =>
            "Windows запретила программам доступ к микрофону. Разрешите его: Параметры → Конфиденциальность и защита → "
            + "Микрофон → «Доступ к микрофону» и «Разрешить классическим приложениям доступ к микрофону».",

        IssueCode.OutputStreamFailed =>
            $"Не удалось запустить вывод звука в {Q(s, "устройство")}. Программа будет пробовать снова.",

        IssueCode.CaptureStreamFailed =>
            $"Не удалось запустить захват микрофона{QSpace(s)}. Программа будет пробовать снова.",

        IssueCode.DeviceInUse =>
            $"Устройство {QTrail(s)}занято другой программой в монопольном режиме. Откройте {SoundPanel}, выберите это устройство "
            + "→ «Свойства» → «Дополнительно» и снимите галочку «Разрешить приложениям использовать устройство "
            + "в монопольном режиме» (для VB-CABLE — у «CABLE Input» и «CABLE Output»).",

        IssueCode.AudioCallbackError =>
            $"Ошибка при обработке звука{QParen(s)}: блок заменён тишиной, вывод продолжается.",

        IssueCode.StreamStalled =>
            $"Звуковой поток{QSpace(s)} перестал обрабатываться (устройство зависло или вышло из сна) и перезапущен.",

        IssueCode.MmcssNotGranted =>
            $"Windows не повысила приоритет звукового потока{QParen(s)} (служба MMCSS): под нагрузкой возможны щелчки и прерывания.",

        IssueCode.CableSessionAttenuated =>
            "Громкость программы в VB-CABLE снижена (микшер громкости Windows или приглушение во время звонка): "
            + "друзья слышат звуки и ваш голос тише. Верните 100 % в микшере громкости Windows и отключите "
            + "«Приглушение» в Discord (Настройки → Голос и видео).",

        IssueCode.EngineAlreadyRunning =>
            "Звуковой движок уже запущен в другом окне или экземпляре Soundboard. Закройте другой экземпляр: "
            + "два одновременно удвоят ваш голос в Discord.",

        IssueCode.SoundDecodeFailed =>
            $"Не удалось прочитать звук{QSpace(s)}: файл повреждён или его формат не поддерживается.",

        IssueCode.SoundTooLong =>
            $"Звук{QSpace(s)} длиннее допустимого предела и не загружен.",

        IssueCode.CacheBudgetExceeded =>
            "Загруженные звуки занимают больше памяти, чем разрешено: редко используемые звуки будут выгружаться "
            + "и загружаться заново перед воспроизведением.",

        _ => $"Проблема со звуком: {code}.",
    };

    /// <summary>«имя» или запасная формулировка.</summary>
    private static string Q(string? subject, string fallback) => subject is null ? fallback : $"«{subject}»";

    /// <summary>«имя» с пробелом после или пусто.</summary>
    private static string QTrail(string? subject) => subject is null ? "" : $"«{subject}» ";

    /// <summary>Пробел и «имя» или пусто.</summary>
    private static string QSpace(string? subject) => subject is null ? "" : $" «{subject}»";

    /// <summary>Пробел и («имя») или пусто.</summary>
    private static string QParen(string? subject) => subject is null ? "" : $" («{subject}»)";
}
