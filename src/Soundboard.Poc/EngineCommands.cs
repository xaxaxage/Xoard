using Soundboard.Core.Audio;
using Soundboard.Core.Audio.Devices;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Audio.Status;

namespace Soundboard.Poc;

/// <summary>Этап 1: команды поверх движка Soundboard.Core.</summary>
internal static class EngineCommands
{
    private static readonly string[] SoundExtensions = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".aac", ".aiff"];

    /// <summary>engine-devices: устройства, план маршрутизации и проблемы (ничего не играет).</summary>
    public static int Devices(CliOptions options)
    {
        using var catalog = new MmDeviceCatalog();
        var snapshot = catalog.Snapshot();
        var settings = BuildSettings(snapshot, options);
        var plan = DeviceRouter.Resolve(snapshot, settings);

        Ui.Heading("Устройства вывода:");
        foreach (var d in snapshot.Outputs)
            PrintDevice(d, snapshot.Defaults.RenderConsole);
        Ui.Heading("Устройства ввода:");
        foreach (var d in snapshot.Inputs)
            PrintDevice(d, snapshot.Defaults.CaptureConsole);

        Ui.Info("");
        Ui.Heading("План движка:");
        Ui.Info($"  VB-CABLE (звуки + голос): {Describe(plan.Cable)}");
        Ui.Info($"  Discord слушает:          {plan.CableCapture?.FriendlyName ?? "—"}");
        Ui.Info($"  Наушники (только звуки):  {Describe(plan.Headphones)}");
        Ui.Info($"  Микрофон:                 {Describe(plan.Mic)}");
        if (plan.CableCapture is not null)
        {
            var listeners = catalog.ListeningApps(plan.CableCapture.Id);
            Ui.Info($"  Сейчас слушают CABLE Output: {(listeners.Count == 0 ? "никто" : string.Join(", ", listeners.Select(l => l.ProcessName)))}");
        }

        PrintIssues(plan.Issues);
        return plan.Cable is null ? 2 : 0;
    }

    /// <summary>engine: интерактивный режим с настоящим микрофоном.</summary>
    public static int Interactive(CliOptions options)
    {
        AudioSettings settings;
        using (var catalog = new MmDeviceCatalog())
            settings = BuildSettings(catalog.Snapshot(), options);

        var engine = new AudioEngine(AudioEngineOptions.For(settings.LatencyProfile), msg => { if (Verbose) Ui.Dim($"  · {msg}"); });
        engine.PlaybackStarted += (_, e) => Ui.Info($"  ▶ {e.Sound.Name} {e.Voice}");
        engine.PlaybackEnded += (_, e) => Ui.Dim($"  ■ {e.Sound.Name} {e.Voice}: {ReasonText(e.Reason)}");
        engine.CableActivityChanged += (_, e) => Ui.Dim(e.Active ? "  [кабель: звучит]" : "  [кабель: тишина]");
        IReadOnlyList<EngineIssue> lastIssues = [];
        engine.StatusChanged += (_, s) =>
        {
            if (s.Issues.Select(i => i.Code).SequenceEqual(lastIssues.Select(i => i.Code)))
                return;
            lastIssues = s.Issues;
            PrintIssues(s.Issues);
        };

        try
        {
            engine.StartAsync(settings).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }

        var sounds = LoadSounds(engine, options.Sounds);
        var tone = engine.AddSound("тон 1 кГц", TestTones.Tone(1000, -12, 1.0));
        PrintInteractiveHelp(sounds);
        PrintStatus(engine.Status);

        var previewNext = false;
        var meterOn = false;
        using var meterCts = new CancellationTokenSource();
        var meterTask = Task.Run(async () =>
        {
            while (!meterCts.IsCancellationRequested)
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (meterOn)
                    PrintMeter(engine);
            }
        });

        var quit = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit = true;
        };

        while (!quit)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(20);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            var s = engine.Settings;
            switch (key.Key)
            {
                case >= ConsoleKey.D0 and <= ConsoleKey.D9:
                    var index = key.Key == ConsoleKey.D0 ? 9 : key.Key - ConsoleKey.D1;
                    if (index < sounds.Count)
                    {
                        var r = engine.Play(sounds[index], new PlayOptions(Outputs: previewNext ? OutputMask.Headphones : OutputMask.Both));
                        if (r.Status != PlayStatus.Started)
                            Ui.Warn($"  не запущено: {r.Status}");
                        previewNext = false;
                    }

                    break;
                case ConsoleKey.T:
                    engine.Play(tone, new PlayOptions(Outputs: previewNext ? OutputMask.Headphones : OutputMask.Both));
                    previewNext = false;
                    break;
                case ConsoleKey.Spacebar:
                    engine.StopAll();
                    Ui.Info("  ■ стоп всё");
                    break;
                case ConsoleKey.O:
                    engine.PlaybackMode = engine.PlaybackMode == PlaybackMode.Overlap ? PlaybackMode.Restart : PlaybackMode.Overlap;
                    Ui.Info($"  режим: {(engine.PlaybackMode == PlaybackMode.Overlap ? "наложение" : "перезапуск")}");
                    break;
                case ConsoleKey.M:
                    engine.SetMicMuted(!s.MicMuted);
                    Ui.Info($"  микрофон: {(engine.Settings.MicMuted ? "ВЫКЛ (mute)" : "вкл")}");
                    break;
                case ConsoleKey.P:
                    engine.ApplySettingsAsync(s with { MicPassthroughEnabled = !s.MicPassthroughEnabled }).GetAwaiter().GetResult();
                    Ui.Info($"  передача голоса: {(engine.Settings.MicPassthroughEnabled ? "вкл" : "выкл")}");
                    break;
                case ConsoleKey.Q or ConsoleKey.A:
                    ChangeLevel(engine, LevelTarget.Headphones, s.HeadphonesLevel, key.Key == ConsoleKey.Q);
                    break;
                case ConsoleKey.W or ConsoleKey.S:
                    ChangeLevel(engine, LevelTarget.Cable, s.CableLevel, key.Key == ConsoleKey.W);
                    break;
                case ConsoleKey.E or ConsoleKey.D:
                    ChangeLevel(engine, LevelTarget.Mic, s.MicLevel, key.Key == ConsoleKey.E);
                    break;
                case ConsoleKey.V:
                    previewNext = true;
                    Ui.Info("  следующий звук — только в наушники");
                    break;
                case ConsoleKey.I:
                    PrintStatus(engine.Status);
                    break;
                case ConsoleKey.L:
                    meterOn = !meterOn;
                    break;
                case ConsoleKey.R:
                    Ui.Info("  пересборка потоков…");
                    engine.RestartStreamsAsync().GetAwaiter().GetResult();
                    break;
                case ConsoleKey.Oem2 or ConsoleKey.H:
                    PrintInteractiveHelp(sounds);
                    break;
                case ConsoleKey.Escape:
                    quit = true;
                    break;
            }
        }

        meterCts.Cancel();
        meterTask.Wait(1000);
        Ui.Info("Останавливаю движок…");
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Подробный журнал движка (переменная окружения SOUNDBOARD_POC_DEBUG=1).</summary>
    public static bool Verbose => Environment.GetEnvironmentVariable("SOUNDBOARD_POC_DEBUG") == "1";

    public static AudioSettings BuildSettings(DeviceSnapshot snapshot, CliOptions options)
    {
        var settings = AudioSettings.Default with
        {
            MicPassthroughEnabled = !options.NoMic,
            LatencyProfile = options.Profile,
        };

        if (options.Headphones is not null)
            settings = settings with { HeadphonesDeviceId = FindId(snapshot.Outputs, options.Headphones, "наушники") };
        if (options.Mic is not null)
            settings = settings with { MicDeviceId = FindId(snapshot.Inputs, options.Mic, "микрофон") };
        return settings;
    }

    private static string FindId(IReadOnlyList<AudioEndpointInfo> list, string part, string what)
    {
        var matches = list.Where(d => d.FriendlyName.Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new CliException($"Не найдено устройство ({what}), содержащее «{part}». Список: .\\poc.cmd engine-devices"),
            _ => throw new CliException($"Под «{part}» подходит несколько устройств ({what}): {string.Join(", ", matches.Select(m => m.FriendlyName))}"),
        };
    }

    private static List<SoundHandle> LoadSounds(AudioEngine engine, string? folder)
    {
        var result = new List<SoundHandle>();
        if (folder is null)
            return result;
        if (!Directory.Exists(folder))
        {
            Ui.Warn($"Папка не найдена: {folder}");
            return result;
        }

        var files = Directory.EnumerateFiles(folder)
            .Where(f => SoundExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .Take(10)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                result.Add(engine.LoadAsync(file).GetAwaiter().GetResult());
            }
            catch (SoundLoadException ex)
            {
                Ui.Warn($"  {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return result;
    }

    private static void ChangeLevel(AudioEngine engine, LevelTarget target, float current, bool up)
    {
        var level = Math.Clamp(current + (up ? 0.05f : -0.05f), 0f, 1f);
        engine.SetLevel(target, level);
        var name = target switch { LevelTarget.Headphones => "наушники", LevelTarget.Cable => "звуки в кабель", _ => "голос" };
        Ui.Info($"  {name}: {level:P0} ({Soundboard.Core.Audio.Dsp.VolumeCurve.ToDecibels(level):0.0} дБ)");
    }

    private static void PrintInteractiveHelp(List<SoundHandle> sounds)
    {
        Ui.Heading("Движок запущен. Клавиши:");
        for (var i = 0; i < sounds.Count; i++)
            Ui.Info($"  {(i + 1) % 10} — {sounds[i].Name} ({sounds[i].Duration:mm\\:ss})");
        Ui.Info("""
              t — тон 1 кГц        пробел — стоп всё        o — наложение/перезапуск
              m — mute голоса      p — передача голоса вкл/выкл
              q/a — наушники ±     w/s — звуки в кабель ±   e/d — голос ±
              v — следующий звук только в наушники (предпрослушивание)
              i — статус   l — индикатор уровней   r — пересобрать потоки   h — справка   Esc — выход
            """);
    }

    public static void PrintStatus(EngineStatus s)
    {
        Ui.Heading($"Статус: {StateText(s.State)}");
        Ui.Info($"  VB-CABLE: {(s.VbCable.Installed ? s.VbCable.RenderName : "не найден")}; " +
                $"слушают CABLE Output: {(s.VbCable.ListeningApps.Count == 0 ? "никто" : string.Join(", ", s.VbCable.ListeningApps.Select(a => a.ProcessName)))}");
        PrintBus("Наушники", s.Headphones);
        PrintBus("Кабель  ", s.Cable);
        var m = s.Mic;
        Ui.Info($"  Голос:    {(m.Enabled ? (m.Capturing ? "захват" : "нет захвата") : "выкл")}{(m.Muted ? ", MUTE" : "")} — {m.DeviceName ?? "—"} [{m.Format ?? "—"}]");
        Ui.Info($"            дрейф {m.DriftPpm:+0.0;-0.0} ppm{(m.Locked ? " (захвачен)" : "")}, буфер {m.FillMs:0.0}/{m.TargetMs} мс, " +
                $"недогрузок {m.Underruns}, переполнений {m.Overflows}, пересинхр. {m.Resyncs}, разрывов захвата {m.Discontinuities}");
        if (m.LastError is not null)
            Ui.Warn($"            последняя ошибка: {m.LastError}");
        if (s.Playing.Count > 0)
            Ui.Info("  Играет:   " + string.Join(", ", s.Playing.Select(p => $"{p.Name} {p.PositionSec:0.0}/{p.DurationSec:0.0} с")));
        PrintIssues(s.Issues);
    }

    private static void PrintBus(string name, BusStatus b)
    {
        Ui.Info($"  {name}: {BusText(b.State)} — {b.DeviceName ?? "—"}, громкость {b.Level:P0}, буфер {b.BufferMs} мс, " +
                $"почти-пусто {b.NearEmpty}, пересборок {b.Restarts}, ошибок {b.CallbackErrors}, выделений {b.AudioThreadAllocatedBytes} Б, " +
                $"приоритет потока {(b.AudioThreadPriority == int.MinValue ? "?" : b.AudioThreadPriority.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
                (b.SessionVolume < 0.999f || b.SessionMuted ? $", СЕССИЯ {b.SessionVolume:P0}{(b.SessionMuted ? " выкл" : "")}" : ""));
        if (b.LastError is not null)
            Ui.Dim($"            последняя ошибка: {b.LastError}");
    }

    private static void PrintMeter(AudioEngine engine)
    {
        var m = engine.ReadMeters();
        var s = engine.Status;
        Ui.Dim($"  HP {Db(m.HeadphonesPeakDb)}  CABLE {Db(m.CablePeakDb)} (лимитер {m.CableLimiterDb:0.0})  " +
               $"голос вх {Db(m.MicInPeakDb)} вых {Db(m.MicOutPeakDb)}  дрейф {s.Mic.DriftPpm:+0;-0} ppm  буфер {s.Mic.FillMs:0} мс  " +
               $"недогр {s.Mic.Underruns}  почти-пусто {s.Cable.NearEmpty}");
    }

    private static string Db(float db) => db <= -119 ? "  −∞" : $"{db,5:0.0}";

    public static void PrintIssues(IReadOnlyList<EngineIssue> issues)
    {
        foreach (var issue in issues)
        {
            var text = $"  {(issue.Severity == IssueSeverity.Error ? "✗" : "!")} {issue.Message}" + (issue.HelpUrl is null ? "" : $" ({issue.HelpUrl})");
            switch (issue.Severity)
            {
                case IssueSeverity.Error:
                    Ui.Error(text);
                    break;
                case IssueSeverity.Warning:
                    Ui.Warn(text);
                    break;
                default:
                    Ui.Dim(text);
                    break;
            }
        }
    }

    private static void PrintDevice(AudioEndpointInfo d, string? defaultId)
    {
        var marks = new List<string>();
        if (d.HasId(defaultId))
            marks.Add("по умолчанию");
        if (d.IsVbCable)
            marks.Add(d.IsVbCable16Ch ? "VB-CABLE 16ch" : "VB-CABLE");
        if (d.LoopbackRisk != LoopbackRisk.None)
            marks.Add(d.LoopbackRisk == LoopbackRisk.Loopback ? "петлевое" : "виртуальное");
        Ui.Info($"  {d.FriendlyName}");
        Ui.Dim($"      {d.FormatDescription}{(marks.Count > 0 ? "   [" + string.Join("; ", marks) + "]" : "")}");
    }

    private static string Describe(DeviceChoice? c) => c is null ? "—" : $"{c.Device.FriendlyName} ({ReasonText(c.Reason)})";

    private static string ReasonText(ChoiceReason r) => r switch
    {
        ChoiceReason.UserSelected => "выбрано",
        ChoiceReason.WindowsDefault => "по умолчанию в Windows",
        ChoiceReason.FallbackToWindowsDefault => "выбранное недоступно — по умолчанию",
        _ => "найдено автоматически",
    };

    private static string ReasonText(PlaybackEndReason? r) => r switch
    {
        PlaybackEndReason.Completed => "доиграл",
        PlaybackEndReason.Stopped => "остановлен",
        PlaybackEndReason.StopAll => "стоп всё",
        PlaybackEndReason.Restarted => "перезапущен",
        PlaybackEndReason.Stolen => "вытеснен",
        PlaybackEndReason.OutputLost => "устройство пропало",
        PlaybackEndReason.Error => "ошибка",
        _ => "",
    };

    private static string StateText(EngineState s) => s switch
    {
        EngineState.Running => "всё работает",
        EngineState.Degraded => "работает частично",
        EngineState.Starting => "запуск",
        _ => "остановлен",
    };

    private static string BusText(BusState s) => s switch
    {
        BusState.Running => "работает",
        BusState.Starting => "запуск",
        BusState.Recovering => "восстановление",
        _ => "нет устройства",
    };
}

/// <summary>Тестовые сигналы для движка.</summary>
internal static class TestTones
{
    /// <summary>Синус с плавными краями 5 мс.</summary>
    public static DecodedSound Tone(double hz, double dbfs, double seconds, double fadeMs = 5) =>
        Mix([(hz, dbfs)], seconds, fadeMs);

    /// <summary>Сумма синусов.</summary>
    public static DecodedSound Mix((double Hz, double Dbfs)[] tones, double seconds, double fadeMs = 5)
    {
        var frames = (int)(seconds * AudioFormat.SampleRate);
        var fade = (int)(fadeMs * AudioFormat.SampleRate / 1000);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            double v = 0;
            foreach (var (hz, db) in tones)
                v += Math.Pow(10, db / 20) * Math.Sin(2 * Math.PI * hz * i / AudioFormat.SampleRate);
            var g = Math.Min(1.0, Math.Min((i + 1) / (double)fade, (frames - i) / (double)fade));
            samples[2 * i] = samples[(2 * i) + 1] = (float)(v * g);
        }

        return DecodedSound.FromSamples(samples);
    }
}
