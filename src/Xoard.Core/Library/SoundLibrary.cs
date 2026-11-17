using System.Runtime.InteropServices;

namespace Xoard.Core.Library;

/// <summary>Звук в папке библиотеки.</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Name">Отображаемое имя (имя файла без расширения).</param>
public sealed record SoundFile(string Path, string Name)
{
    /// <summary>Имя файла с расширением (ключ порядка кнопок).</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Ошибка операции с библиотекой (сообщение — для пользователя).</summary>
public sealed class SoundLibraryException : Exception
{
    public SoundLibraryException()
    {
    }

    public SoundLibraryException(string message)
        : base(message)
    {
    }

    public SoundLibraryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Удаление в корзину (подменяется в тестах).</summary>
public interface IRecycleBin
{
    /// <summary>Переместить файл в корзину.</summary>
    void Delete(string path);
}

/// <summary>
/// Папка звуков (Документы\Xoard\Sounds): список, добавление (копирование), переименование,
/// удаление в корзину. Порядок кнопок хранится в настройках по именам файлов.
/// </summary>
public sealed class SoundLibrary
{
    /// <summary>Поддерживаемые форматы (декодирует NAudio / Media Foundation).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ogg", ".opus"];

    private readonly IRecycleBin recycleBin;

    public SoundLibrary(string folder, IRecycleBin recycleBin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = System.IO.Path.GetFullPath(folder);
        this.recycleBin = recycleBin;
    }

    /// <summary>Папка по умолчанию: Документы\Xoard\Sounds.</summary>
    public static string DefaultFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Xoard", "Sounds");

    /// <summary>Папка звуков.</summary>
    public string Folder { get; }

    /// <summary>Формат поддерживается.</summary>
    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Звуки в папке: сначала в сохранённом порядке, новые — в конце по алфавиту.
    /// </summary>
    public IReadOnlyList<SoundFile> Scan(IReadOnlyList<string> order)
    {
        Directory.CreateDirectory(Folder);
        var files = Directory.EnumerateFiles(Folder)
            .Where(IsSupported)
            .Select(p => new SoundFile(p, System.IO.Path.GetFileNameWithoutExtension(p)))
            .ToList();

        var rank = order
            .Select((name, index) => (name, index))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

        return files
            .OrderBy(f => rank.TryGetValue(f.FileName, out var i) ? i : int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Копирует файлы в папку звуков (оригиналы остаются). Неподдерживаемые пропускаются;
    /// совпадающие имена получают суффикс « (2)». Файлы, уже лежащие в папке, не копируются.
    /// </summary>
    public (IReadOnlyList<SoundFile> Added, IReadOnlyList<string> Skipped) Import(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(Folder);
        var added = new List<SoundFile>();
        var skipped = new List<string>();

        foreach (var source in paths)
        {
            if (!IsSupported(source) || !File.Exists(source))
            {
                skipped.Add(source);
                continue;
            }

            var full = System.IO.Path.GetFullPath(source);
            if (string.Equals(System.IO.Path.GetDirectoryName(full), Folder, StringComparison.OrdinalIgnoreCase))
            {
                added.Add(new SoundFile(full, System.IO.Path.GetFileNameWithoutExtension(full)));
                continue;
            }

            var target = UniquePath(System.IO.Path.GetFileNameWithoutExtension(full), System.IO.Path.GetExtension(full));
            try
            {
                File.Copy(full, target);
                added.Add(new SoundFile(target, System.IO.Path.GetFileNameWithoutExtension(target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(source);
            }
        }

        return (added, skipped);
    }

    /// <summary>Переименовывает звук (файл). Расширение сохраняется.</summary>
    public SoundFile Rename(SoundFile file, string newName)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = (newName ?? "").Trim();
        if (name.Length == 0)
            throw new SoundLibraryException("Имя не может быть пустым.");
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new SoundLibraryException("В имени есть недопустимые символы: \\ / : * ? \" < > |");
        if (name is "." or ".." || name.EndsWith('.'))
            throw new SoundLibraryException("Имя не может заканчиваться точкой.");

        var target = System.IO.Path.Combine(Folder, name + System.IO.Path.GetExtension(file.Path));
        if (string.Equals(target, file.Path, StringComparison.Ordinal))
            return file;

        // Смена только регистра — разрешена (тот же файл в Windows)
        var sameFile = string.Equals(target, file.Path, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && File.Exists(target))
            throw new SoundLibraryException($"Звук «{name}» уже есть.");

        try
        {
            File.Move(file.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SoundLibraryException($"Не удалось переименовать: {ex.Message}", ex);
        }

        return new SoundFile(target, name);
    }

    /// <summary>Удаляет звук в корзину.</summary>
    public void Delete(SoundFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            recycleBin.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new SoundLibraryException($"Не удалось удалить «{file.Name}»: {ex.Message}", ex);
        }
    }

    private string UniquePath(string name, string extension)
    {
        var candidate = System.IO.Path.Combine(Folder, name + extension);
        for (var i = 2; File.Exists(candidate); i++)
            candidate = System.IO.Path.Combine(Folder, $"{name} ({i}){extension}");
        return candidate;
    }
}

/// <summary>Корзина Windows (SHFileOperation с возможностью восстановления).</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <inheritdoc />
    public void Delete(string path)
    {
        if (!File.Exists(path))
            return;

        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",   // список путей, заканчивается двойным нулём
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.AnyOperationsAborted)
            throw new IOException($"корзина вернула код 0x{result:X}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

#pragma warning disable SYSLIB1054 // строковые поля структуры: классический DllImport проще генератора
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
#pragma warning restore SYSLIB1054
}
