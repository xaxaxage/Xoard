namespace Soundboard.Core.Audio.Sounds;

/// <summary>
/// Ссылка на загруженный звук, которую получают UI, пайп и MCP. Лёгкая и неизменяемая;
/// сами отсчёты живут в кэше (<see cref="SoundCache"/>).
/// </summary>
/// <param name="Key">
/// Ключ кэша: полный путь + размер + время изменения файла (см. <see cref="SoundFileStamp"/>).
/// Изменённый файл получает новый ключ. Для звуков из памяти — <c>memory:…</c>.
/// </param>
/// <param name="Path">Полный путь к файлу (для звуков из памяти — пустая строка).</param>
/// <param name="Name">Отображаемое имя (имя файла без расширения).</param>
/// <param name="Duration">Длительность декодированного звука.</param>
public sealed record SoundHandle(string Key, string Path, string Name, TimeSpan Duration)
{
    /// <summary>Звук создан в памяти (тестовые сигналы), а не загружен из файла.</summary>
    public bool IsInMemory => Key.StartsWith(SoundFileStamp.MemoryKeyPrefix, StringComparison.Ordinal);
}
