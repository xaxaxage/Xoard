using System.Text.RegularExpressions;

namespace Xoard.Core.Audio.Devices;

/// <summary>
/// Правила распознавания VB-CABLE (чистые функции, без обращения к устройствам).
/// </summary>
/// <remarks>
/// Имя конечной точки («CABLE Input») пользователь может переименовать, поэтому главный признак —
/// имя адаптера «VB-Audio Virtual Cable». Другие продукты VB-Audio (Cable A/B, Hi-Fi Cable,
/// Voicemeeter) — не VB-CABLE, даже если конечная точка названа похоже.
/// У «CABLE In 16ch» на этой машине формат смесителя тоже двухканальный, поэтому 16-канальный
/// вход отличается только по имени (<see cref="Is16Ch"/>).
/// </remarks>
public static partial class VbCable
{
    /// <summary>Имя адаптера VB-CABLE в Windows.</summary>
    public const string AdapterName = "VB-Audio Virtual Cable";

    /// <summary>Страница загрузки VB-CABLE.</summary>
    public const string DownloadUrl = "https://vb-audio.com/Cable/";

    /// <summary>Начало имени входа VB-CABLE (устройство вывода, куда мы играем).</summary>
    public const string RenderNamePrefix = "CABLE Input";

    /// <summary>Начало имени выхода VB-CABLE (устройство ввода, которое слушает Discord).</summary>
    public const string CaptureNamePrefix = "CABLE Output";

    private const string Render16ChNamePrefix = "CABLE In 16";
    private const string VbAudioVendor = "VB-Audio";

    /// <summary>
    /// Это VB-CABLE: адаптер содержит «VB-Audio Virtual Cable», либо (если адаптер не другой
    /// продукт VB-Audio) имя начинается с «CABLE Input», «CABLE Output» или «CABLE In 16».
    /// </summary>
    public static bool IsVbCable(string? friendlyName, string? adapterName)
    {
        var adapter = adapterName ?? "";
        if (adapter.Contains(AdapterName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Адаптер известен и это другой продукт VB-Audio («VB-Audio Cable A», «VB-Audio Hi-Fi Cable»…).
        if (adapter.Contains(VbAudioVendor, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = (friendlyName ?? "").TrimStart();
        return name.StartsWith(RenderNamePrefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(CaptureNamePrefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(Render16ChNamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Имя похоже на 16-канальный вход («16ch», «16 Ch», «16-ch»; регистр не важен).
    /// Число каналов формата для этого не годится: «CABLE In 16ch» сообщает 2 канала.
    /// </summary>
    public static bool Is16Ch(string? friendlyName) => friendlyName is not null && SixteenChannels().IsMatch(friendlyName);

    /// <summary>
    /// Вход VB-CABLE, в который играть: только активные устройства вывода VB-CABLE; сначала
    /// не-16-канальные, затем с именем «CABLE Input…», затем двухканальные, затем по id (детерминированно).
    /// </summary>
    public static AudioEndpointInfo? SelectRender(IEnumerable<AudioEndpointInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        return outputs
            .Where(d => d.Flow == EndpointFlow.Render && d.State == EndpointState.Active && d.IsVbCable)
            .OrderBy(d => d.IsVbCable16Ch ? 1 : 0)
            .ThenBy(d => StartsWith(d.FriendlyName, RenderNamePrefix) ? 0 : 1)
            .ThenBy(d => d.Channels == 2 ? 0 : 1)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Выход VB-CABLE (то, что слушает Discord): только активные устройства ввода VB-CABLE;
    /// сначала с именем «CABLE Output…», затем двухканальные, затем по id.
    /// </summary>
    public static AudioEndpointInfo? SelectCapture(IEnumerable<AudioEndpointInfo> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs
            .Where(d => d.Flow == EndpointFlow.Capture && d.State == EndpointState.Active && d.IsVbCable)
            .OrderBy(d => StartsWith(d.FriendlyName, CaptureNamePrefix) ? 0 : 1)
            .ThenBy(d => d.Channels == 2 ? 0 : 1)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool StartsWith(string? name, string prefix) =>
        (name ?? "").TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    // «16ch», «16 ch», «16-ch», но не «116ch»/«160ch».
    [GeneratedRegex(@"(?<!\d)16\s*-?\s*ch", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SixteenChannels();
}
