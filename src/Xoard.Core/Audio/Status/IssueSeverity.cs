namespace Xoard.Core.Audio.Status;

/// <summary>Насколько серьёзна проблема.</summary>
public enum IssueSeverity
{
    /// <summary>К сведению: всё работает, но пользователю полезно знать.</summary>
    Info,

    /// <summary>Работа ухудшена или есть риск (эхо, щелчки, тишина в наушниках).</summary>
    Warning,

    /// <summary>Основная функция не работает (друзья не слышат звуки или голос).</summary>
    Error,
}
