using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Xoard.Poc;

/// <summary>Единый внутренний формат: 48 кГц, float, стерео. Всё приводится к нему.</summary>
internal static class InternalFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    public static readonly WaveFormat WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>Приводит любой float-источник к 48 кГц / стерео.
    /// Порядок выбран так, чтобы ресемплировать как можно меньше каналов.</summary>
    public static ISampleProvider Convert(ISampleProvider source)
    {
        var provider = source;

        // >2 каналов (5.1, 7.1 …): оставляем первые два (Front L / Front R)
        if (provider.WaveFormat.Channels > 2)
        {
            provider = new MultiplexingSampleProvider([provider], 2);
        }

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        return provider;
    }

    public static string Describe(WaveFormat format)
    {
        var kind = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "float",
            WaveFormatEncoding.Pcm => "PCM",
            WaveFormatEncoding.Extensible => format is WaveFormatExtensible ext && ext.SubFormat == WaveSubFormats.IeeeFloat ? "float" : "PCM",
            _ => format.Encoding.ToString(),
        };
        return $"{format.SampleRate} Гц, {format.Channels} кан, {format.BitsPerSample} бит {kind}";
    }
}

/// <summary>GUID подформатов WAVE_FORMAT_EXTENSIBLE (KSDATAFORMAT_SUBTYPE_*).</summary>
internal static class WaveSubFormats
{
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
}
