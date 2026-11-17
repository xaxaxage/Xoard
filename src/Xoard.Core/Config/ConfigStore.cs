using System.Text.Json;
using System.Text.Json.Serialization;
using Xoard.Core.Audio;

namespace Xoard.Core.Config;

/// <summary>Положение и размер окна.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Все настройки приложения (то, что сохраняется в settings.json).</summary>
public sealed record AppConfig
{
    /// <summary>Версия формата файла.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Звук: устройства, громкости, режимы.</summary>
    public AudioSettings Audio { get; init; } = AudioSettings.Default;

    /// <summary>Порядок кнопок: имена файлов в папке звуков.</summary>
    public IReadOnlyList<string> SoundOrder { get; init; } = [];

    /// <summary>Горячие клавиши звуков: имя файла → сочетание («Ctrl+Alt+1»).</summary>
    public IReadOnlyDictionary<string, string> SoundHotkeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Горячая клавиша «Стоп всё» (null — не назначена).</summary>
    public string? StopAllHotkey { get; init; }

    /// <summary>Горячая клавиша «Начать/остановить запись».</summary>
    public string? RecordHotkey { get; init; }

    /// <summary>Горячая клавиша «Сохранить повтор».</summary>
    public string? ReplayHotkey { get; init; }

    /// <summary>Повтор: постоянно хранить последние N секунд звука компьютера.</summary>
    public bool ReplayEnabled { get; init; } = true;

    /// <summary>Длина буфера повтора, секунд (10–120).</summary>
    public int ReplaySeconds { get; init; } = 30;

    /// <summary>Источник записи: System (звук компьютера) или Microphone.</summary>
    public Xoard.Core.Recording.RecordingSource RecordSource { get; init; } = Xoard.Core.Recording.RecordingSource.System;

    /// <summary>Анимации интерфейса (выключаются и при отключённых анимациях Windows).</summary>
    public bool AnimationsEnabled { get; init; } = true;

    /// <summary>Палитра: auto (по теме Windows), xoard, xoard-night, harbor, … (см. Palettes в приложении).</summary>
    public string Palette { get; init; } = "auto";

    /// <summary>Форма: strict (почти прямые углы, тонкие линии) или soft (скруглённые карточки).</summary>
    public string Shape { get; init; } = "strict";

    /// <summary>Раскладка главного окна: pult, dock или stream.</summary>
    public string Layout { get; init; } = "pult";

    /// <summary>Шутливые подписи под заголовком и в уведомлениях.</summary>
    public bool HumorEnabled { get; init; } = true;

    /// <summary>Кнопка ✕ прячет окно в трей (программа продолжает работать), а не закрывает её.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Подсказка «Xoard работает в трее» уже показывалась.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Окно «Как настроить Discord» уже открывалось при первом запуске.</summary>
    public bool SetupGuideShown { get; init; }

    /// <summary>Последнее положение окна.</summary>
    public WindowPlacement? Window { get; init; }
}

/// <summary>
/// Хранение настроек в JSON (%LOCALAPPDATA%\Xoard\settings.json). Запись атомарная
/// (временный файл + замена), испорченный файл откладывается в сторону, и берутся настройки по умолчанию.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock gate = new();

    public ConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Путь по умолчанию.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xoard", "settings.json");

    /// <summary>Путь к файлу настроек.</summary>
    public string Path { get; }

    /// <summary>Сообщение о проблеме при последней загрузке (файл был испорчен), иначе null.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Загружает настройки. Нет файла — настройки по умолчанию; испорчен — откладывает его и берёт умолчания.</summary>
    public AppConfig Load()
    {
        lock (gate)
        {
            LoadWarning = null;
            if (!File.Exists(Path))
                return new AppConfig();

            try
            {
                var json = File.ReadAllText(Path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
                config.Audio.Validate();
                return config with
                {
                    SoundOrder = config.SoundOrder ?? [],
                    SoundHotkeys = config.SoundHotkeys ?? new Dictionary<string, string>(),
                };
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or NotSupportedException or InvalidOperationException)
            {
                var backup = $"{Path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                try
                {
                    File.Copy(Path, backup, overwrite: true);
                }
                catch (IOException)
                {
                    // Не смогли сохранить копию — всё равно продолжаем с умолчаниями
                }

                LoadWarning = $"Файл настроек повреждён, загружены настройки по умолчанию. Копия: {backup}";
                return new AppConfig();
            }
        }
    }

    /// <summary>Сохраняет настройки атомарно.</summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (gate)
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, Path, overwrite: true);
        }
    }
}
