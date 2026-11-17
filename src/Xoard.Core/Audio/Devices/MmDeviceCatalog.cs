using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Xoard.Core.Audio.Devices;

/// <summary>Источник снимков звуковых устройств (подменяется в тестах).</summary>
public interface IDeviceCatalog
{
    /// <summary>Текущий снимок устройств.</summary>
    DeviceSnapshot Snapshot();
}

/// <summary>Программа, у которой открыт активный поток захвата на устройстве (например, Discord на CABLE Output).</summary>
public sealed record ListeningApp(int ProcessId, string ProcessName);

/// <summary>
/// Снимок устройств через WASAPI (MMDeviceEnumerator). Создавать и вызывать из MTA-потока.
/// </summary>
public sealed class MmDeviceCatalog : IDeviceCatalog, IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();

    /// <summary>Перечислитель для построения потоков (живёт столько же, сколько каталог).</summary>
    internal MMDeviceEnumerator Enumerator => enumerator;

    /// <inheritdoc />
    public DeviceSnapshot Snapshot()
    {
        var outputs = Enumerate(DataFlow.Render, DeviceState.Active, EndpointState.Active);
        var inputs = Enumerate(DataFlow.Capture, DeviceState.Active, EndpointState.Active);
        var disabled = Enumerate(DataFlow.Render, DeviceState.Disabled, EndpointState.Disabled)
            .Concat(Enumerate(DataFlow.Capture, DeviceState.Disabled, EndpointState.Disabled))
            .ToList();

        var defaults = new DefaultEndpoints(
            DefaultId(DataFlow.Render, Role.Console),
            DefaultId(DataFlow.Render, Role.Multimedia),
            DefaultId(DataFlow.Render, Role.Communications),
            DefaultId(DataFlow.Capture, Role.Console),
            DefaultId(DataFlow.Capture, Role.Communications));

        return new DeviceSnapshot(outputs, inputs, defaults, disabled);
    }

    /// <summary>Устройство по id (вызывающий освобождает его) или <c>null</c>.</summary>
    internal MMDevice? TryGetDevice(string id)
    {
        try
        {
            return enumerator.GetDevice(id);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Программы с активным потоком захвата на устройстве ввода (ревью M5): кто сейчас слушает CABLE Output.
    /// Собственный процесс не включается.
    /// </summary>
    public IReadOnlyList<ListeningApp> ListeningApps(string captureDeviceId)
    {
        using var device = TryGetDevice(captureDeviceId);
        if (device is null)
            return [];

        var result = new List<ListeningApp>();
        var self = Environment.ProcessId;
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    continue;
                var pid = (int)session.GetProcessID;
                if (pid == 0 || pid == self)
                    continue;
                result.Add(new ListeningApp(pid, ProcessName(pid)));
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Устройство исчезло во время опроса — считаем, что слушателей нет
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose() => enumerator.Dispose();

    private List<AudioEndpointInfo> Enumerate(DataFlow flow, DeviceState state, EndpointState endpointState)
    {
        var list = new List<AudioEndpointInfo>();
        MMDeviceCollection collection;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(flow, state);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return list;
        }

        foreach (var device in collection)
        {
            using (device)
            {
                list.Add(ToInfo(device, flow == DataFlow.Render ? EndpointFlow.Render : EndpointFlow.Capture, endpointState));
            }
        }

        return list;
    }

    private static AudioEndpointInfo ToInfo(MMDevice device, EndpointFlow flow, EndpointState state)
    {
        var id = device.ID;
        var name = Safe(() => device.FriendlyName) ?? id;
        var adapter = Safe(() => device.DeviceFriendlyName) ?? "";

        int channels = 0, rate = 0, bits = 0;
        var isFloat = false;
        if (state == EndpointState.Active)
        {
            try
            {
                using var client = device.CreateAudioClient();
                var format = client.MixFormat;
                channels = format.Channels;
                rate = format.SampleRate;
                bits = format.BitsPerSample;
                isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                    || (format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypesFloat);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Формат неизвестен (устройство занято монопольно или недоступно)
            }
        }

        return new AudioEndpointInfo(id, name, adapter, flow, channels, rate, isFloat, bits, state);
    }

    private static readonly Guid AudioMediaSubtypesFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private string? DefaultId(DataFlow flow, Role role)
    {
        try
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device))
                return null;
            using (device)
            {
                return device.ID;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"pid {pid}";
        }
    }

    private static string? Safe(Func<string> get)
    {
        try
        {
            var value = get();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
