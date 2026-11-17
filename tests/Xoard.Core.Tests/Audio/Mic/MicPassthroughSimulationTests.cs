using System.Runtime.InteropServices;
using NAudio.Wave;
using Xoard.Core.Audio.Mic;

namespace Xoard.Core.Tests.Audio.Mic;

/// <summary>
/// Моделирование двух независимых часов: микрофон идёт на ±ppm от VB-CABLE, пакеты приходят
/// с задержкой и джиттером. Проверяем захват дрейфа, отсутствие недогрузок и непрерывность синуса.
/// </summary>
public class MicPassthroughSimulationTests
{
    private const long TicksPerSecond = 10_000_000;
    private const int Rate = 48_000;
    private const int PacketFrames = 480;
    private const double ToneHz = 440;
    private const float Amplitude = 0.5f;

    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    [InlineData(1000)]
    public void Locks_onto_clock_drift_without_underruns_or_discontinuities(double ppm)
    {
        var result = Simulate(ppm, seconds: 120, stallAtSecond: null);

        Assert.Equal(0, result.Underruns);
        Assert.Equal(0, result.Overflows);
        Assert.True(result.Locked, $"нет захвата; ppm={result.Ppm:F1}");
        Assert.InRange(result.Ppm, ppm - 15, ppm + 15);
        Assert.True(result.MaxResidual < 0.005, $"разрыв синуса: остаток {result.MaxResidual:F5}");
        Assert.InRange(result.MeanFillErrorMs, -1.0, 1.0);
    }

    [Fact]
    public void Recovers_after_capture_stall()
    {
        var result = Simulate(250, seconds: 120, stallAtSecond: 40);

        Assert.InRange(result.Underruns, 1, 2);
        Assert.True(result.Locked, $"нет захвата после сбоя; ppm={result.Ppm:F1}");
        Assert.True(result.MaxResidual < 0.005, $"разрыв синуса после восстановления: {result.MaxResidual:F5}");
    }

    private sealed record SimResult(long Underruns, long Overflows, bool Locked, double Ppm, double MaxResidual, double MeanFillErrorMs);

    private static SimResult Simulate(double ppm, int seconds, int? stallAtSecond)
    {
        long now = 0;
        var mic = new MicPassthrough(30, 20, () => now, TicksPerSecond);
        mic.Attach(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2), MicChannelMode.Average);
        mic.SetGain(1f, false);

        var producerRate = Rate * (1 + (ppm * 1e-6));
        var random = new Random(12345);
        var packetBytes = new byte[PacketFrames * 2 * sizeof(float)];
        var packetFloats = MemoryMarshal.Cast<byte, float>(packetBytes.AsSpan());
        long producerSample = 0;
        long packetIndex = 0;
        long lastArrival = 0;

        var bus = new float[PacketFrames * 2];
        var output = new List<float>(seconds * Rate);
        long callback = 0;
        var fillErrors = new List<double>();
        var analyzeFrom = (long)(Rate * 60);   // анализируем вторую половину (после захвата)
        var stallStart = stallAtSecond.HasValue ? stallAtSecond.Value * TicksPerSecond : long.MaxValue;
        var stallEnd = stallStart + (TicksPerSecond / 20);   // 50 мс без пакетов

        long NextArrival()
        {
            var ready = (long)((packetIndex + 1) * PacketFrames / producerRate * TicksPerSecond);
            var lag = (long)(0.015 * TicksPerSecond) + (long)(random.NextDouble() * 0.003 * TicksPerSecond);
            var arrival = Math.Max(lastArrival, ready + lag);
            if (arrival >= stallStart && arrival < stallEnd)
                arrival = stallEnd;   // зависание захвата: пакеты приходят пачкой после паузы
            return arrival;
        }

        var nextPacket = NextArrival();
        var end = seconds * TicksPerSecond;
        while (true)
        {
            var nextRender = callback * TicksPerSecond * PacketFrames / Rate;
            if (nextRender >= end)
                break;

            if (nextPacket <= nextRender)
            {
                now = nextPacket;
                for (var f = 0; f < PacketFrames; f++)
                {
                    var v = Amplitude * (float)Math.Sin(2 * Math.PI * ToneHz * producerSample++ / Rate);
                    packetFloats[2 * f] = v;
                    packetFloats[(2 * f) + 1] = v;
                }

                mic.OnPacket(packetBytes, NAudio.CoreAudioApi.AudioClientBufferFlags.None);
                lastArrival = now;
                packetIndex++;
                nextPacket = NextArrival();
            }
            else
            {
                now = nextRender;
                Array.Clear(bus);
                mic.MixInto(bus);
                for (var f = 0; f < PacketFrames; f++)
                    output.Add(bus[2 * f]);
                if (output.Count > analyzeFrom)
                    fillErrors.Add(mic.FillMs - 30);
                callback++;
            }
        }

        // Непрерывность: x[n] − 2cos(w)·x[n−1] + x[n−2] ≈ 0 для чистого синуса
        var w = 2 * Math.PI * ToneHz * (1 + (ppm * 1e-6)) / Rate;
        var c = 2 * Math.Cos(w);
        double maxResidual = 0;
        var from = (int)Math.Max(analyzeFrom, stallAtSecond.HasValue ? (stallAtSecond.Value + 15) * Rate : 0);
        for (var n = from + 2; n < output.Count; n++)
            maxResidual = Math.Max(maxResidual, Math.Abs(output[n] - (c * output[n - 1]) + output[n - 2]));

        return new SimResult(mic.Underruns, mic.Overflows, mic.Locked, mic.DriftPpm, maxResidual, fillErrors.Average());
    }
}
