namespace Xoard.Core.Audio.Status;

/// <summary>
/// Одна проблема в статусе движка. Ожидаемые ситуации (нет устройства, нет VB-CABLE, битый файл)
/// сообщаются так, а не исключениями. Обычно создаётся через <see cref="IssueText.Create"/>.
/// </summary>
/// <param name="Code">Код проблемы.</param>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Message">Текст для пользователя (по-русски).</param>
/// <param name="DeviceId">Id конечной точки, к которой относится проблема, если есть.</param>
/// <param name="HelpUrl">Ссылка с инструкцией (например, загрузка VB-CABLE).</param>
/// <param name="HResult">HRESULT ошибки Windows, если проблема вызвана ею.</param>
public sealed record EngineIssue(
    IssueCode Code,
    IssueSeverity Severity,
    string Message,
    string? DeviceId = null,
    string? HelpUrl = null,
    int? HResult = null);
