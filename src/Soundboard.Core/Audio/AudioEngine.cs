using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundboard.Core.Audio.Devices;
using Soundboard.Core.Audio.Dsp;
using Soundboard.Core.Audio.Mic;
using Soundboard.Core.Audio.Output;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Audio.Status;
using Soundboard.Core.Audio.Voices;

namespace Soundboard.Core.Audio;

/// <summary>
/// Звуковой движок: две шины вывода (наушники, VB-CABLE), передача голоса, голоса звуков.
/// Вся работа с устройствами — на одном управляющем потоке (MTA); звуковые потоки — потоки
/// WASAPI с MMCSS; события — на отдельном потоке уведомлений (ревью M6).
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    /// <summary>Имя мьютекса «один движок на пользователя» (ревью C2).</summary>
    public const string InstanceMutexName = @"Local\Soundboard.AudioEngine";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DeviceDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MicStallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly AudioEngineOptions baseOptions;
    private readonly Action<string>? log;
    private readonly BlockingCollection<Action> work = [];
    private readonly Channel<Action> notifications = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock voicesLock = new();
    private readonly Dictionary<long, Voice> voices = [];
    private readonly SoundCache cache;
    private readonly MicPassthrough mic;
    private readonly OutputBus headphones;
    private readonly OutputBus cable;

    private Thread? controlThread;
    private Task? notifyTask;
    private int controlThreadId;
    private volatile bool running;
    private volatile bool stopping;
    private long nextVoiceId;
    private int playbackMode;

    // Только управляющий поток
    private AudioEngineOptions options;
    private MmDeviceCatalog? catalog;
    private DeviceWatcher? watcher;
    private Mutex? instanceMutex;
    private RoutingPlan? plan;
    private DeviceSnapshot snapshot = DeviceSnapshot.Empty;
    private WasapiRecorder? recorder;
    private MMDevice? micDevice;
    private DeviceChoice? micChoice;
    private int micGeneration;
    private long micStartedTicks;
    private long micRestarts;
    private string? micLastError;
    private bool micAccessDenied;
    private string? micFormat;
    private long devicesDirtyAt;
    private long lastStatusTicks;
    private long lastListenersTicks;
    private IReadOnlyList<ListeningApp> listeners = [];
    private bool statusDirty = true;
    private bool cableActive;
    private long cableActiveUntil;
    private ProcessPriorityClass? previousPriority;
    private GCLatencyMode previousLatencyMode;
    private readonly Dictionary<string, double> ppmHints = [];

    private AudioSettings settings = AudioSettings.Default;
    private EngineStatus status = EngineStatus.Initial;

    public AudioEngine(AudioEngineOptions? options = null, Action<string>? log = null)
    {
        baseOptions = options ?? new AudioEngineOptions();
        this.options = baseOptions;
        this.log = log;
        cache = new SoundCache(baseOptions.CacheBudgetBytes);
        mic = new MicPassthrough(baseOptions.MicTargetFillMs, baseOptions.LevelRampMs);
        headphones = new OutputBus(BusId.Headphones, 1f, baseOptions.LevelRampMs, null, OnBusStopped, Log);
        cable = new OutputBus(BusId.Cable, 1f, baseOptions.LevelRampMs, mic, OnBusStopped, Log);
    }

    /// <inheritdoc />
    public event EventHandler<EngineStatus>? StatusChanged;

    /// <inheritdoc />
    public event EventHandler<PlaybackEventArgs>? PlaybackStarted;

    /// <inheritdoc />
    public event EventHandler<PlaybackEventArgs>? PlaybackEnded;

    /// <inheritdoc />
    public event EventHandler<ActivityChangedEventArgs>? CableActivityChanged;

    /// <inheritdoc />
    public AudioSettings Settings => Volatile.Read(ref settings);

    /// <inheritdoc />
    public EngineStatus Status => Volatile.Read(ref status);

    /// <inheritdoc />
    public PlaybackMode PlaybackMode
    {
        get => (PlaybackMode)Volatile.Read(ref playbackMode);
        set
        {
            Volatile.Write(ref playbackMode, (int)value);
            UpdateSettings(s => s with { PlaybackMode = value });
        }
    }

    /// <inheritdoc />
    public Task StartAsync(AudioSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (controlThread is not null)
            throw new InvalidOperationException("Движок уже запущен.");

        Volatile.Write(ref this.settings, settings);
        Volatile.Write(ref playbackMode, (int)settings.PlaybackMode);
        notifyTask = Task.Run(NotificationLoopAsync, CancellationToken.None);
        controlThread = new Thread(ControlLoop) { Name = "Soundboard.AudioControl", IsBackground = true };
        controlThread.SetApartmentState(ApartmentState.MTA);
        controlThread.Start();
        return Invoke(Initialize);
    }

    /// <inheritdoc />
    public Task<SoundHandle> LoadAsync(string path, CancellationToken ct = default) => cache.LoadAsync(path, ct);

    /// <inheritdoc />
    public SoundHandle AddSound(string name, DecodedSound sound) => cache.AddInMemory(name, sound);

    /// <inheritdoc />
    public PlayResult Play(SoundHandle sound, PlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sound);
        if (!running)
            return new PlayResult(PlayStatus.NotRunning, default);
        if (!cache.TryGet(sound.Key, out var decoded))
            return new PlayResult(PlayStatus.NotLoaded, default);

        options ??= new PlayOptions();
        var opts = this.options;
        lock (voicesLock)
        {
            if (PlaybackMode == PlaybackMode.Restart)
            {
                foreach (var v in voices.Values)
                {
                    if (v.Handle.Key == sound.Key)
                        v.RequestStop(Fade.FramesFor(opts.RestartFadeMs), PlaybackEndReason.Restarted);
                }
            }

            var active = voices.Values.Where(v => !v.StopRequested).OrderBy(v => v.Id.Value).ToList();
            for (var i = 0; i <= active.Count - opts.MaxVoices; i++)
                active[i].RequestStop(Fade.FramesFor(opts.RestartFadeMs), PlaybackEndReason.Stolen);

            var voice = new Voice(new VoiceId(Interlocked.Increment(ref nextVoiceId)), sound, decoded,
                Math.Clamp(options.Gain, 0f, 1f), DateTimeOffset.Now);

            if ((options.Outputs & OutputMask.Headphones) != 0 && headphones.AcceptsVoices)
            {
                var tap = new VoiceTap(voice);
                if (headphones.Renderer.Enqueue(tap))
                    voice.Taps[(int)BusId.Headphones] = tap;
            }

            if ((options.Outputs & OutputMask.Cable) != 0 && cable.AcceptsVoices)
            {
                var delay = (int)(options.CableStartDelay.TotalSeconds * AudioFormat.SampleRate);
                var tap = new VoiceTap(voice, delay);
                if (cable.Renderer.Enqueue(tap))
                    voice.Taps[(int)BusId.Cable] = tap;
            }

            if (voice.Taps[0] is null && voice.Taps[1] is null)
                return new PlayResult(PlayStatus.NoOutput, default);

            voices[voice.Id.Value] = voice;
            return new PlayResult(PlayStatus.Started, voice.Id);
        }
    }

    /// <inheritdoc />
    public void StopVoice(VoiceId voice)
    {
        lock (voicesLock)
        {
            if (voices.TryGetValue(voice.Value, out var v))
                v.RequestStop(Fade.FramesFor(options.StopFadeMs), PlaybackEndReason.Stopped);
        }
    }

    /// <inheritdoc />
    public void StopSound(SoundHandle sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        lock (voicesLock)
        {
            foreach (var v in voices.Values)
            {
                if (v.Handle.Key == sound.Key)
                    v.RequestStop(Fade.FramesFor(options.StopFadeMs), PlaybackEndReason.Stopped);
            }
        }
    }

    /// <inheritdoc />
    public void StopAll()
    {
        lock (voicesLock)
        {
            foreach (var v in voices.Values)
                v.RequestStop(Fade.FramesFor(options.StopFadeMs), PlaybackEndReason.StopAll);
        }
    }

    /// <inheritdoc />
    public void SetLevel(LevelTarget target, float level)
    {
        level = VolumeCurve.ClampLevel(level);
        switch (target)
        {
            case LevelTarget.Headphones:
                UpdateSettings(s => s with { HeadphonesLevel = level });
                break;
            case LevelTarget.Cable:
                UpdateSettings(s => s with { CableLevel = level });
                break;
            case LevelTarget.Mic:
                UpdateSettings(s => s with { MicLevel = level });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        ApplyLevels();
    }

    /// <inheritdoc />
    public void SetMicMuted(bool muted)
    {
        UpdateSettings(s => s with { MicMuted = muted });
        ApplyLevels();
    }

    /// <inheritdoc />
    public Task ApplySettingsAsync(AudioSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        newSettings.Validate();
        return Invoke(() =>
        {
            var old = Settings;
            Volatile.Write(ref settings, newSettings);
            Volatile.Write(ref playbackMode, (int)newSettings.PlaybackMode);
            ApplyLevels();
            if (old.LatencyProfile != newSettings.LatencyProfile)
            {
                options = AudioEngineOptions.For(newSettings.LatencyProfile) with { RaiseProcessPriority = baseOptions.RaiseProcessPriority };
                mic.TargetFillMs = options.MicTargetFillMs;
            }

            if (old.MicChannelMode != newSettings.MicChannelMode)
                StopMic();
            Reconfigure("изменились настройки");
        });
    }

    /// <inheritdoc />
    public Task RefreshDevicesAsync() => Invoke(() => Reconfigure("обновление устройств"));

    /// <inheritdoc />
    public Task RestartStreamsAsync() => Invoke(() =>
    {
        headphones.Restart(catalog!);
        cable.Restart(catalog!);
        StopMic();
        Reconfigure("пересборка потоков");
    });

    /// <inheritdoc />
    public MeterSnapshot ReadMeters()
    {
        var hp = headphones.Renderer.Meter.TakeMax();
        var cb = cable.Renderer.Meter.TakeMax();
        return new MeterSnapshot(
            hp.PeakDb,
            cb.PeakDb,
            -cable.Renderer.Limiter.TakeMaxGainReductionDb(),
            mic.InputMeter.TakeMax().PeakDb,
            mic.OutputMeter.TakeMax().PeakDb);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (controlThread is null || stopping)
            return;

        try
        {
            await Invoke(Shutdown).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Уже остановлен
        }

        stopping = true;
        work.CompleteAdding();
        controlThread.Join(TimeSpan.FromSeconds(5));
        notifications.Writer.TryComplete();
        if (notifyTask is not null)
            await notifyTask.ConfigureAwait(false);
        cache.Dispose();
        work.Dispose();
    }

    // ───────────────────────── управляющий поток ─────────────────────────

    private void ControlLoop()
    {
        controlThreadId = Environment.CurrentManagedThreadId;
        var next = Stopwatch.GetTimestamp();
        while (!stopping)
        {
            var waitMs = Math.Max(0, (int)((next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
            try
            {
                if (work.TryTake(out var action, waitMs))
                    RunSafe(action);
            }
            catch (InvalidOperationException)
            {
                break;   // очередь закрыта
            }

            var now = Stopwatch.GetTimestamp();
            if (now >= next && running)
            {
                RunSafe(() => Tick(now));
                next = now + (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);
            }
            else if (!running)
            {
                next = now + (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);
            }
        }
    }

    private void Initialize()
    {
        // Один движок на пользователя: два движка удвоили бы голос в кабеле (ревью C2)
        instanceMutex = new Mutex(false, InstanceMutexName);
        bool acquired;
        try
        {
            acquired = instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            var issue = IssueText.Create(IssueCode.EngineAlreadyRunning);
            Volatile.Write(ref status, Status with { Issues = [issue], State = EngineState.Stopped, Timestamp = DateTimeOffset.Now });
            throw new InvalidOperationException(issue.Message);
        }

        TuneProcess();
        options = AudioEngineOptions.For(Settings.LatencyProfile) with { RaiseProcessPriority = baseOptions.RaiseProcessPriority };
        mic.TargetFillMs = options.MicTargetFillMs;
        catalog = new MmDeviceCatalog();
        watcher = new DeviceWatcher(catalog.Enumerator, reason => Post(() => MarkDevicesDirty(reason)));
        running = true;
        ApplyLevels();
        Reconfigure("запуск");
        PublishStatus(force: true);
    }

    private void Shutdown()
    {
        if (!running)
            return;

        // Порядок важен: затухание звуков → тишина в кабель → голос → потоки (ревью, мелочь 8)
        StopAll();
        Thread.Sleep(options.StopFadeMs + options.CableBufferMs + 50);
        mic.SetGain(0, true);
        Thread.Sleep(30);
        StopMic();
        running = false;
        headphones.Shutdown(voicesLock);
        cable.Shutdown(voicesLock);
        PollVoices(Stopwatch.GetTimestamp());
        watcher?.Dispose();
        catalog?.Dispose();
        RestoreProcess();
        if (instanceMutex is not null)
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            instanceMutex = null;
        }

        Volatile.Write(ref status, Status with { State = EngineState.Stopped, Timestamp = DateTimeOffset.Now, Playing = [] });
        RaiseStatusChanged(Status);
    }

    private void Tick(long now)
    {
        headphones.Tick(catalog!, now);
        cable.Tick(catalog!, now);
        CheckMic(now);
        PollVoices(now);
        UpdateActivity(now);

        if (devicesDirtyAt != 0 && now - devicesDirtyAt >= ToTicks(DeviceDebounce))
        {
            devicesDirtyAt = 0;
            Reconfigure("устройства изменились");
        }

        if (statusDirty || now - lastStatusTicks >= ToTicks(TimeSpan.FromMilliseconds(250)))
            PublishStatus(force: statusDirty);
    }

    private void MarkDevicesDirty(string reason)
    {
        Log($"устройства: {reason}");
        devicesDirtyAt = Stopwatch.GetTimestamp();
    }

    private void Reconfigure(string reason)
    {
        if (!running || catalog is null)
            return;

        snapshot = catalog.Snapshot();
        var current = Settings;
        plan = DeviceRouter.Resolve(snapshot, current);
        Log($"маршрутизация ({reason}): кабель={plan.Cable?.Device.FriendlyName ?? "нет"}, " +
            $"наушники={plan.Headphones?.Device.FriendlyName ?? "нет"}, микрофон={plan.Mic?.Device.FriendlyName ?? "нет"}");

        if (plan.Cable is null)
        {
            StopMic();
            if (cable.State != BusState.Detached)
                cable.Detach(PlaybackEndReason.OutputLost, voicesLock);
        }
        else
        {
            cable.Attach(catalog, plan.Cable, options.CableBufferMs);
        }

        if (plan.Headphones is null)
        {
            if (headphones.State != BusState.Detached)
                headphones.Detach(PlaybackEndReason.OutputLost, voicesLock);
        }
        else
        {
            headphones.Attach(catalog, plan.Headphones, options.HeadphonesBufferMs);
        }

        // Голос: только при работающем кабеле и включённой передаче
        var wantMic = plan.Mic is not null && plan.Cable is not null && current.MicPassthroughEnabled;
        if (!wantMic)
        {
            StopMic();
        }
        else if (micChoice is null || !micChoice.Device.HasId(plan.Mic!.Device.Id) || recorder is null)
        {
            StopMic();
            StartMic(plan.Mic!);
        }

        statusDirty = true;
    }

    // ───────────────────────── микрофон ─────────────────────────

    private void StartMic(DeviceChoice choice)
    {
        micChoice = choice;
        micAccessDenied = false;
        var gen = ++micGeneration;
        try
        {
            micDevice = catalog!.TryGetDevice(choice.Device.Id) ?? throw new InvalidOperationException("микрофон не найден");
            var builder = new WasapiRecorderBuilder()
                .WithDevice(micDevice)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(100)
                .WithMmcssThreadPriority("Pro Audio");

            // Не 48 кГц — просим Windows пересчитать частоту (каналы сохраняем, сводим сами)
            if (choice.Device.HasFormat && choice.Device.SampleRate != AudioFormat.SampleRate && choice.Device.Channels <= 2)
                builder = builder.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, choice.Device.Channels));

            var rec = builder.Build();
            mic.Attach(rec.WaveFormat, Settings.MicChannelMode, ppmHints.GetValueOrDefault(choice.Device.Id + "|" + plan?.Cable?.Device.Id));
            rec.DataAvailable += (buffer, flags, _, _) => mic.OnPacket(buffer, flags);
            rec.RecordingStopped += (_, e) => Post(() => OnMicStopped(gen, e.Exception));
            rec.StartRecording();
            recorder = rec;
            micFormat = rec.WaveFormat.ToString();
            micStartedTicks = Stopwatch.GetTimestamp();
            micLastError = null;
            Log($"микрофон: захват «{choice.Device.FriendlyName}» ({micFormat})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            micAccessDenied = ex is UnauthorizedAccessException || (uint)ex.HResult == 0x80070005;
            micLastError = micAccessDenied ? "доступ к микрофону запрещён в параметрах конфиденциальности Windows" : ex.Message;
            Log($"микрофон: не удалось запустить: {micLastError}");
            StopMic();
            micChoice = choice;   // повторим при следующей пересборке
            micStartedTicks = Stopwatch.GetTimestamp();
        }

        statusDirty = true;
    }

    private void StopMic()
    {
        var rec = recorder;
        var dev = micDevice;
        recorder = null;
        micDevice = null;
        if (micChoice is not null && plan?.Cable is not null && mic.Locked)
            ppmHints[micChoice.Device.Id + "|" + plan.Cable.Device.Id] = mic.DriftPpm;
        micChoice = null;
        if (rec is not null)
        {
            var task = Task.Run(() =>
            {
                try
                {
                    rec.StopRecording();
                }
                finally
                {
                    rec.Dispose();
                }
            });
            if (!task.Wait(TimeSpan.FromSeconds(2)))
                micLastError = "захват микрофона не остановился за 2 с и был брошен";
        }

        dev?.Dispose();
        mic.Detach();
        statusDirty = true;
    }

    private void OnMicStopped(int gen, Exception? error)
    {
        if (gen != micGeneration || recorder is null)
            return;
        micLastError = error?.Message ?? "захват микрофона остановился";
        micRestarts++;
        Log($"микрофон: {micLastError}");
        var choice = micChoice;
        StopMic();
        devicesDirtyAt = Stopwatch.GetTimestamp();   // пересоберём после паузы — устройство могло пропасть
        _ = choice;
    }

    private void CheckMic(long now)
    {
        // Повторная попытка после ошибки запуска
        if (recorder is null && micChoice is not null && now - micStartedTicks > ToTicks(TimeSpan.FromSeconds(2)))
        {
            var choice = micChoice;
            micChoice = null;
            StartMic(choice);
            return;
        }

        // Тихое зависание захвата: пакетов нет дольше 500 мс (ревью M1)
        if (recorder is not null && now - micStartedTicks > ToTicks(TimeSpan.FromSeconds(1)))
        {
            var last = mic.LastPacketTicks;
            var since = last == 0 ? now - micStartedTicks : now - last;
            if (since > ToTicks(MicStallTimeout))
            {
                micLastError = "захват микрофона завис — перезапуск";
                micRestarts++;
                Log($"микрофон: {micLastError}");
                var choice = micChoice!;
                StopMic();
                StartMic(choice);
            }
        }
    }

    // ───────────────────────── голоса и события ─────────────────────────

    private void PollVoices(long now)
    {
        List<Voice>? started = null;
        List<Voice>? ended = null;
        lock (voicesLock)
        {
            foreach (var voice in voices.Values)
            {
                // Отвод, застрявший в очереди шины, которая не работает, — завершаем
                for (var i = 0; i < voice.Taps.Length; i++)
                {
                    var tap = voice.Taps[i];
                    var bus = i == 0 ? headphones : cable;
                    if (tap is { State: TapState.Pending } && bus.State != BusState.Running
                        && DateTimeOffset.Now - voice.CreatedAt > TimeSpan.FromSeconds(1))
                    {
                        tap.ForceEnd(PlaybackEndReason.OutputLost);
                    }
                }

                if (!voice.StartedRaised && voice.AnyTapPlaying)
                {
                    voice.StartedRaised = true;
                    (started ??= []).Add(voice);
                }

                if (voice.AllTapsEnded)
                    (ended ??= []).Add(voice);
            }

            if (ended is not null)
            {
                foreach (var v in ended)
                    voices.Remove(v.Id.Value);
            }
        }

        if (started is not null)
        {
            foreach (var v in started)
                Notify(() => PlaybackStarted?.Invoke(this, new PlaybackEventArgs(v.Id, v.Handle, null, true)));
        }

        if (ended is not null)
        {
            foreach (var v in ended)
            {
                var audible = v.Taps.Any(t => t?.WasAudible == true);
                var reason = v.EndReason;
                Notify(() => PlaybackEnded?.Invoke(this, new PlaybackEventArgs(v.Id, v.Handle, reason, audible)));
            }

            statusDirty = true;
        }
    }

    private void UpdateActivity(long now)
    {
        bool anyCable;
        lock (voicesLock)
            anyCable = voices.Values.Any(v => v.Taps[(int)BusId.Cable] is { State: not TapState.Ended });

        if (anyCable)
            cableActiveUntil = now + ToTicks(options.ActivityHold);
        var active = anyCable || now < cableActiveUntil;
        if (active != cableActive)
        {
            cableActive = active;
            Notify(() => CableActivityChanged?.Invoke(this, new ActivityChangedEventArgs(active)));
        }
    }

    // ───────────────────────── статус ─────────────────────────

    private void PublishStatus(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        lastStatusTicks = now;
        statusDirty = false;
        var current = Settings;

        if (plan?.CableCapture is not null && catalog is not null && now - lastListenersTicks > ToTicks(TimeSpan.FromSeconds(2)))
        {
            lastListenersTicks = now;
            listeners = catalog.ListeningApps(plan.CableCapture.Id);
        }

        var issues = new List<EngineIssue>(plan?.Issues ?? []);
        AddBusIssues(issues, headphones);
        AddBusIssues(issues, cable);
        var (cableVolume, cableMuted) = cable.SessionVolume();
        if (cable.State == BusState.Running && (cableVolume < 0.999f || cableMuted))
            issues.Add(IssueText.Create(IssueCode.CableSessionAttenuated, cable.Choice?.Device.FriendlyName, $"{cableVolume:P0}{(cableMuted ? ", выкл." : "")}"));
        if (micAccessDenied)
            issues.Add(IssueText.Create(IssueCode.MicAccessDenied, micChoice?.Device.FriendlyName));
        else if (micLastError is not null && current.MicPassthroughEnabled && recorder is null && plan?.Mic is not null)
            issues.Add(IssueText.Create(IssueCode.CaptureStreamFailed, plan.Mic.Device.FriendlyName, micLastError));

        List<PlayingVoiceInfo> playing;
        lock (voicesLock)
        {
            playing = voices.Values
                .Where(v => !v.AllTapsEnded)
                .Select(v => new PlayingVoiceInfo(
                    v.Id,
                    v.Handle.Key,
                    v.Handle.Name,
                    (v.Taps[1] ?? v.Taps[0])!.PositionFrames / (double)AudioFormat.SampleRate,
                    v.Sound.Duration.TotalSeconds,
                    (v.Taps[0] is null ? 0 : OutputMask.Headphones) | (v.Taps[1] is null ? 0 : OutputMask.Cable)))
                .ToList();
        }

        var state = cable.State == BusState.Running && (headphones.State == BusState.Running || !current.HeadphonesEnabled)
            && (recorder is not null || !current.MicPassthroughEnabled)
            ? EngineState.Running
            : EngineState.Degraded;

        var next = new EngineStatus(
            DateTimeOffset.Now,
            state,
            new VbCableStatus(plan?.Cable is not null, plan?.Cable?.Device.FriendlyName, plan?.CableCapture?.FriendlyName, listeners),
            BusSnapshot(headphones, current.HeadphonesLevel),
            BusSnapshot(cable, current.CableLevel),
            new MicStatus(
                current.MicPassthroughEnabled,
                current.MicMuted,
                recorder is not null,
                micChoice?.Device.Id ?? plan?.Mic?.Device.Id,
                micChoice?.Device.FriendlyName ?? plan?.Mic?.Device.FriendlyName,
                micFormat,
                current.MicLevel,
                mic.DriftPpm,
                mic.Locked,
                mic.FillMs,
                mic.TargetFillMs,
                mic.Underruns,
                mic.Overflows,
                mic.Resyncs,
                mic.Discontinuities,
                micRestarts,
                micLastError),
            PlaybackMode,
            playing,
            snapshot.Outputs,
            snapshot.Inputs,
            issues);

        var previous = Status;
        Volatile.Write(ref status, next);
        if (force || !SameShape(previous, next))
            RaiseStatusChanged(next);
    }

    private static bool SameShape(EngineStatus a, EngineStatus b) =>
        a.State == b.State
        && a.Headphones.State == b.Headphones.State && a.Headphones.DeviceId == b.Headphones.DeviceId
        && a.Cable.State == b.Cable.State && a.Cable.DeviceId == b.Cable.DeviceId
        && a.Mic.Capturing == b.Mic.Capturing && a.Mic.DeviceId == b.Mic.DeviceId && a.Mic.Muted == b.Mic.Muted
        && a.Playing.Count == b.Playing.Count
        && a.Issues.Count == b.Issues.Count
        && a.VbCable.ListeningApps.Count == b.VbCable.ListeningApps.Count;

    private static BusStatus BusSnapshot(OutputBus bus, float level)
    {
        var (volume, muted) = bus.SessionVolume();
        var r = bus.Renderer;
        return new BusStatus(
            bus.Id,
            bus.State,
            bus.Choice?.Device.Id,
            bus.Choice?.Device.FriendlyName,
            bus.Choice?.Reason,
            level,
            bus.BufferMs,
            r.NearEmpty,
            bus.Restarts,
            r.Errors,
            r.AudioThreadAllocatedBytes,
            r.AudioThreadPriority,
            volume,
            muted,
            bus.LastError);
    }

    private static void AddBusIssues(List<EngineIssue> issues, OutputBus bus)
    {
        if (bus.Choice is null)
            return;
        if (bus.DeviceInUse)
            issues.Add(IssueText.Create(IssueCode.DeviceInUse, bus.Choice.Device.FriendlyName, deviceId: bus.Choice.Device.Id));
        else if (bus.State == BusState.Recovering && bus.LastError is not null)
            issues.Add(IssueText.Create(IssueCode.OutputStreamFailed, bus.Choice.Device.FriendlyName, bus.LastError, bus.Choice.Device.Id));
        if (bus.Renderer.Errors > 0)
            issues.Add(IssueText.Create(IssueCode.AudioCallbackError, bus.Choice.Device.FriendlyName, bus.Renderer.LastError?.Message));
    }

    // ───────────────────────── служебное ─────────────────────────

    private void ApplyLevels()
    {
        var s = Settings;
        headphones.Renderer.SetGain(VolumeCurve.ToGain(s.HeadphonesLevel));
        cable.Renderer.SetGain(VolumeCurve.ToGain(s.CableLevel));
        mic.SetGain(VolumeCurve.ToGain(s.MicLevel), s.MicMuted);
        statusDirty = true;
    }

    private void UpdateSettings(Func<AudioSettings, AudioSettings> change)
    {
        while (true)
        {
            var old = Volatile.Read(ref settings);
            if (ReferenceEquals(Interlocked.CompareExchange(ref settings, change(old), old), old))
                return;
        }
    }

    private void OnBusStopped(BusId id, int generation, Exception? error) =>
        Post(() => (id == BusId.Cable ? cable : headphones).OnStreamStopped(generation, error));

    private void TuneProcess()
    {
        // Приоритет процесса и отказ от энергосбережения: GC и звуковые потоки не должны
        // проигрывать фоновой нагрузке (ревью M2). Это настройки только нашего процесса.
        previousLatencyMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        if (options.RaiseProcessPriority)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                previousPriority = self.PriorityClass;
                if (previousPriority is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
                    self.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                previousPriority = null;
            }
        }

        if (!NativeMethods.DisablePowerThrottling())
            Log("не удалось отключить энергосбережение процесса");
    }

    private void RestoreProcess()
    {
        GCSettings.LatencyMode = previousLatencyMode;
        if (previousPriority is { } p)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                self.PriorityClass = p;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Не страшно
            }
        }
    }

    private void Post(Action action)
    {
        try
        {
            if (!stopping)
                work.Add(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Движок остановлен
        }
    }

    private Task Invoke(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Environment.CurrentManagedThreadId == controlThreadId)
            throw new InvalidOperationException("Нельзя ждать управляющий поток движка из него самого (обработчик события?).");
        try
        {
            work.Add(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(AudioEngine)));
        }

        return tcs.Task;
    }

    private void RunSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"ошибка управляющего потока: {ex}");
        }
    }

    private void Notify(Action action) => notifications.Writer.TryWrite(action);

    private void RaiseStatusChanged(EngineStatus s) => Notify(() => StatusChanged?.Invoke(this, s));

    private async Task NotificationLoopAsync()
    {
        await foreach (var action in notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log($"ошибка в обработчике события: {ex.Message}");
            }
        }
    }

    private void Log(string message) => log?.Invoke(message);

    private static long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
