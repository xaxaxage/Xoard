namespace Xoard.Core.Audio.Devices;

/// <summary>Насколько устройство похоже на петлевое или виртуальное (ревью M4).</summary>
public enum LoopbackRisk
{
    /// <summary>Обычное физическое устройство.</summary>
    None,

    /// <summary>Виртуальное устройство: может возвращать то, что в него играют другие программы.</summary>
    Virtual,

    /// <summary>Петлевая запись («Stereo Mix», «What U Hear»…): возвращает звук самого компьютера.</summary>
    Loopback,
}

/// <summary>
/// Распознавание петлевых и виртуальных устройств по имени (чистые функции).
/// Такой «микрофон» вернул бы в VB-CABLE звук компьютера — друзья услышали бы сами себя,
/// а звуки сыграли бы дважды. Поэтому автоматически такие устройства не выбираются.
/// </summary>
public static class LoopbackDevices
{
    private static readonly string[] LoopbackPatterns =
    [
        "Stereo Mix", "Стерео микшер", "What U Hear", "Wave Out Mix", "Wave-Out Mix", "Rec. Playback",
        "Loopback", "Steam Streaming", "Что слышно",
    ];

    private static readonly string[] VirtualPatterns =
    [
        "Voicemeeter", "VB-Audio", "Virtual", "Виртуальн", "NVIDIA Broadcast", "Krisp", "SteelSeries Sonar",
        "Elgato Wave Link", "OBS", "Discord Stream",
    ];

    /// <summary>Классифицирует устройство по имени конечной точки и имени адаптера.</summary>
    public static LoopbackRisk Classify(string? friendlyName, string? adapterName)
    {
        var text = $"{friendlyName} {adapterName}";
        if (ContainsAny(text, LoopbackPatterns))
            return LoopbackRisk.Loopback;
        return ContainsAny(text, VirtualPatterns) ? LoopbackRisk.Virtual : LoopbackRisk.None;
    }

    private static bool ContainsAny(string text, string[] patterns)
    {
        foreach (var p in patterns)
        {
            if (text.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
