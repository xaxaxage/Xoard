using System.Collections.Concurrent;
using Xoard.Protocol;

namespace Xoard.Core.Tests.Control;

/// <summary>Поддельное приложение: два звука, запоминает вызовы.</summary>
internal sealed class FakeXoard : IXoardControl
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public VolumeInfo Volume { get; private set; } = new(80, 100, 100, false);

    private static readonly SoundInfo[] Sounds =
    [
        new("airhorn.mp3", "Airhorn", 2.5, "Ctrl+1", false, true, null),
        new("ба-дум-тсс.wav", "Ба-дум-тсс", 1.2, null, false, true, null),
    ];

    public Task<SoundList> ListSoundsAsync(CancellationToken ct)
    {
        Calls.Enqueue("list");
        return Task.FromResult(new SoundList(Sounds));
    }

    public Task<PlayInfo> PlaySoundAsync(PlaySoundArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"play:{args.Name ?? args.Id}:{args.PreviewOnly}");
        var sound = Sounds.FirstOrDefault(s => s.Id == args.Id || (args.Name is not null && s.Name.Contains(args.Name, StringComparison.OrdinalIgnoreCase)))
            ?? throw new ControlException(ControlErrorCodes.NotFound, $"Звук «{args.Name ?? args.Id}» не найден.");
        return Task.FromResult(new PlayInfo(sound.Id, sound.Name, sound.DurationSeconds, args.PreviewOnly));
    }

    public Task StopAllAsync(CancellationToken ct)
    {
        Calls.Enqueue("stop");
        return Task.CompletedTask;
    }

    public Task<VolumeInfo> SetVolumeAsync(SetVolumeArgs args, CancellationToken ct)
    {
        Calls.Enqueue(FormattableString.Invariant($"volume:{args.Target}:{args.Percent}"));
        Volume = args.Target switch
        {
            "headphones" => Volume with { Headphones = args.Percent },
            "cable" => Volume with { Cable = args.Percent },
            "mic" => Volume with { Mic = args.Percent },
            _ => throw new ControlException(ControlErrorCodes.BadRequest, "target должен быть headphones, cable или mic."),
        };
        return Task.FromResult(Volume);
    }

    public Task<HotkeyInfo> SetHotkeyAsync(SetHotkeyArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"hotkey:{args.Target}:{args.Hotkey}");
        return Task.FromResult(new HotkeyInfo(args.Target, string.IsNullOrEmpty(args.Hotkey) ? null : args.Hotkey));
    }

    public Task<RecordingInfo> StartRecordingAsync(StartRecordingArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"record:{args.Source}");
        return Task.FromResult(new RecordingInfo(true, args.Source, 0, true, 12));
    }

    public Task<SoundInfo> StopRecordingAsync(SaveSoundArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"stop-record:{args.Name}");
        return Task.FromResult(new SoundInfo("rec.wav", args.Name ?? "Запись", 3, null, false, true, null));
    }

    public Task<SoundInfo> SaveLastSecondsAsync(SaveSoundArgs args, CancellationToken ct)
    {
        Calls.Enqueue(FormattableString.Invariant($"replay:{args.Seconds}:{args.Name}"));
        return Task.FromResult(new SoundInfo("replay.wav", args.Name ?? "Повтор", args.Seconds, null, false, true, null));
    }

    public Task<SoundInfo> RenameSoundAsync(RenameSoundArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"rename:{args.Name ?? args.Id}:{args.NewName}");
        return Task.FromResult(new SoundInfo(args.NewName + ".mp3", args.NewName, 2.5, null, false, true, null));
    }

    public Task<DeletedInfo> DeleteSoundAsync(SoundRefArgs args, CancellationToken ct)
    {
        Calls.Enqueue($"delete:{args.Name ?? args.Id}");
        return Task.FromResult(new DeletedInfo("airhorn.mp3", "Airhorn"));
    }

    public Task<SoundInfo> TrimSoundAsync(TrimSoundArgs args, CancellationToken ct)
    {
        Calls.Enqueue(FormattableString.Invariant($"trim:{args.Name ?? args.Id}:{args.Start}-{args.End}"));
        if (args.End <= args.Start)
            throw new ControlException(ControlErrorCodes.BadRequest, "Конец должен быть позже начала.");
        return Task.FromResult(new SoundInfo("airhorn.wav", "Airhorn", args.End - args.Start, null, false, true, null));
    }

    public Task<StatusInfo> GetStatusAsync(CancellationToken ct)
    {
        Calls.Enqueue("status");
        return Task.FromResult(new StatusInfo("running", true, "CABLE Input", "CABLE Output", ["Discord"],
            new OutputInfo("running", "Headphones"), new OutputInfo("running", "CABLE Input"),
            new MicInfo(true, false, true, "Microphone"), Volume, "overlap", [], 2, []));
    }
}
