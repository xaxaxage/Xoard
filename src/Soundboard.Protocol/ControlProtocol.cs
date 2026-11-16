using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soundboard.Protocol;

/// <summary>
/// Протокол управления Soundboard: запрос/ответ JSON, по одному объекту в строке (UTF-8).
/// Транспорт v1 — named pipe «Soundboard.Control» (только для текущего пользователя Windows);
/// тот же протокол подойдёт и для внешнего (облачного) канала.
/// </summary>
public static class ControlProtocol
{
    /// <summary>Имя канала.</summary>
    public const string PipeName = "Soundboard.Control";

    /// <summary>Максимальный размер одного сообщения.</summary>
    public const int MaxMessageBytes = 1 << 20;

    /// <summary>Настройки JSON протокола: camelCase, перечисления строками.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Имена команд.</summary>
public static class ControlCommands
{
    public const string ListSounds = "list_sounds";
    public const string PlaySound = "play_sound";
    public const string StopAll = "stop_all";
    public const string SetVolume = "set_volume";
    public const string GetStatus = "get_status";
}

/// <summary>Коды ошибок.</summary>
public static class ControlErrorCodes
{
    /// <summary>Нет такого звука.</summary>
    public const string NotFound = "not_found";

    /// <summary>Под имя подходит несколько звуков.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Неверные аргументы.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>Неизвестная команда.</summary>
    public const string UnknownCommand = "unknown_command";

    /// <summary>Сейчас невозможно (нет устройства, движок не запущен).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Внутренняя ошибка.</summary>
    public const string Internal = "internal";
}

/// <summary>Запрос.</summary>
public sealed record ControlRequest(string Id, string Command, JsonElement? Args = null);

/// <summary>Ошибка в ответе (сообщение — для человека, на русском).</summary>
public sealed record ControlError(string Code, string Message);

/// <summary>Ответ.</summary>
public sealed record ControlResponse(string Id, bool Ok, JsonElement? Result = null, ControlError? Error = null)
{
    public static ControlResponse Success(string id, object? result) =>
        new(id, true, result is null ? null : JsonSerializer.SerializeToElement(result, ControlProtocol.Json));

    public static ControlResponse Failure(string id, string code, string message) => new(id, false, null, new ControlError(code, message));
}

/// <summary>Ошибка выполнения команды с кодом протокола.</summary>
public sealed class ControlException : Exception
{
    public ControlException()
        : this(ControlErrorCodes.Internal, "Ошибка")
    {
    }

    public ControlException(string message)
        : this(ControlErrorCodes.Internal, message)
    {
    }

    public ControlException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ControlErrorCodes.Internal;
    }

    public ControlException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

// ───────── аргументы и результаты ─────────

/// <summary>Аргументы play_sound: имя (точное или часть) или id (имя файла).</summary>
public sealed record PlaySoundArgs(string? Name = null, string? Id = null, bool PreviewOnly = false);

/// <summary>Аргументы set_volume: target = headphones | cable | mic, percent = 0..100.</summary>
public sealed record SetVolumeArgs(string Target, double Percent);

/// <summary>Звук.</summary>
public sealed record SoundInfo(string Id, string Name, double DurationSeconds, string? Hotkey, bool IsPlaying, bool Loaded, string? Error);

/// <summary>Результат list_sounds.</summary>
public sealed record SoundList(IReadOnlyList<SoundInfo> Sounds);

/// <summary>Результат play_sound.</summary>
public sealed record PlayInfo(string Id, string Name, double DurationSeconds, bool PreviewOnly);

/// <summary>Громкости, проценты 0..100.</summary>
public sealed record VolumeInfo(double Headphones, double Cable, double Mic, bool MicMuted);

/// <summary>Устройство вывода в статусе.</summary>
public sealed record OutputInfo(string State, string? Device);

/// <summary>Голос с микрофона в статусе.</summary>
public sealed record MicInfo(bool Enabled, bool Muted, bool Capturing, string? Device);

/// <summary>Что играет.</summary>
public sealed record PlayingInfo(string Name, double PositionSeconds, double DurationSeconds);

/// <summary>Результат get_status.</summary>
public sealed record StatusInfo(
    string Engine,
    bool VbCableInstalled,
    string? VbCableInput,
    string? VbCableOutput,
    IReadOnlyList<string> ListeningApps,
    OutputInfo Headphones,
    OutputInfo Cable,
    MicInfo Mic,
    VolumeInfo Volumes,
    string Mode,
    IReadOnlyList<PlayingInfo> Playing,
    int SoundCount,
    IReadOnlyList<string> Issues);

/// <summary>
/// Команды саундборда, независимые от транспорта: их реализует приложение, а вызывают
/// сервер named pipe (сейчас) и внешний канал (позже). Ошибки — <see cref="ControlException"/>.
/// </summary>
public interface ISoundboardControl
{
    Task<SoundList> ListSoundsAsync(CancellationToken ct);

    Task<PlayInfo> PlaySoundAsync(PlaySoundArgs args, CancellationToken ct);

    Task StopAllAsync(CancellationToken ct);

    Task<VolumeInfo> SetVolumeAsync(SetVolumeArgs args, CancellationToken ct);

    Task<StatusInfo> GetStatusAsync(CancellationToken ct);
}
