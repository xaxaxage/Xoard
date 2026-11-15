using System.Text;
using Soundboard.Poc;

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
