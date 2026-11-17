using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Xoard.Poc;

/// <summary>VB-CABLE не найден в системе.</summary>
internal sealed class VbCableNotFoundException(string message) : Exception(message);

/// <summary>Поиск устройств WASAPI и VB-CABLE.</summary>
internal static class AudioDevices
{
    /// <summary>Имя «адаптера» VB-CABLE. Имя самой конечной точки («CABLE Input») пользователь
    /// может переименовать в настройках звука, а в диспетчере устройств оно вообще показывается
    /// иначе («Speakers»), поэтому ищем прежде всего по имени драйвера.</summary>
    public const string VbCableAdapterName = "VB-Audio Virtual Cable";

    public const string VbCableDownloadUrl = "https://vb-audio.com/Cable/";

    public static bool IsVbCable(MMDevice device) =>
        Contains(SafeAdapterName(device), VbCableAdapterName)
        || Contains(device.FriendlyName, "CABLE Input")
        || Contains(device.FriendlyName, "CABLE Output");

    public static List<MMDevice> ActiveDevices(MMDeviceEnumerator enumerator, DataFlow flow) =>
        [.. enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)];

    /// <summary>Вход VB-CABLE (устройство вывода, куда мы играем звук).
    /// Если вариантов несколько (обычный и «16 Ch»), берём обычный двухканальный.</summary>
    public static MMDevice FindVbCableRender(MMDeviceEnumerator enumerator) =>
        ActiveDevices(enumerator, DataFlow.Render)
            .Where(IsVbCable)
            .OrderBy(d => Contains(d.FriendlyName, "CABLE Input") ? 0 : 1)
            .ThenBy(d => Contains(d.FriendlyName, "16 Ch") ? 1 : 0)
            .ThenBy(d => TryGetMixFormat(d)?.Channels == 2 ? 0 : 1)
            .FirstOrDefault()
        ?? throw new VbCableNotFoundException(
            "Не найден VB-CABLE (виртуальный кабель VB-Audio): нет активного устройства вывода «CABLE Input».");

    /// <summary>Выход VB-CABLE (устройство ввода «CABLE Output» — это «микрофон» для Discord).
    /// Его никогда не выводим в наушники, иначе будет эхо.</summary>
    public static MMDevice FindVbCableCapture(MMDeviceEnumerator enumerator) =>
        ActiveDevices(enumerator, DataFlow.Capture)
            .Where(IsVbCable)
            .OrderBy(d => Contains(d.FriendlyName, "CABLE Output") ? 0 : 1)
            .FirstOrDefault()
        ?? throw new VbCableNotFoundException(
            "Не найден VB-CABLE (виртуальный кабель VB-Audio): нет активного устройства ввода «CABLE Output».");

    /// <summary>Устройство вывода по части имени (без учёта регистра).</summary>
    public static MMDevice FindRenderByName(MMDeviceEnumerator enumerator, string namePart)
    {
        var matches = ActiveDevices(enumerator, DataFlow.Render)
            .Where(d => Contains(d.FriendlyName, namePart))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"Устройство вывода, содержащее «{namePart}», не найдено. Список: .\\poc.cmd list"),
            _ => throw new CliException(
                $"Под «{namePart}» подходит несколько устройств, уточните:{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", matches.Select(m => m.FriendlyName))),
        };
    }

    public static MMDevice? DefaultRender(MMDeviceEnumerator enumerator) =>
        enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : null;

    public static MMDevice? DefaultCapture(MMDeviceEnumerator enumerator) =>
        enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : null;

    /// <summary>Формат общего (shared) режима устройства — то, во что Windows микширует звук.</summary>
    public static WaveFormat? TryGetMixFormat(MMDevice device)
    {
        try
        {
            using var client = device.CreateAudioClient();
            return client.MixFormat;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string SafeAdapterName(MMDevice device)
    {
        try
        {
            return device.DeviceFriendlyName ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool Contains(string? text, string part) =>
        text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;
}
