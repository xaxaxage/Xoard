using Soundboard.Core.Audio.Status;

namespace Soundboard.Core.Audio.Devices;

/// <summary>Почему выбрано именно это устройство.</summary>
public enum ChoiceReason
{
    /// <summary>Выбрано пользователем.</summary>
    UserSelected,

    /// <summary>Устройство Windows по умолчанию (пользователь ничего не выбирал).</summary>
    WindowsDefault,

    /// <summary>Выбранное пользователем устройство недоступно — взято устройство по умолчанию.</summary>
    FallbackToWindowsDefault,

    /// <summary>Найдено автоматически (VB-CABLE).</summary>
    Auto,
}

/// <summary>Выбранное устройство и причина выбора.</summary>
public sealed record DeviceChoice(AudioEndpointInfo Device, ChoiceReason Reason);

/// <summary>
/// План маршрутизации: куда играть звуки (наушники, VB-CABLE), откуда брать голос, что слушает
/// Discord (CABLE Output) и какие проблемы показать пользователю.
/// </summary>
public sealed record RoutingPlan(
    DeviceChoice? Cable,
    DeviceChoice? Headphones,
    DeviceChoice? Mic,
    AudioEndpointInfo? CableCapture,
    IReadOnlyList<EngineIssue> Issues);

/// <summary>
/// Правила выбора устройств (чистая функция от снимка и настроек).
/// Главное правило против эха: VB-CABLE никогда не становится «наушниками» или «микрофоном»,
/// а петлевые устройства не выбираются автоматически.
/// </summary>
public static class DeviceRouter
{
    /// <summary>Строит план по снимку устройств и настройкам пользователя.</summary>
    public static RoutingPlan Resolve(DeviceSnapshot snapshot, AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        var issues = new List<EngineIssue>();

        // VB-CABLE
        var cableRender = VbCable.SelectRender(snapshot.Outputs);
        var cableCapture = VbCable.SelectCapture(snapshot.Inputs);
        if (cableRender is null)
        {
            issues.Add(IssueText.Create(snapshot.VbCableRenderDisabled ? IssueCode.VbCableRenderDisabled : IssueCode.VbCableNotInstalled));
        }
        else
        {
            AddFormatIssue(issues, cableRender);
        }

        if (cableCapture is null && snapshot.VbCableCaptureDisabled)
            issues.Add(IssueText.Create(IssueCode.VbCableCaptureDisabled));

        if (cableRender is { HasFormat: true } && cableCapture is { HasFormat: true } && cableRender.SampleRate != cableCapture.SampleRate)
        {
            issues.Add(IssueText.Create(IssueCode.DeviceRatesDiffer, cableRender.FriendlyName,
                $"{cableRender.SampleRate} Гц / {cableCapture.SampleRate} Гц"));
        }

        // Устройство вывода Windows по умолчанию — VB-CABLE (для любой роли)
        var defaults = snapshot.Defaults;
        foreach (var id in new[] { defaults.RenderConsole, defaults.RenderMultimedia, defaults.RenderCommunications })
        {
            if (snapshot.FindOutput(id) is { IsVbCable: true } d)
            {
                issues.Add(IssueText.Create(IssueCode.DefaultOutputIsVbCable, d.FriendlyName, deviceId: d.Id));
                break;
            }
        }

        var headphones = settings.HeadphonesEnabled ? ResolveHeadphones(snapshot, settings, issues) : null;
        var mic = settings.MicPassthroughEnabled ? ResolveMic(snapshot, settings, issues) : null;

        return new RoutingPlan(
            cableRender is null ? null : new DeviceChoice(cableRender, ChoiceReason.Auto),
            headphones,
            mic,
            cableCapture,
            issues);
    }

    private static DeviceChoice? ResolveHeadphones(DeviceSnapshot s, AudioSettings settings, List<EngineIssue> issues)
    {
        var wanted = settings.EffectiveHeadphonesDeviceId;
        DeviceChoice? choice = null;

        if (wanted is not null)
        {
            var device = s.FindOutput(wanted);
            if (device is null)
            {
                issues.Add(IssueText.Create(IssueCode.HeadphonesPreferredMissing, s.FindAny(wanted)?.FriendlyName, deviceId: wanted));
            }
            else if (device.IsVbCable)
            {
                issues.Add(IssueText.Create(IssueCode.HeadphonesRejectedVbCable, device.FriendlyName, deviceId: device.Id));
            }
            else
            {
                choice = new DeviceChoice(device, ChoiceReason.UserSelected);
                if (device.LoopbackRisk != LoopbackRisk.None)
                    issues.Add(IssueText.Create(IssueCode.HeadphonesIsLoopbackDevice, device.FriendlyName, deviceId: device.Id));
            }
        }

        if (choice is null)
        {
            var fallback = s.FindOutput(s.Defaults.RenderConsole) ?? s.FindOutput(s.Defaults.RenderMultimedia);
            if (fallback is not null && !fallback.IsVbCable && fallback.LoopbackRisk == LoopbackRisk.None)
                choice = new DeviceChoice(fallback, wanted is null ? ChoiceReason.WindowsDefault : ChoiceReason.FallbackToWindowsDefault);
            else
                issues.Add(IssueText.Create(IssueCode.HeadphonesUnavailable, fallback?.FriendlyName));
        }

        if (choice is not null)
            AddFormatIssue(issues, choice.Device);
        return choice;
    }

    private static DeviceChoice? ResolveMic(DeviceSnapshot s, AudioSettings settings, List<EngineIssue> issues)
    {
        var wanted = settings.EffectiveMicDeviceId;

        if (wanted is not null)
        {
            var device = s.FindInput(wanted);
            if (device is null)
            {
                issues.Add(IssueText.Create(IssueCode.MicPreferredMissing, s.FindAny(wanted)?.FriendlyName, deviceId: wanted));
            }
            else if (device.IsVbCable)
            {
                issues.Add(IssueText.Create(IssueCode.MicRejectedVbCable, device.FriendlyName, deviceId: device.Id));
            }
            else
            {
                // Явный выбор уважаем, но предупреждаем: петлевой «микрофон» даёт эхо
                if (device.LoopbackRisk != LoopbackRisk.None)
                    issues.Add(IssueText.Create(IssueCode.MicIsLoopbackDevice, device.FriendlyName, deviceId: device.Id));
                return new DeviceChoice(device, ChoiceReason.UserSelected);
            }
        }

        var fallback = s.FindInput(s.Defaults.CaptureConsole) ?? s.FindInput(s.Defaults.CaptureCommunications);
        if (fallback is null)
        {
            issues.Add(IssueText.Create(IssueCode.MicUnavailable));
            return null;
        }

        if (fallback.IsVbCable)
        {
            issues.Add(IssueText.Create(IssueCode.MicDefaultIsVbCable, fallback.FriendlyName, deviceId: fallback.Id));
            return null;
        }

        if (fallback.LoopbackRisk != LoopbackRisk.None)
        {
            issues.Add(IssueText.Create(IssueCode.MicIsLoopbackDevice, fallback.FriendlyName, deviceId: fallback.Id));
            return null;
        }

        return new DeviceChoice(fallback, wanted is null ? ChoiceReason.WindowsDefault : ChoiceReason.FallbackToWindowsDefault);
    }

    private static void AddFormatIssue(List<EngineIssue> issues, AudioEndpointInfo device)
    {
        if (device.HasFormat && device.SampleRate != AudioFormat.SampleRate)
            issues.Add(IssueText.Create(IssueCode.DeviceFormatNot48k, device.FriendlyName, $"{device.SampleRate} Гц", device.Id));
    }
}
