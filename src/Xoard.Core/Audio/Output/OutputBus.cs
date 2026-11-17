using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xoard.Core.Audio.Devices;
using Xoard.Core.Audio.Mic;

namespace Xoard.Core.Audio.Output;

/// <summary>
/// Жизненный цикл потока вывода одной шины (только управляющий поток движка).
/// Конвейер <see cref="BusRenderer"/> переживает пересборку потока: голоса продолжают звучать
/// после короткого сбоя. Поток следит за «тихим зависанием» (ревью M1): если устройство перестало
/// забирать данные, поток пересобирается.
/// </summary>
internal sealed class OutputBus
{
    private static readonly int[] RetryDelaysMs = [0, 100, 250, 500, 1000, 2000, 5000];
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<string> log;
    private readonly Action<BusId, int, Exception?> onStopped;
    private readonly float initialGain;
    private readonly int rampMs;
    private readonly MicPassthrough? mic;

    private WasapiPlayer? player;
    private MMDevice? device;
    private int generation;
    private long lastFrames;
    private long lastProgressTicks;
    private long acceptAfterTicks;
    private long nextRetryTicks;
    private int consecutiveFailures;
    private int state = (int)BusState.Detached;

    public OutputBus(BusId id, float initialGain, int rampMs, MicPassthrough? mic, Action<BusId, int, Exception?> onStopped, Action<string> log)
    {
        Id = id;
        this.initialGain = initialGain;
        this.rampMs = rampMs;
        this.mic = mic;
        this.onStopped = onStopped;
        this.log = log;
        Renderer = CreateRenderer();
    }

    public BusId Id { get; }

    /// <summary>Конвейер шины (меняется, только если старый поток не удалось остановить).</summary>
    public BusRenderer Renderer { get; private set; }

    public BusState State => (BusState)Volatile.Read(ref state);

    public DeviceChoice? Choice { get; private set; }

    public int BufferMs { get; private set; }

    public long Restarts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Последний сбой был «устройство занято монопольно».</summary>
    public bool DeviceInUse { get; private set; }

    /// <summary>Поток завис и был пересобран.</summary>
    public long Stalls { get; private set; }

    /// <summary>Можно добавлять голоса (поток работает и прошла пауза после старта).</summary>
    public bool AcceptsVoices => State == BusState.Running && Stopwatch.GetTimestamp() >= Volatile.Read(ref acceptAfterTicks);

    /// <summary>Громкость сессии Windows (микшер громкости, приглушение при звонках).</summary>
    public (float Volume, bool Muted) SessionVolume()
    {
        try
        {
            return player is null ? (1f, false) : (player.Volume, player.IsMuted);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NullReferenceException)
        {
            return (1f, false);
        }
    }

    /// <summary>Подключить шину к устройству (или переподключить с новым буфером).</summary>
    public void Attach(MmDeviceCatalog catalog, DeviceChoice choice, int bufferMs)
    {
        var sameDevice = Choice?.Device.HasId(choice.Device.Id) == true && BufferMs == bufferMs;
        Choice = choice;
        if (sameDevice && State is BusState.Running or BusState.Recovering)
            return;

        BufferMs = bufferMs;
        StopStream();
        consecutiveFailures = 0;
        StartStream(catalog);
    }

    /// <summary>Отключить шину: поток останавливается, голоса шины завершаются.</summary>
    public void Detach(PlaybackEndReason reason, Lock voicesLock)
    {
        lock (voicesLock)
            Volatile.Write(ref state, (int)BusState.Detached);
        StopStream();
        lock (voicesLock)
            Renderer.EndAllDetached(reason);
        Choice = null;
    }

    /// <summary>Пересобрать поток (проверка восстановления или новый буфер).</summary>
    public void Restart(MmDeviceCatalog catalog)
    {
        if (Choice is null)
            return;
        StopStream();
        StartStream(catalog);
    }

    /// <summary>Поток сообщил об остановке (через очередь управляющего потока).</summary>
    public void OnStreamStopped(int gen, Exception? error)
    {
        if (gen != generation || player is null)
            return;   // устаревшее поколение или намеренная остановка

        LastError = error?.Message ?? "поток вывода остановился";
        DeviceInUse = error?.GetType().Name.Contains("InUse", StringComparison.Ordinal) == true;
        log($"{Id}: поток остановился: {LastError}");
        StopStream();
        ScheduleRetry();
    }

