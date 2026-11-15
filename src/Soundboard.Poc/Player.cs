using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// В PoC сознательно используем классический WasapiOut (в NAudio 3 помечен устаревшим
// в пользу WasapiPlayerBuilder). Что брать для движка — решаем на этапе 1.
#pragma warning disable CS0618

namespace Soundboard.Poc;

/// <summary>Одна цель вывода: устройство + свой экземпляр источника звука.</summary>
internal sealed record PlaybackTarget(MMDevice Device, ISampleProvider Source);

/// <summary>Проигрывание на одно или несколько устройств через WasapiOut (shared-режим).</summary>
internal static class Player
{
    /// <summary>Играет до конца источников или до Ctrl+C. Возвращает false, если прервано.</summary>
    public static bool PlayToEnd(IReadOnlyList<PlaybackTarget> targets, int latencyMs, Action? onStarted = null,
        int preRollMs = 0, int postRollMs = 0)
    {
        if (preRollMs > 0 || postRollMs > 0)
        {
            // Тишина до и после звука: поток не должен остановиться, пока хвост звука ещё в буфере кабеля
            targets = [.. targets.Select(t => t with
            {
                Source = new OffsetSampleProvider(t.Source)
                {
                    DelayBy = TimeSpan.FromMilliseconds(preRollMs),
                    LeadOut = TimeSpan.FromMilliseconds(postRollMs),
                },
            })];
        }

        var outputs = new List<WasapiOut>(targets.Count);
        var stopped = new List<TaskCompletionSource>(targets.Count);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancelled.TrySetResult();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            foreach (var target in targets)
            {
                var output = new WasapiOut(target.Device, AudioClientShareMode.Shared, useEventSync: true, latencyMs);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception is null)
                    {
                        done.TrySetResult();
                    }
                    else
                    {
                        done.TrySetException(e.Exception);
                    }
                };
                output.Init(target.Source);
                outputs.Add(output);
                stopped.Add(done);
            }

            // Запускаем все выходы подряд, чтобы старт был почти одновременным
            foreach (var output in outputs)
            {
                output.Play();
            }

            onStarted?.Invoke();

            var all = Task.WhenAll(stopped.Select(s => s.Task));
            Task.WaitAny(all, cancelled.Task);

            if (all.IsFaulted)
            {
                throw all.Exception!.InnerException!;
            }

            return !cancelled.Task.IsCompleted;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            foreach (var output in outputs)
            {
                output.Stop();
                output.Dispose();
            }
        }
    }
}
