using Soundboard.Core.Audio.Mic;

namespace Soundboard.Core.Audio;

/// <summary>Как ведёт себя повторный запуск того же звука.</summary>
public enum PlaybackMode
{
    /// <summary>Новый запуск накладывается на уже играющие копии звука.</summary>
    Overlap,

    /// <summary>Играющие копии того же звука плавно (5 мс) останавливаются, звук начинается сначала.</summary>
    Restart,
}

/// <summary>Набор размеров буферов (задержка против устойчивости к нагрузке).</summary>
public enum LatencyProfile
{
    /// <summary>Обычный: кабель 20 мс, наушники 30 мс, цель буфера микрофона 30 мс.</summary>
    Default,

    /// <summary>Надёжный: кабель 50 мс, наушники 60 мс, микрофон 50 мс — для слабых или нагруженных ПК.</summary>
    Safe,

    /// <summary>Экспериментальный: IAudioClient3 (малый период) на CABLE Input.</summary>
    Low,
}

/// <summary>
/// Пользовательские настройки звука. Неизменяемая запись: изменения делаются через <c>with</c>.
/// Технические параметры (буферы, лимитер, кэш) живут отдельно, в опциях движка.
/// </summary>
public sealed record AudioSettings
{
    /// <summary>Настройки по умолчанию.</summary>
    public static AudioSettings Default { get; } = new();

    /// <summary>Id конечной точки наушников; <c>null</c> — следовать устройству вывода Windows по умолчанию.</summary>
    public string? HeadphonesDeviceId { get; init; }

    /// <summary>
    /// Выводить ли звуки в наушники. <c>false</c> — шина наушников не строится (звуки идут только
    /// в VB-CABLE); нужно самопроверке для теста изоляции (ревью M8).
    /// </summary>
    public bool HeadphonesEnabled { get; init; } = true;

    /// <summary>Id конечной точки микрофона; <c>null</c> — микрофон Windows по умолчанию.</summary>
    public string? MicDeviceId { get; init; }

    /// <summary>Передавать ли голос с микрофона в VB-CABLE. <c>false</c> останавливает захват.</summary>
    public bool MicPassthroughEnabled { get; init; } = true;

    /// <summary>Мягкое выключение микрофона: захват продолжается, усиление плавно уходит в 0.</summary>
    public bool MicMuted { get; init; }

    /// <summary>Как многоканальный микрофон сводится в моно.</summary>
    public MicChannelMode MicChannelMode { get; init; } = MicChannelMode.Average;

    /// <summary>Громкость в наушниках, положение ползунка 0..1 (кубическая кривая, 1 = 0 дБ).</summary>
    public float HeadphonesLevel { get; init; } = 0.8f;

    /// <summary>Громкость звуков в VB-CABLE (то, что слышат друзья), 0..1.</summary>
    public float CableLevel { get; init; } = 1f;

    /// <summary>Громкость голоса в VB-CABLE, 0..1.</summary>
    public float MicLevel { get; init; } = 1f;

    /// <summary>Наложение или перезапуск при повторном нажатии.</summary>
    public PlaybackMode PlaybackMode { get; init; } = PlaybackMode.Overlap;

    /// <summary>Профиль задержки.</summary>
    public LatencyProfile LatencyProfile { get; init; } = LatencyProfile.Default;

    /// <summary>
    /// Проверяет значения и бросает <see cref="ArgumentOutOfRangeException"/> при ошибке
    /// (уровни вне 0..1 или NaN, неизвестные значения перечислений).
    /// Пустой или пробельный id устройства ошибкой не считается: это то же, что <c>null</c>.
    /// </summary>
    public void Validate()
    {
        ValidateLevel(HeadphonesLevel, nameof(HeadphonesLevel));
        ValidateLevel(CableLevel, nameof(CableLevel));
        ValidateLevel(MicLevel, nameof(MicLevel));
        ValidateEnum(MicChannelMode, nameof(MicChannelMode));
        ValidateEnum(PlaybackMode, nameof(PlaybackMode));
        ValidateEnum(LatencyProfile, nameof(LatencyProfile));
    }

    /// <summary>Id наушников без пустых строк: пустой/пробельный id означает «по умолчанию».</summary>
    internal string? EffectiveHeadphonesDeviceId => Normalize(HeadphonesDeviceId);

    /// <summary>Id микрофона без пустых строк: пустой/пробельный id означает «по умолчанию».</summary>
    internal string? EffectiveMicDeviceId => Normalize(MicDeviceId);

    private static string? Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    private static void ValidateLevel(float value, string name)
    {
        // NaN не проходит ни одно сравнение, поэтому проверка записана «от противного».
        if (!(value >= 0f && value <= 1f))
            throw new ArgumentOutOfRangeException(name, value, "Уровень должен быть в диапазоне 0..1.");
    }

    private static void ValidateEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(name, value, "Неизвестное значение.");
    }
}
