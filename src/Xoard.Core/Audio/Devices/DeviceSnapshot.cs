namespace Xoard.Core.Audio.Devices;

/// <summary>
/// Снимок звуковых устройств в один момент: активные выходы и входы, устройства по умолчанию
/// и отключённые конечные точки (чтобы отличить «VB-CABLE не установлен» от «CABLE Input отключён»
/// и назвать по имени выбранное, но отключённое устройство). Неизменяем; строится в
/// <see cref="IDeviceCatalog.Snapshot"/>.
/// </summary>
/// <param name="Outputs">Активные устройства вывода (в порядке Windows).</param>
/// <param name="Inputs">Активные устройства ввода.</param>
/// <param name="Defaults">Устройства по умолчанию для каждой роли.</param>
/// <param name="Disabled">Отключённые конечные точки обоих направлений (формат у них неизвестен).</param>
public sealed record DeviceSnapshot(
    IReadOnlyList<AudioEndpointInfo> Outputs,
    IReadOnlyList<AudioEndpointInfo> Inputs,
    DefaultEndpoints Defaults,
    IReadOnlyList<AudioEndpointInfo> Disabled)
{
    /// <summary>Снимок без устройств (нет звуковой службы или устройств).</summary>
    public static DeviceSnapshot Empty { get; } = new([], [], DefaultEndpoints.None, []);

    /// <summary>Снимок без отключённых устройств.</summary>
    public DeviceSnapshot(IReadOnlyList<AudioEndpointInfo> outputs, IReadOnlyList<AudioEndpointInfo> inputs, DefaultEndpoints defaults)
        : this(outputs, inputs, defaults, [])
    {
    }

    /// <summary>Есть отключённая конечная точка вывода VB-CABLE (CABLE Input или CABLE In 16ch).</summary>
    public bool VbCableRenderDisabled => Disabled.Any(d => d.Flow == EndpointFlow.Render && d.IsVbCable);

    /// <summary>Есть отключённая конечная точка ввода VB-CABLE (CABLE Output).</summary>
    public bool VbCableCaptureDisabled => Disabled.Any(d => d.Flow == EndpointFlow.Capture && d.IsVbCable);

    /// <summary>Активное устройство вывода по id или <c>null</c>.</summary>
    public AudioEndpointInfo? FindOutput(string? id) => Find(Outputs, id);

    /// <summary>Активное устройство ввода по id или <c>null</c>.</summary>
    public AudioEndpointInfo? FindInput(string? id) => Find(Inputs, id);

    /// <summary>Любое известное устройство по id: активное (вывод, ввод) или отключённое.</summary>
    public AudioEndpointInfo? FindAny(string? id) => FindOutput(id) ?? FindInput(id) ?? Find(Disabled, id);

    private static AudioEndpointInfo? Find(IReadOnlyList<AudioEndpointInfo> list, string? id)
    {
        if (id is null)
            return null;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].HasId(id))
                return list[i];
        }
        return null;
    }
}