    /// <summary>Периодическая проверка: зависание потока и повторные попытки.</summary>
    public void Tick(MmDeviceCatalog catalog, long now)
    {
        switch (State)
        {
            case BusState.Running:
                var frames = Renderer.FramesRendered;
                if (frames != lastFrames)
                {
                    lastFrames = frames;
                    lastProgressTicks = now;
                    consecutiveFailures = 0;
                }
                else if (TicksToMs(now - lastProgressTicks) > Math.Max(3 * BufferMs, 200))
                {
                    Stalls++;
                    LastError = "поток вывода завис (устройство перестало забирать звук)";
                    log($"{Id}: {LastError} — пересобираю");
                    StopStream();
                    ScheduleRetry();
                }

                break;

            case BusState.Recovering when now >= nextRetryTicks && Choice is not null:
                StartStream(catalog);
                break;
        }
    }

    /// <summary>Окончательная остановка.</summary>
    public void Shutdown(Lock voicesLock) => Detach(PlaybackEndReason.Stopped, voicesLock);

    private void StartStream(MmDeviceCatalog catalog)
    {
        if (Choice is null)
            return;

        Volatile.Write(ref state, (int)BusState.Starting);
        var gen = ++generation;
        MMDevice? dev = null;
        WasapiPlayer? p = null;
        try
        {
            dev = catalog.TryGetDevice(Choice.Device.Id) ?? throw new InvalidOperationException("устройство не найдено");
            p = new WasapiPlayerBuilder()
                .WithDevice(dev)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(BufferMs)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            p.PlaybackStopped += (_, e) => onStopped(Id, gen, e.Exception);
            Renderer.OnStreamAttached();
            p.Init(new SampleToWaveProvider(Renderer));
            p.Play();
            OptOutOfDucking(dev);

            player = p;
            device = dev;
            DeviceInUse = false;
            var now = Stopwatch.GetTimestamp();
            lastFrames = Renderer.FramesRendered;
            lastProgressTicks = now;
            // VB-CABLE: ~150 мс тишины после старта, чтобы «холодный» щелчок не попал в звук
            Volatile.Write(ref acceptAfterTicks, now + (Id == BusId.Cable ? MsToTicks(150) : 0));
            Volatile.Write(ref state, (int)BusState.Running);
            log($"{Id}: поток запущен на «{Choice.Device.FriendlyName}», буфер {BufferMs} мс");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = ex.Message;
            DeviceInUse = ex.GetType().Name.Contains("InUse", StringComparison.Ordinal);
            log($"{Id}: не удалось запустить поток: {ex.Message}");
            DisposeWithTimeout(p, dev);
            ScheduleRetry();
        }
    }

    private void StopStream()
    {
        var p = player;
        var dev = device;
        player = null;
        device = null;
        if (p is null && dev is null)
            return;

        if (!DisposeWithTimeout(p, dev))
        {
            // Поток завис внутри драйвера: бросаем его и строим новый конвейер (старый может ещё читать)
            LastError = "поток вывода не остановился за 2 с и был брошен";
            log($"{Id}: {LastError}");
            Renderer = CreateRenderer();
        }
    }

    private void ScheduleRetry()
    {
        Restarts++;
        var delay = RetryDelaysMs[Math.Min(consecutiveFailures, RetryDelaysMs.Length - 1)];
        consecutiveFailures++;
        nextRetryTicks = Stopwatch.GetTimestamp() + MsToTicks(delay);
        Volatile.Write(ref state, (int)BusState.Recovering);
    }

    private BusRenderer CreateRenderer()
    {
        var renderer = new BusRenderer(Id, initialGain, rampMs, mic);
        renderer.Warmup();
        return renderer;
    }

    private void OptOutOfDucking(MMDevice dev)
    {
        // Windows по умолчанию приглушает «другие звуки» на 80 % во время звонка Discord — и наш
        // поток в VB-CABLE тоже. Отказываемся от приглушения для своей сессии (ревью C1).
        try
        {
            dev.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NullReferenceException)
        {
            log($"{Id}: не удалось отключить приглушение: {ex.Message}");
        }
    }

    private static bool DisposeWithTimeout(WasapiPlayer? p, MMDevice? dev)
    {
        if (p is null)
        {
            dev?.Dispose();
            return true;
        }

        var task = Task.Run(() =>
        {
            p.Dispose();
            dev?.Dispose();
        });
        try
        {
            return task.Wait(DisposeTimeout);
        }
        catch (AggregateException)
        {
            return true;   // ошибка при освобождении — поток всё равно остановлен
        }
    }

    private static long MsToTicks(int ms) => ms * Stopwatch.Frequency / 1000;

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
