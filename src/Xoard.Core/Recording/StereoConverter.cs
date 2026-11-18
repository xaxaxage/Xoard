using NAudio.Wave;

namespace Xoard.Core.Recording;

/// <summary>Байты захвата (float32 / PCM 16/24/32, любые каналы) → стерео float.</summary>
internal static class StereoConverter
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static int FramesIn(int bytes, WaveFormat format) => format.BlockAlign == 0 ? 0 : bytes / format.BlockAlign;

    public static void Convert(ReadOnlySpan<byte> source, WaveFormat format, Span<float> stereo)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        var bytesPerSample = format.BitsPerSample / 8;
        var channels = format.Channels;
        var frames = FramesIn(source.Length, format);
        for (var f = 0; f < frames; f++)
        {
            var o = f * format.BlockAlign;
            var left = Sample(source, o, isFloat, bytesPerSample);
            var right = channels > 1 ? Sample(source, o + bytesPerSample, isFloat, bytesPerSample) : left;
            stereo[2 * f] = left;
            stereo[(2 * f) + 1] = right;
        }
    }

    private static float Sample(ReadOnlySpan<byte> b, int o, bool isFloat, int bytes) => (isFloat, bytes) switch
    {
        (true, 4) => BitConverter.ToSingle(b.Slice(o, 4)),
        (false, 2) => BitConverter.ToInt16(b.Slice(o, 2)) / 32768f,
        (false, 3) => (b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16)) / 8388608f,
        (false, 4) => BitConverter.ToInt32(b.Slice(o, 4)) / 2147483648f,
        _ => 0f,
    };
}
