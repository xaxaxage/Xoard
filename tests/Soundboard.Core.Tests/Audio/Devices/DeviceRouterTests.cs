using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Devices;
using Soundboard.Core.Audio.Status;

namespace Soundboard.Core.Tests.Audio.Devices;

/// <summary>Правила маршрутизации на устройствах этой машины (этап 0).</summary>
public class DeviceRouterTests
{
    private static readonly AudioEndpointInfo Arctis = Out("hp-arctis", "Headphones (Arctis Nova 7)", "Arctis Nova 7");
    private static readonly AudioEndpointInfo CableIn = Out("cable-in", "CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable");
    private static readonly AudioEndpointInfo Cable16 = Out("cable-16", "CABLE In 16ch (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable");
    private static readonly AudioEndpointInfo Realtek = Out("spk-realtek", "Speakers (Realtek(R) Audio)", "Realtek(R) Audio");
    private static readonly AudioEndpointInfo CableOut = In("cable-out", "CABLE Output (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable", 2);
    private static readonly AudioEndpointInfo ArctisMic = In("mic-arctis", "Microphone (Arctis Nova 7)", "Arctis Nova 7", 2);
    private static readonly AudioEndpointInfo OculusMic = In("mic-oculus", "Headset Microphone (Oculus Virtual Audio Device)", "Oculus Virtual Audio Device", 1);
    private static readonly AudioEndpointInfo StereoMix = In("stereo-mix", "Stereo Mix (Realtek(R) Audio)", "Realtek(R) Audio", 2);

    private static AudioEndpointInfo Out(string id, string name, string adapter) =>
        new(id, name, adapter, EndpointFlow.Render, 2, 48_000, true, 32);

    private static AudioEndpointInfo In(string id, string name, string adapter, int ch) =>
        new(id, name, adapter, EndpointFlow.Capture, ch, 48_000, true, 32);

    private static DeviceSnapshot Machine(string defaultRender = "hp-arctis", string defaultCapture = "mic-arctis") => new(
        [Realtek, Arctis, Cable16, CableIn],
        [CableOut, OculusMic, ArctisMic, StereoMix],
        DefaultEndpoints.Same(defaultRender, defaultCapture));

