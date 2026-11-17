using System.Diagnostics;

namespace Xoard.App.Updates;

/// <summary>
/// Сервер для Claude Desktop (Xoard.Mcp.exe) едет вместе с программой, но Claude держит его
/// запущенным — обновление программы не смогло бы его заменить, а путь в конфиге Claude менялся бы.
/// Поэтому при каждом запуске его копия кладётся в постоянное место рядом с настройками
/// (%LOCALAPPDATA%\Xoard\mcp\Xoard.Mcp.exe). Если Claude сейчас держит старую копию — заменим в другой раз.
/// </summary>
public static class McpServerCopy
{
    public const string FileName = "Xoard.Mcp.exe";

    /// <summary>Положить свежую копию. Возвращает путь для конфига Claude или null, если сервера в сборке нет.</summary>
    public static string? Sync(string settingsDir)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "mcp", FileName);
        var dest = Path.Combine(settingsDir, "mcp", FileName);
        if (!File.Exists(source))
            return File.Exists(dest) ? dest : null;

        try
        {
            if (!File.Exists(dest) || !SameBuild(source, dest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(source, dest, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Claude Desktop сейчас держит старую копию — она работает, заменим при следующем запуске
        }

        return dest;
    }

    private static bool SameBuild(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length
            && FileVersionInfo.GetVersionInfo(a).FileVersion == FileVersionInfo.GetVersionInfo(b).FileVersion;
    }
}
