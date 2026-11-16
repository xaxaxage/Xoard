using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NAudio.Wave;

namespace Soundboard.Core.Audio.Mic;

/// <summary>
/// Чистое преобразование байтов пакета захвата (формат устройства) в стерео float с L = R.
/// <para>
/// Поддерживается: IEEE float 32 (обычный и WAVE_FORMAT_EXTENSIBLE), PCM 16, PCM 24 упакованный (3 байта),
/// PCM в 32-битном контейнере (включая 24 значащих бита, выровненных влево), любое число каналов
/// от 1 до <see cref="MaxChannels"/>. Каналы сводятся в моно по <see cref="Mode"/>, затем моно дублируется в L и R.
/// </para>
/// <para>
/// Потоки: <see cref="Convert"/> вызывается только из потока захвата; <see cref="Mode"/> можно менять из любого
/// потока (применяется со следующего пакета). Аллокаций на пути преобразования нет.
/// </para>
/// </summary>
internal sealed class CaptureConverter
{
    /// <summary>Максимальное поддерживаемое число каналов устройства.</summary>
    public const int MaxChannels = 32;

    private const float Pcm16Scale = 1f / 32768f;
    private const float Pcm24Scale = 1f / 8388608f;
    private const float Pcm32Scale = 1f / 2147483648f;

    private readonly int channels;
    private readonly int bytesPerFrame;
    private int mode;
    private long nonFiniteSamples;

    /// <summary>Создаёт преобразователь для формата устройства.</summary>
    /// <exception cref="NotSupportedException">Формат не поддерживается (текст причины — в сообщении).</exception>
    public CaptureConverter(WaveFormat sourceFormat, MicChannelMode mode = MicChannelMode.Average)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        if (!TryGetSampleFormat(sourceFormat, out var sampleFormat, out var error))
        {
            throw new NotSupportedException(error);
        }

