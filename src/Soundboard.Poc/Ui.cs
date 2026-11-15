namespace Soundboard.Poc;

/// <summary>Цветной вывод в консоль.</summary>
internal static class Ui
{
    public static void Info(string text) => Console.WriteLine(text);

    public static void Ok(string text) => WriteColored(ConsoleColor.Green, text, Console.Out);

    public static void Warn(string text) => WriteColored(ConsoleColor.Yellow, text, Console.Out);

    public static void Error(string text) => WriteColored(ConsoleColor.Red, text, Console.Error);

    public static void Dim(string text) => WriteColored(ConsoleColor.DarkGray, text, Console.Out);

    public static void Heading(string text) => WriteColored(ConsoleColor.Cyan, text, Console.Out);

    private static void WriteColored(ConsoleColor color, string text, TextWriter writer)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        writer.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
