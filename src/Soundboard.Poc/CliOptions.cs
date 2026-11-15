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
