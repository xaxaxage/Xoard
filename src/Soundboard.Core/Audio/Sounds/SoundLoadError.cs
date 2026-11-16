namespace Soundboard.Core.Audio.Sounds;

/// <summary>Почему звук не удалось загрузить.</summary>
public enum SoundLoadError
{
    /// <summary>Файла нет (или путь указывает на папку).</summary>
    FileNotFound,

    /// <summary>Нет прав на чтение или файл заблокирован другой программой.</summary>
    AccessDenied,

    /// <summary>Файл не открывается как звук: неизвестный формат или повреждён заголовок.</summary>
    InvalidFormat,

    /// <summary>Файл открылся, но декодирование оборвалось ошибкой (повреждённые данные).</summary>
    DecodeFailed,

    /// <summary>Звук длиннее допустимого (<see cref="SoundDecoderOptions.MaxSoundDuration"/>).</summary>
    TooLong,

    /// <summary>В файле нет ни одного звукового кадра.</summary>
    Empty,

    /// <summary>Media Foundation недоступен (например, Windows N без Media Feature Pack).</summary>
    MediaFoundationUnavailable,

    /// <summary>Не хватило памяти, чтобы держать звук целиком.</summary>
    OutOfMemory,
}

/// <summary>
/// Ошибка загрузки звука: ожидаемая ситуация времени выполнения (битый файл, нет файла и т. п.).
/// <see cref="Exception.Message"/> — текст для пользователя на русском.
/// </summary>
public sealed class SoundLoadException : Exception
{
    /// <summary>Создаёт исключение с кодом <see cref="SoundLoadError.DecodeFailed"/>.</summary>
    public SoundLoadException()
        : this(SoundLoadError.DecodeFailed, string.Empty)
    {
    }

    /// <summary>Создаёт исключение с кодом <see cref="SoundLoadError.DecodeFailed"/> и заданным текстом.</summary>
    public SoundLoadException(string message)
        : base(message)
    {
        Error = SoundLoadError.DecodeFailed;
        FilePath = string.Empty;
    }

    /// <summary>Создаёт исключение с кодом <see cref="SoundLoadError.DecodeFailed"/>, текстом и причиной.</summary>
    public SoundLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = SoundLoadError.DecodeFailed;
        FilePath = string.Empty;
    }

    /// <summary>Создаёт исключение с кодом ошибки; текст строится автоматически.</summary>
    /// <param name="error">Код ошибки.</param>
    /// <param name="filePath">Путь к файлу.</param>
    /// <param name="detail">Уточнение (например, длительность), добавляется к тексту.</param>
    /// <param name="innerException">Исходное исключение.</param>
    public SoundLoadException(SoundLoadError error, string filePath, string? detail = null, Exception? innerException = null)
        : base(BuildMessage(error, filePath, detail), innerException)
    {
        Error = error;
        FilePath = filePath ?? string.Empty;
    }

    /// <summary>Код ошибки.</summary>
    public SoundLoadError Error { get; }

    /// <summary>Путь к файлу, который не удалось загрузить.</summary>
    public string FilePath { get; }

    /// <summary>Текст для пользователя (без пути).</summary>
    public static string Describe(SoundLoadError error) => error switch
    {
        SoundLoadError.FileNotFound => "Файл не найден",
        SoundLoadError.AccessDenied => "Нет доступа к файлу (нет прав или файл открыт другой программой)",
        SoundLoadError.InvalidFormat => "Файл повреждён или его формат не поддерживается",
        SoundLoadError.DecodeFailed => "Ошибка при декодировании файла (данные повреждены)",
        SoundLoadError.TooLong => "Звук слишком длинный",
        SoundLoadError.Empty => "В файле нет звука",
        SoundLoadError.MediaFoundationUnavailable =>
            "Media Foundation недоступен: установите Media Feature Pack (Windows N) или используйте WAV",
        SoundLoadError.OutOfMemory => "Недостаточно памяти, чтобы загрузить звук",
        _ => "Не удалось загрузить звук",
    };

    private static string BuildMessage(SoundLoadError error, string? filePath, string? detail)
    {
        string text = Describe(error);
        if (!string.IsNullOrEmpty(detail))
        {
            text += $" ({detail})";
        }
        return string.IsNullOrEmpty(filePath) ? text : $"{text}: {filePath}";
    }
}
