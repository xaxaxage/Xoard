namespace Xoard.Core.Shell;

/// <summary>
/// Раньше программа называлась Soundboard. При первом запуске под новым именем её папки
/// (звуки в «Документах», настройки в %LOCALAPPDATA%) переносятся под имя Xoard.
/// </summary>
public static class LegacyMigration
{
    /// <summary>Прежнее имя программы.</summary>
    public const string OldName = "Soundboard";

    /// <summary>Прежняя папка настроек: %LOCALAPPDATA%\Soundboard.</summary>
    public static string LegacySettingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);

    /// <summary>Прежняя папка в «Документах» (внутри — Sounds).</summary>
    public static string LegacyDocumentsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OldName);

    /// <summary>
    /// Перенести старую папку на новое место, если новой ещё нет. Если переместить нельзя
    /// (папку держит другая программа), файлы копируются — старые остаются как были.
    /// Возвращает папку, которой пользоваться: новую, а если не вышло и скопировать — старую.
    /// </summary>
    public static string MoveFolder(string legacy, string current)
    {
        if (Directory.Exists(current) || !Directory.Exists(legacy))
            return current;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Переместить не дали — копируем
        }

        try
        {
            CopyTree(legacy, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
