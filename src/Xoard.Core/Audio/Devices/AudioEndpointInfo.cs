namespace Xoard.Core.Audio.Devices;

/// <summary>Направление конечной точки.</summary>
public enum EndpointFlow
{
    /// <summary>Устройство вывода (наушники, колонки, CABLE Input).</summary>
    Render,

    /// <summary>Устройство ввода (микрофон, CABLE Output).</summary>
    Capture,
}

/// <summary>Состояние конечной точки в Windows (то, что попадает в снимок).</summary>
public enum EndpointState
{
    /// <summary>Включена и доступна.</summary>
    Active,

    /// <summary>Отключена пользователем в панели звука.</summary>
    Disabled,
}

/// <summary>
/// Неизменяемое описание конечной точки WASAPI — то, что нужно маршрутизации и статусу.
/// Флаги VB-CABLE и «петлевого» устройства вычисляются из имён, поэтому не могут разойтись
/// с ними (в том числе после <c>with</c>).
/// </summary>
/// <param name="Id">Id конечной точки Windows (непрозрачная строка; сравнивать без учёта регистра).</param>
/// <param name="FriendlyName">Имя конечной точки, например «Headphones (Arctis Nova 7)».</param>
/// <param name="AdapterName">Имя адаптера (PKEY_DeviceInterface_FriendlyName), например «VB-Audio Virtual Cable»;
/// пусто, если неизвестно.</param>
/// <param name="Flow">Вывод или ввод.</param>
/// <param name="Channels">Число каналов формата устройства; 0 — неизвестно.</param>
/// <param name="SampleRate">Частота формата устройства, Гц; 0 — неизвестна.</param>
/// <param name="IsFloat">Формат устройства — IEEE float (информационно; общий режим всё равно микширует во float).</param>
/// <param name="BitsPerSample">Разрядность формата устройства; 0 — неизвестна.</param>
/// <param name="State">Включена или отключена.</param>
public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string AdapterName,
    EndpointFlow Flow,
    int Channels = 0,
    int SampleRate = 0,
    bool IsFloat = false,
    int BitsPerSample = 0,
    EndpointState State = EndpointState.Active)
{
    /// <summary>Это конечная точка VB-CABLE (CABLE Input, CABLE In 16ch, CABLE Output или переименованная).</summary>
    public bool IsVbCable => VbCable.IsVbCable(FriendlyName, AdapterName);

    /// <summary>Это 16-канальный вход VB-CABLE («CABLE In 16ch»).</summary>
    public bool IsVbCable16Ch => IsVbCable && VbCable.Is16Ch(FriendlyName);

    /// <summary>Похоже ли устройство на петлевое или виртуальное (ревью M4).</summary>
    public LoopbackRisk LoopbackRisk => LoopbackDevices.Classify(FriendlyName, AdapterName);

    /// <summary>Формат устройства известен.</summary>
    public bool HasFormat => SampleRate > 0 && Channels > 0;

    /// <summary>Совпадает ли id (без учёта регистра).</summary>
    public bool HasId(string? id) => id is not null && string.Equals(Id, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Краткое описание формата для журналов и Poc: «48000 Гц, 2 кан., float 32».</summary>
    public string FormatDescription => HasFormat
        ? $"{SampleRate} Гц, {Channels} кан., {(IsFloat ? "float" : "PCM")}{(BitsPerSample > 0 ? " " + BitsPerSample : "")}"
        : "формат неизвестен";

    /// <inheritdoc />
    public override string ToString() => $"{FriendlyName} [{Flow}, {FormatDescription}]";
}
