namespace Soundboard.Core.Audio.Mic;

/// <summary>Распознанный формат сэмплов захвата (контейнер одного сэмпла).</summary>
internal enum CaptureSampleFormat
{
    /// <summary>IEEE float 32 бита (обычный или WAVE_FORMAT_EXTENSIBLE с подтипом IEEE_FLOAT).</summary>
    Float32,

    /// <summary>Целый PCM 16 бит, little-endian.</summary>
    Pcm16,

    /// <summary>Целый PCM 24 бита, упакованный в 3 байта, little-endian.</summary>
    Pcm24,

    /// <summary>Целый PCM в 32-битном контейнере (в т. ч. 24 значащих бита, выровненных влево).</summary>
    Pcm32,
}