    [Fact]
    public void This_machine_routes_as_expected()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default);

        Assert.Equal("cable-in", plan.Cable?.Device.Id);           // не 16ch
        Assert.Equal("cable-out", plan.CableCapture?.Id);
        Assert.Equal("hp-arctis", plan.Headphones?.Device.Id);
        Assert.Equal(ChoiceReason.WindowsDefault, plan.Headphones?.Reason);
        Assert.Equal("mic-arctis", plan.Mic?.Device.Id);
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void Default_output_on_cable_warns_and_leaves_no_headphones()
    {
        var plan = DeviceRouter.Resolve(Machine(defaultRender: "cable-in"), AudioSettings.Default);

        Assert.Contains(plan.Issues, i => i.Code == IssueCode.DefaultOutputIsVbCable);
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.HeadphonesUnavailable);
        Assert.Null(plan.Headphones);
        Assert.Equal("cable-in", plan.Cable?.Device.Id);
    }

    [Fact]
    public void Cable_chosen_as_headphones_is_refused_and_default_used()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { HeadphonesDeviceId = "cable-in" });

        Assert.Contains(plan.Issues, i => i.Code == IssueCode.HeadphonesRejectedVbCable);
        Assert.Equal("hp-arctis", plan.Headphones?.Device.Id);
        Assert.Equal(ChoiceReason.FallbackToWindowsDefault, plan.Headphones?.Reason);
    }

    [Fact]
    public void User_selected_headphones_win()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { HeadphonesDeviceId = "spk-realtek" });
        Assert.Equal("spk-realtek", plan.Headphones?.Device.Id);
        Assert.Equal(ChoiceReason.UserSelected, plan.Headphones?.Reason);
    }

    [Fact]
    public void Missing_preferred_headphones_fall_back_with_warning()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { HeadphonesDeviceId = "gone" });
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.HeadphonesPreferredMissing);
        Assert.Equal("hp-arctis", plan.Headphones?.Device.Id);
    }

    [Fact]
    public void Headphones_disabled_means_no_headphones_bus()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { HeadphonesEnabled = false });
        Assert.Null(plan.Headphones);
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void Loopback_default_mic_is_never_auto_picked()
    {
        var plan = DeviceRouter.Resolve(Machine(defaultCapture: "stereo-mix"), AudioSettings.Default);
        Assert.Null(plan.Mic);
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.MicIsLoopbackDevice);
    }

    [Fact]
    public void Cable_output_as_default_mic_is_refused()
    {
        var plan = DeviceRouter.Resolve(Machine(defaultCapture: "cable-out"), AudioSettings.Default);
        Assert.Null(plan.Mic);
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.MicDefaultIsVbCable);
    }

    [Fact]
    public void Cable_output_chosen_as_mic_is_refused()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { MicDeviceId = "cable-out" });
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.MicRejectedVbCable);
        Assert.Equal("mic-arctis", plan.Mic?.Device.Id);   // взят микрофон по умолчанию
    }

    [Fact]
    public void Explicit_loopback_mic_is_used_with_warning()
    {
        var plan = DeviceRouter.Resolve(Machine(), AudioSettings.Default with { MicDeviceId = "stereo-mix" });
        Assert.Equal("stereo-mix", plan.Mic?.Device.Id);
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.MicIsLoopbackDevice);
    }

    [Fact]
    public void Missing_vb_cable_is_reported_with_download_link()
    {
        var s = new DeviceSnapshot([Arctis], [ArctisMic], DefaultEndpoints.Same("hp-arctis", "mic-arctis"));
        var plan = DeviceRouter.Resolve(s, AudioSettings.Default);

        Assert.Null(plan.Cable);
        var issue = Assert.Single(plan.Issues, i => i.Code == IssueCode.VbCableNotInstalled);
        Assert.Equal(VbCable.DownloadUrl, issue.HelpUrl);
        Assert.Equal("hp-arctis", plan.Headphones?.Device.Id);   // наушники работают и без кабеля
    }

    [Fact]
    public void Disabled_vb_cable_is_distinguished_from_missing()
    {
        var disabled = CableIn with { State = EndpointState.Disabled };
        var s = new DeviceSnapshot([Arctis], [ArctisMic], DefaultEndpoints.Same("hp-arctis", "mic-arctis"), [disabled]);
        var plan = DeviceRouter.Resolve(s, AudioSettings.Default);

        Assert.Contains(plan.Issues, i => i.Code == IssueCode.VbCableRenderDisabled);
        Assert.DoesNotContain(plan.Issues, i => i.Code == IssueCode.VbCableNotInstalled);
    }

    [Fact]
    public void Non_48k_device_is_reported()
    {
        var cable441 = CableIn with { SampleRate = 44_100 };
        var s = new DeviceSnapshot([Arctis, cable441], [CableOut, ArctisMic], DefaultEndpoints.Same("hp-arctis", "mic-arctis"));
        var plan = DeviceRouter.Resolve(s, AudioSettings.Default);

        Assert.Contains(plan.Issues, i => i.Code == IssueCode.DeviceFormatNot48k);
        Assert.Contains(plan.Issues, i => i.Code == IssueCode.DeviceRatesDiffer);
    }

    [Theory]
    [InlineData("Stereo Mix (Realtek(R) Audio)", "", LoopbackRisk.Loopback)]
    [InlineData("Steam Streaming Microphone", "Steam Streaming Microphone", LoopbackRisk.Loopback)]
    [InlineData("VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)", "", LoopbackRisk.Virtual)]
    [InlineData("Microphone (Arctis Nova 7)", "Arctis Nova 7", LoopbackRisk.None)]
    public void Loopback_classification(string name, string adapter, LoopbackRisk expected) =>
        Assert.Equal(expected, LoopbackDevices.Classify(name, adapter));
}
