namespace Soundboard.Core.Audio.Devices;

/// <summary>
/// Id устройств Windows по умолчанию для каждой роли (<c>null</c> — для роли нет устройства).
/// Для ввода роли Console и Multimedia в Windows всегда совпадают, поэтому Multimedia не хранится.
/// </summary>
public sealed record DefaultEndpoints(
    string? RenderConsole,
    string? RenderMultimedia,
    string? RenderCommunications,
    string? CaptureConsole,
    string? CaptureCommunications)
{
    /// <summary>Ни одного устройства по умолчанию.</summary>
    public static DefaultEndpoints None { get; } = new(null, null, null, null, null);

    /// <summary>Одинаковые устройства для всех ролей (удобно для тестов и простых конфигураций).</summary>
    public static DefaultEndpoints Same(string? render, string? capture) => new(render, render, render, capture, capture);
}
