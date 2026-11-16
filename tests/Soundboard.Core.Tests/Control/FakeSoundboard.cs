using System.Collections.Concurrent;
using Soundboard.Protocol;

namespace Soundboard.Core.Tests.Control;

/// <summary>Поддельное приложение: два звука, запоминает вызовы.</summary>
internal sealed class FakeSoundboard : ISoundboardControl
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
        Calls.Enqueue($"volume:{args.Target}:{args.Percent}");
        Volume = args.Target switch
        {
            "headphones" => Volume with { Headphones = args.Percent },
            "cable" => Volume with { Cable = args.Percent },
            "mic" => Volume with { Mic = args.Percent },
            _ => throw new ControlException(ControlErrorCodes.BadRequest, "target должен быть headphones, cable или mic."),
        };
        return Task.FromResult(Volume);
    }

    public Task<StatusInfo> GetStatusAsync(CancellationToken ct)
    {
        Calls.Enqueue("status");
        return Task.FromResult(new StatusInfo("running", true, "CABLE Input", "CABLE Output", ["Discord"],
            new OutputInfo("running", "Headphones"), new OutputInfo("running", "CABLE Input"),
            new MicInfo(true, false, true, "Microphone"), Volume, "overlap", [], 2, []));
    }
}
