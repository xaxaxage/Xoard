using System.Runtime.CompilerServices;
using NAudio.Wave;
using Xoard.Core.Audio.Sounds;

namespace Xoard.Core.Audio.Voices;

/// <summary>Состояние отвода голоса на одной шине.</summary>
internal enum TapState
{
    Pending = 0,
    Playing = 1,
    Ended = 2,
}

/// <summary>
/// Один запуск звука («голос»). Звучит одновременно в наушниках и в VB-CABLE: у каждой шины свой
/// <see cref="VoiceTap"/> со своим курсором по общему массиву сэмплов, потому что устройства
/// идут от разных часов.
/// </summary>
internal sealed class Voice
{
    private int stopFadeFrames = -1;
    private int stopReason;

    public Voice(VoiceId id, SoundHandle handle, DecodedSound sound, float gain, DateTimeOffset startedAt)
    {
        Id = id;
        Handle = handle;
        Sound = sound;
        Gain = gain;
        CreatedAt = startedAt;
    }

    public VoiceId Id { get; }

    public SoundHandle Handle { get; }

    public DecodedSound Sound { get; }

    public float Gain { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Отводы по шинам: [0] — наушники, [1] — VB-CABLE.</summary>
    public VoiceTap?[] Taps { get; } = new VoiceTap?[2];

    // Состояние для событий: меняет только управляющий поток
    public bool StartedRaised { get; set; }

    public bool EndedRaised { get; set; }

    /// <summary>Запрошена остановка: число кадров затухания (≥ 0) или −1.</summary>
    public int StopFadeFrames => Volatile.Read(ref stopFadeFrames);

    public PlaybackEndReason StopReason => (PlaybackEndReason)Volatile.Read(ref stopReason);

    public bool StopRequested => StopFadeFrames >= 0;

    /// <summary>Попросить остановиться с затуханием (любой поток; повторный вызов игнорируется).</summary>
    public void RequestStop(int fadeFrames, PlaybackEndReason reason)
    {
        Volatile.Write(ref stopReason, (int)reason);
        Interlocked.CompareExchange(ref stopFadeFrames, Math.Max(0, fadeFrames), -1);
    }

    /// <summary>Все отводы закончили.</summary>
    public bool AllTapsEnded
    {
        get
        {
            foreach (var tap in Taps)
            {
                if (tap is not null && tap.State != TapState.Ended)
                    return false;
            }

            return true;
        }
    }

    public bool AnyTapPlaying
    {
        get
        {
            foreach (var tap in Taps)
            {
                if (tap is { State: TapState.Playing or TapState.Ended, WasAudible: true })
                    return true;
            }

            return false;
        }
    }

    /// <summary>Причина окончания: по первому отводу, у которого она не «естественная».</summary>
    public PlaybackEndReason EndReason
    {
        get
        {
            var reason = PlaybackEndReason.Completed;
            foreach (var tap in Taps)
            {
                if (tap is not null && tap.EndReason != PlaybackEndReason.Completed)
                    reason = tap.EndReason;
            }

            return reason;
        }
    }
}

/// <summary>
/// Отвод голоса на одну шину: читает общий массив сэмплов своим курсором. Вызывается только
/// звуковым потоком своей шины (через <c>MixingSampleProvider</c>). Короткое чтение = конец:
/// микшер сам удаляет вход.
/// </summary>
internal sealed class VoiceTap : ISampleProvider
{
    private readonly Voice voice;
    private readonly float[] samples;
    private int cursor;              // индекс сэмпла
    private int delaySamplesLeft;
    private int fadeTotal;           // кадров затухания; 0 — не затухаем
    private int fadePosition;
    private int state;
    private int endReason;
    private int wasAudible;

    public VoiceTap(Voice voice, int startDelayFrames = 0)
    {
        this.voice = voice;
        samples = voice.Sound.Samples;
        delaySamplesLeft = Math.Max(0, startDelayFrames) * AudioFormat.Channels;
    }

    public WaveFormat WaveFormat => AudioFormat.WaveFormat;

    public Voice Voice => voice;

    public TapState State => (TapState)Volatile.Read(ref state);

    public PlaybackEndReason EndReason => (PlaybackEndReason)Volatile.Read(ref endReason);

    /// <summary>Отвод хоть раз выдал звук.</summary>
    public bool WasAudible => Volatile.Read(ref wasAudible) != 0;

    /// <summary>Позиция в звуке, кадров.</summary>
    public int PositionFrames => Volatile.Read(ref cursor) / AudioFormat.Channels;

    /// <summary>Пометить отвод законченным снаружи (шина потеряна, ошибка). Только когда шина не читает.</summary>
    public void ForceEnd(PlaybackEndReason reason)
    {
        if (State == TapState.Ended)
            return;
        Volatile.Write(ref endReason, (int)reason);
        Volatile.Write(ref state, (int)TapState.Ended);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Read(Span<float> buffer)
    {
        if (state == (int)TapState.Ended)
            return 0;

        // Запрос остановки: начинаем затухание (или заканчиваем сразу, если ещё не звучали)
        if (fadeTotal == 0)
        {
            var stopFrames = voice.StopFadeFrames;
            if (stopFrames >= 0)
            {
                if (state == (int)TapState.Pending || stopFrames == 0)
                    return End(voice.StopReason);
                fadeTotal = stopFrames;
                fadePosition = 0;
            }
        }

        if (state == (int)TapState.Pending)
            Volatile.Write(ref state, (int)TapState.Playing);

        var written = 0;

        // Задержка старта (заготовка для push-to-talk): сначала тишина
        if (delaySamplesLeft > 0)
        {
            var silence = Math.Min(delaySamplesLeft, buffer.Length);
            buffer[..silence].Clear();
            delaySamplesLeft -= silence;
            written = silence;
            if (written == buffer.Length)
                return written;
        }

        var gain = voice.Gain;
        var remainingSound = samples.Length - cursor;
        var count = Math.Min(buffer.Length - written, remainingSound);
        if (fadeTotal > 0)
            count = Math.Min(count, (fadeTotal - fadePosition) * AudioFormat.Channels);

        var src = samples.AsSpan(cursor, count);
        var dst = buffer.Slice(written, count);
        if (fadeTotal > 0)
        {
            var frames = count / AudioFormat.Channels;
            for (var f = 0; f < frames; f++)
            {
                var g = gain * (1f - ((fadePosition + f + 1) / (float)fadeTotal));
                dst[2 * f] = src[2 * f] * g;
                dst[(2 * f) + 1] = src[(2 * f) + 1] * g;
            }

            fadePosition += frames;
        }
        else if (gain == 1f)
        {
            src.CopyTo(dst);
        }
        else
        {
            for (var i = 0; i < count; i++)
                dst[i] = src[i] * gain;
        }

        if (count > 0)
            Volatile.Write(ref wasAudible, 1);

        Volatile.Write(ref cursor, cursor + count);
        written += count;

        if (fadeTotal > 0 && fadePosition >= fadeTotal)
        {
            End(voice.StopReason);
            return written;   // короткое чтение (или полное) — следующий Read вернёт 0
        }

        if (cursor >= samples.Length)
        {
            End(PlaybackEndReason.Completed);
            return written;
        }

        return written;
    }

    private int End(PlaybackEndReason reason)
    {
        Volatile.Write(ref endReason, (int)reason);
        Volatile.Write(ref state, (int)TapState.Ended);
        return 0;
    }
}
