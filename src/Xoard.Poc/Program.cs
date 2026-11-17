using System.Text;
using Xoard.Poc;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
{
    Commands.PrintHelp();
    return 0;
}

try
{
    var options = CliOptions.Parse(args.Skip(1));
    return args[0].ToLowerInvariant() switch
    {
        "list" => Commands.List(),
        "play" => Commands.Play(options),
        "tone" => Commands.Tone(options),
        "selftest" => SelfTest.Run(options),
        "engine-devices" => EngineCommands.Devices(options),
        "engine" => EngineCommands.Interactive(options),
        "engine-selftest" => EngineSelfTest.Run(options),
        "recording-selftest" => RecordingSelfTest.Run(options),
        "burn" => EngineSelfTest.Burn(options.Positional.Count > 0 ? int.Parse(options.Positional[0], System.Globalization.CultureInfo.InvariantCulture) : 1),
        _ => throw new CliException($"Неизвестная команда «{args[0]}». Справка: .\\poc.cmd help"),
    };
}
catch (VbCableNotFoundException ex)
{
    Commands.PrintVbCableMissing(ex.Message);
    return 2;
}
catch (CliException ex)
{
    Ui.Error(ex.Message);
    return 1;
}
catch (Exception ex)
{
    Ui.Error($"Ошибка звука: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
