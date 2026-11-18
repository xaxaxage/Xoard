namespace Xoard.Core.Audio;

/// <summary>
/// Получатель голоса для записи: тот же голос, что уходит друзьям, — после шумоподавления и
/// выравнивания, но до ползунка громкости; при выключенном микрофоне — тишина.
/// </summary>
public interface IVoiceTap
{
    /// <summary>
    /// Поток захвата микрофона: блок стерео 48 кГц. <paramref name="delayFrames"/> — на сколько кадров
    /// голос уже задержан обработкой. Должен работать быстро и не бросать исключений.
    /// </summary>
    void OnVoice(ReadOnlySpan<float> stereo, int delayFrames);
}
