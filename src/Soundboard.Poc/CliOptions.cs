using System.Globalization;

namespace Soundboard.Poc;

/// <summary>Ошибка в аргументах командной строки или понятная пользователю ошибка.</summary>
internal sealed class CliException(string message) : Exception(message);

internal sealed class CliOptions
{
    public List<string> Positional { get; } = [];
    public string? Device { get; private set; }
    public bool AlsoDefault { get; private set; }
    public float? Volume { get; private set; }
    public double? Seconds { get; private set; }
    public double? Frequency { get; private set; }
    public int LatencyMs { get; private set; } = 50;
    /// <summary>Тишина до звука. VB-CABLE теряет начало звука, если его поток стартует «холодным».</summary>
    public int PreRollMs { get; private set; } = 100;

    /// <summary>Тишина после звука. VB-CABLE выбрасывает хвост из своего буфера, если поток останавливается сразу.</summary>
    public int PostRollMs { get; private set; } = 200;

    // ── этап 1: движок ──
    public string? Sounds { get; private set; }
    public string? Headphones { get; private set; }
    public string? Mic { get; private set; }
    public bool NoMic { get; private set; }
    public Soundboard.Core.Audio.LatencyProfile Profile { get; private set; } = Soundboard.Core.Audio.LatencyProfile.Default;
    public int Load { get; private set; }
    public int Runs { get; private set; } = 1;
    public bool Force { get; private set; }

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var options = new CliOptions();
        using var it = args.GetEnumerator();
        while (it.MoveNext())
        {
            var arg = it.Current;
            switch (arg.ToLowerInvariant())
            {
                case "--device":
                    options.Device = NextValue(it, arg);
                    break;
                case "--also-default":
                    options.AlsoDefault = true;
                    break;
                case "--volume":
                    options.Volume = (float)ParseNumber(NextValue(it, arg), arg, 0, 1);
                    break;
                case "--seconds":
                    options.Seconds = ParseNumber(NextValue(it, arg), arg, 0.1, 600);
                    break;
                case "--freq":
                    options.Frequency = ParseNumber(NextValue(it, arg), arg, 20, 20_000);
                    break;
                case "--latency":
                    options.LatencyMs = (int)ParseNumber(NextValue(it, arg), arg, 10, 500);
                    break;
                case "--preroll":
                    options.PreRollMs = (int)ParseNumber(NextValue(it, arg), arg, 0, 2000);
                    break;
                case "--postroll":
                    options.PostRollMs = (int)ParseNumber(NextValue(it, arg), arg, 0, 2000);
                    break;
                case "--sounds":
                    options.Sounds = NextValue(it, arg);
                    break;
                case "--headphones":
                    options.Headphones = NextValue(it, arg);
                    break;
                case "--mic":
                    options.Mic = NextValue(it, arg);
                    break;
                case "--no-mic":
                    options.NoMic = true;
                    break;
                case "--profile":
                    var profile = NextValue(it, arg);
                    options.Profile = profile.ToLowerInvariant() switch
                    {
                        "default" => Soundboard.Core.Audio.LatencyProfile.Default,
                        "safe" => Soundboard.Core.Audio.LatencyProfile.Safe,
                        "low" => Soundboard.Core.Audio.LatencyProfile.Low,
                        _ => throw new CliException($"--profile: default, safe или low, получено «{profile}»."),
                    };
                    break;
                case "--load":
                    options.Load = (int)ParseNumber(NextValue(it, arg), arg, 0, 64);
                    break;
                case "--runs":
                    options.Runs = (int)ParseNumber(NextValue(it, arg), arg, 1, 100);
                    break;
                case "--force":
                    options.Force = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliException($"Неизвестная опция {arg}. Справка: .\\poc.cmd help");
                    }

                    options.Positional.Add(arg);
                    break;
            }
        }

        return options;
    }

    private static string NextValue(IEnumerator<string> it, string name) =>
        it.MoveNext() ? it.Current : throw new CliException($"После {name} нужно значение.");

    private static double ParseNumber(string text, string name, double min, double max)
    {
        // Принимаем и «0.5», и «0,5»
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
        {
            throw new CliException($"{name}: ожидается число от {min} до {max}, получено «{text}».");
        }

        return value;
    }
}
