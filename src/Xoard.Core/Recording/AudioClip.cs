using Xoard.Core.Audio;

namespace Xoard.Core.Recording;

/// <summary>
/// Записанный кусок звука в памяти (стерео float, 48 кГц) — ещё не файл. Из окна запись и повтор
/// сначала открываются в редакторе: сохранить целиком, только выделенное или выбросить.
/// </summary>
/// <param name="Stereo">Отсчёты: левый, правый, левый, … (48 кГц).</param>
/// <param name="SuggestedName">Имя по умолчанию («Запись 21-14-05», «Повтор 21-14-05»).</param>
public sealed record AudioClip(float[] Stereo, string SuggestedName)
{
    public double Seconds => Stereo.Length / 2.0 / AudioFormat.SampleRate;

    /// <summary>
    /// Сохранить кусок [start, end) секунд как WAV в папку (имя занято — «Имя (2)»).
    /// Возвращает путь.
    /// </summary>
    /// <exception cref="ArgumentException">Пустой или слишком короткий (меньше 20 мс) кусок.</exception>
    public string SaveAs(string folder, string name, double startSeconds, double endSeconds)
    {
        var piece = startSeconds <= 0 && endSeconds >= Seconds ? Stereo : AudioClips.Slice(Stereo, startSeconds, endSeconds);
        if (piece.Length < AudioFormat.SampleRate / 50 * 2)
            throw new ArgumentException("Выделен слишком короткий кусок — нужно хотя бы 20 мс.");
        var path = AudioClips.UniqueWavPath(folder, string.IsNullOrWhiteSpace(name) ? SuggestedName : name.Trim());
        AudioClips.WriteWav(path, piece, AudioFormat.SampleRate);
        return path;
    }
}