        ValidateMode(mode);
        SourceFormat = sourceFormat;
        SampleFormat = sampleFormat;
        channels = sourceFormat.Channels;
        bytesPerFrame = sourceFormat.BlockAlign;
        this.mode = (int)mode;
    }

    /// <summary>Исходный формат устройства.</summary>
    public WaveFormat SourceFormat { get; }

    /// <summary>Распознанный формат сэмпла.</summary>
    public CaptureSampleFormat SampleFormat { get; }

    /// <summary>Число каналов устройства.</summary>
    public int SourceChannels => channels;

    /// <summary>Байт на кадр (все каналы) во входных данных.</summary>
    public int BytesPerFrame => bytesPerFrame;

    /// <summary>Сколько NaN/Inf-сэмплов float-устройства было заменено нулями (диагностика).</summary>
    public long NonFiniteSamples => Volatile.Read(ref nonFiniteSamples);

    /// <summary>Режим сведения каналов. Потокобезопасно; действует со следующего вызова <see cref="Convert"/>.</summary>
    public MicChannelMode Mode
    {
        get => (MicChannelMode)Volatile.Read(ref mode);
        set
        {
            ValidateMode(value);
            Volatile.Write(ref mode, (int)value);
        }
    }

    /// <summary>Число целых кадров в <paramref name="byteCount"/> байтах входа (хвост неполного кадра отбрасывается).</summary>
    public int FramesIn(int byteCount) => byteCount <= 0 ? 0 : byteCount / bytesPerFrame;

    /// <summary>
    /// Проверяет, поддерживается ли формат устройства, без исключений (для потока управления).
    /// </summary>
    public static bool TryGetSampleFormat(
        WaveFormat format,
        out CaptureSampleFormat sampleFormat,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(format);
        sampleFormat = default;

        if (format.Channels < 1 || format.Channels > MaxChannels)
        {
            error = $"Неподдерживаемое число каналов микрофона: {format.Channels} (допустимо 1–{MaxChannels}).";
            return false;
        }

        int bits = format.BitsPerSample;
        bool isFloat;
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                isFloat = true;
                break;
            case WaveFormatEncoding.Pcm:
                isFloat = false;
                break;
            case WaveFormatEncoding.Extensible when format is WaveFormatExtensible extensible:
                if (extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)
                {
                    isFloat = true;
                }
                else if (extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM)
                {
                    isFloat = false;
                    if (extensible.ValidBitsPerSample > bits)
                    {
                        error = $"Некорректный формат микрофона: значащих бит {extensible.ValidBitsPerSample} больше контейнера {bits}.";
                        return false;
                    }
                }
                else
                {
                    error = $"Неподдерживаемый подтип формата микрофона: {AudioMediaSubtypes.GetAudioSubtypeName(extensible.SubFormat)}.";
                    return false;
                }

                break;
            default:
                error = $"Неподдерживаемый формат микрофона: {format.Encoding}.";
                return false;
        }

        if (isFloat)
        {
            if (bits != 32)
            {
                error = $"Неподдерживаемый float-формат микрофона: {bits} бит (поддерживается только 32).";
                return false;
            }

            sampleFormat = CaptureSampleFormat.Float32;
        }
        else
        {
            switch (bits)
            {
                case 16:
                    sampleFormat = CaptureSampleFormat.Pcm16;
                    break;
                case 24:
                    sampleFormat = CaptureSampleFormat.Pcm24;
                    break;
                case 32:
                    sampleFormat = CaptureSampleFormat.Pcm32;
                    break;
                default:
                    error = $"Неподдерживаемая разрядность PCM микрофона: {bits} бит (поддерживается 16, 24, 32).";
                    return false;
            }
        }

        if (format.BlockAlign != format.Channels * (bits / 8))
        {
            error = $"Некорректное выравнивание кадра микрофона: BlockAlign {format.BlockAlign}, ожидалось {format.Channels * (bits / 8)}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Преобразует целые кадры из <paramref name="source"/> в интерливный стерео float (L = R) в начало
    /// <paramref name="stereoDestination"/>. Преобразуется min(кадров во входе, длина назначения / 2) кадров;
    /// хвост неполного кадра во входе игнорируется.
    /// </summary>
    /// <returns>Число записанных кадров (в назначение записано вдвое больше float).</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Convert(ReadOnlySpan<byte> source, Span<float> stereoDestination)
    {
        int frames = Math.Min(source.Length / bytesPerFrame, stereoDestination.Length / 2);
        if (frames <= 0)
        {
            return 0;
        }

        // Какой канал брать: -1 — среднее всех каналов.
        int pick = (MicChannelMode)Volatile.Read(ref mode) switch
        {
            MicChannelMode.Left => 0,
            MicChannelMode.Right => channels > 1 ? 1 : 0,
            _ => channels == 1 ? 0 : -1,
        };

        var src = source[..(frames * bytesPerFrame)];
        var dst = stereoDestination[..(frames * 2)];
        switch (SampleFormat)
        {
            case CaptureSampleFormat.Float32:
                int bad = ConvertCore<Float32Reader>(src, dst, frames, channels, pick);
                if (bad != 0)
                {
                    Volatile.Write(ref nonFiniteSamples, nonFiniteSamples + bad);
                }

                break;
            case CaptureSampleFormat.Pcm16:
                ConvertCore<Pcm16Reader>(src, dst, frames, channels, pick);
                break;
            case CaptureSampleFormat.Pcm24:
                ConvertCore<Pcm24Reader>(src, dst, frames, channels, pick);
                break;
            default:
                ConvertCore<Pcm32Reader>(src, dst, frames, channels, pick);
                break;
        }

        return frames;
    }

    private static void ValidateMode(MicChannelMode value)
    {
        if (value is not (MicChannelMode.Average or MicChannelMode.Left or MicChannelMode.Right))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестный режим каналов микрофона.");
        }
    }

    /// <summary>Общий цикл; JIT специализирует его для каждого читателя-структуры (без виртуальных вызовов).</summary>
    /// <returns>Число заменённых нулём не-конечных сэмплов (только для float).</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ConvertCore<TReader>(ReadOnlySpan<byte> src, Span<float> dst, int frames, int channels, int pick)
        where TReader : struct, ISampleReader
    {
        int nonFinite = 0;
        if (pick >= 0)
        {
            for (int f = 0, s = pick; f < frames; f++, s += channels)
            {
                float v = TReader.Read(src, s);
                if (TReader.CanBeNonFinite && !float.IsFinite(v))
                {
                    v = 0f;
                    nonFinite++;
                }

                dst[2 * f] = v;
                dst[(2 * f) + 1] = v;
            }
        }
        else if (channels == 2)
        {
            for (int f = 0; f < frames; f++)
            {
                float a = TReader.Read(src, 2 * f);
                float b = TReader.Read(src, (2 * f) + 1);
                if (TReader.CanBeNonFinite)
                {
                    if (!float.IsFinite(a))
                    {
                        a = 0f;
                        nonFinite++;
                    }

                    if (!float.IsFinite(b))
                    {
                        b = 0f;
                        nonFinite++;
                    }
                }

                float v = (a + b) * 0.5f;
                dst[2 * f] = v;
                dst[(2 * f) + 1] = v;
            }
        }
        else
        {
            float inv = 1f / channels;
            for (int f = 0, s = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++, s++)
                {
                    float x = TReader.Read(src, s);
                    if (TReader.CanBeNonFinite && !float.IsFinite(x))
                    {
                        x = 0f;
                        nonFinite++;
                    }

                    sum += x;
                }

                float v = sum * inv;
                dst[2 * f] = v;
                dst[(2 * f) + 1] = v;
            }
        }

        return nonFinite;
    }

    /// <summary>Чтение одного сэмпла по индексу (с учётом каналов) из сырых байтов, нормировано к ±1.</summary>
    private interface ISampleReader
    {
        static abstract bool CanBeNonFinite { get; }

        static abstract float Read(ReadOnlySpan<byte> src, int sampleIndex);
    }

    private readonly struct Float32Reader : ISampleReader
    {
        public static bool CanBeNonFinite => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Read(ReadOnlySpan<byte> src, int sampleIndex) =>
            BinaryPrimitives.ReadSingleLittleEndian(src.Slice(sampleIndex * 4, 4));
    }

    private readonly struct Pcm16Reader : ISampleReader
    {
        public static bool CanBeNonFinite => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Read(ReadOnlySpan<byte> src, int sampleIndex) =>
            BinaryPrimitives.ReadInt16LittleEndian(src.Slice(sampleIndex * 2, 2)) * Pcm16Scale;
    }

    private readonly struct Pcm24Reader : ISampleReader
    {
        public static bool CanBeNonFinite => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Read(ReadOnlySpan<byte> src, int sampleIndex)
        {
            var b = src.Slice(sampleIndex * 3, 3);
            int v = b[0] | (b[1] << 8) | ((sbyte)b[2] << 16);
            return v * Pcm24Scale;
        }
    }

    private readonly struct Pcm32Reader : ISampleReader
    {
        public static bool CanBeNonFinite => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Read(ReadOnlySpan<byte> src, int sampleIndex) =>
            BinaryPrimitives.ReadInt32LittleEndian(src.Slice(sampleIndex * 4, 4)) * Pcm32Scale;
    }
}
