using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundboard.Poc;

internal static class Commands
{
    public static void PrintHelp()
    {
        Ui.Heading("Soundboard.Poc — проверка вывода звука в VB-CABLE (этап 0)");
        Ui.Info("""

            Команды:
              list                    показать устройства вывода и ввода
              play <файл> [опции]     проиграть mp3/wav/flac/… в VB-CABLE
              tone [опции]            тестовый тон в VB-CABLE (по умолчанию 440 Гц, 2 с)
              selftest [опции]        сыграть тон в CABLE Input и проверить, что он пришёл в CABLE Output

            Этап 1 — движок:
              engine-devices          устройства и план движка: наушники, VB-CABLE, микрофон, проблемы
              engine [опции]          интерактивный режим: звуки, голос в кабель, ползунки (клавиша h — справка)
                --sounds <папка>        до 10 звуков на клавиши 1..0
                --headphones <имя>      наушники (по умолчанию — устройство Windows)
                --mic <имя> | --no-mic  микрофон (по умолчанию — микрофон Windows) или без голоса
                --profile default|safe|low   размер буферов
              engine-selftest [опции] автопроверка движка через VB-CABLE (без наушников и микрофона)
                --load <N>              под нагрузкой: N занятых ядер + давление на GC
                --runs <N>              повторить N раз;  --force — даже если CABLE Output кто-то слушает

            Опции:
              --device <часть имени>  вывести не в VB-CABLE, а в другое устройство
              --also-default          одновременно вывести в устройство по умолчанию (наушники)
              --volume <0..1>         громкость (play: 1.0, tone: 0.3, selftest: 0.25)
              --seconds <n>           длительность тона
              --freq <Гц>             частота тона
              --latency <мс>          буфер WASAPI, по умолчанию 50
              --preroll <мс>          тишина до звука, по умолчанию 100
              --postroll <мс>         тишина после звука, по умолчанию 200
                                      (без них VB-CABLE съедает начало и конец звука,
                                      если его поток стартует/останавливается «холодным»)

            Примеры:
              .\poc.cmd selftest
              .\poc.cmd play "C:\Звуки\bruh.mp3" --also-default
            """);
    }

    public static void PrintVbCableMissing(string reason)
    {
        Ui.Error(reason);
        Ui.Info("Установите бесплатный VB-CABLE: " + AudioDevices.VbCableDownloadUrl);
        Ui.Info("После установки перезагрузите компьютер и запустите: .\\poc.cmd list");
    }

    public static int List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultRender = AudioDevices.DefaultRender(enumerator)?.ID;
        var defaultCapture = AudioDevices.DefaultCapture(enumerator)?.ID;
        var cableRender = TryFind(() => AudioDevices.FindVbCableRender(enumerator));
        var cableCapture = TryFind(() => AudioDevices.FindVbCableCapture(enumerator));

        PrintSection("Устройства вывода", AudioDevices.ActiveDevices(enumerator, DataFlow.Render), defaultRender, cableRender?.ID,
            "VB-CABLE → сюда играем звуки");
        PrintSection("Устройства ввода", AudioDevices.ActiveDevices(enumerator, DataFlow.Capture), defaultCapture, cableCapture?.ID,
            "VB-CABLE → это «микрофон» для Discord");

        if (cableRender is null || cableCapture is null)
        {
            PrintVbCableMissing("VB-CABLE не найден или отключён.");
            return 2;
        }

        Ui.Ok($"VB-CABLE найден: играем в «{cableRender.FriendlyName}», Discord слушает «{cableCapture.FriendlyName}».");

        if (defaultRender is not null && AudioDevices.IsVbCable(AudioDevices.DefaultRender(enumerator)!))
        {
            Ui.Info("");
            Ui.Warn("! Устройство вывода по умолчанию в Windows — VB-CABLE. Тогда весь звук компьютера");
            Ui.Warn("  (игры, браузер, голоса из Discord) идёт в «микрофон», и друзья слышат себя эхом.");
            Ui.Warn("  Параметры → Система → Звук → Вывод: выберите наушники.");
        }

        return 0;
    }

    public static int Play(CliOptions options)
    {
        if (options.Positional.Count != 1)
        {
            throw new CliException("Укажите один файл: Soundboard.Poc play <файл>");
        }

        var path = Path.GetFullPath(options.Positional[0]);
        if (!File.Exists(path))
        {
            throw new CliException($"Файл не найден: {path}");
        }

        using var enumerator = new MMDeviceEnumerator();
        var devices = ResolveTargets(enumerator, options);
        var volume = options.Volume ?? 1f;

        using (var probe = OpenFile(path))
        {
            Ui.Info($"Файл:  {Path.GetFileName(path)}  ({InternalFormat.Describe(probe.WaveFormat)}, {probe.TotalTime:mm\\:ss\\.f})");
            Ui.Info($"       → {InternalFormat.Describe(InternalFormat.WaveFormat)}");
        }

        // У каждого выхода свой экземпляр читателя: позиции воспроизведения независимы
        var readers = new List<AudioFileReader>();
        try
        {
            var targets = devices.Select(device =>
            {
                var reader = OpenFile(path);
                reader.Volume = volume;
                readers.Add(reader);
                return new PlaybackTarget(device, InternalFormat.Convert(reader));
            }).ToList();

            return RunPlayback(targets, options);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    public static int Tone(CliOptions options)
    {
        var frequency = options.Frequency ?? 440;
        var seconds = options.Seconds ?? 2;
        var gain = options.Volume ?? 0.3f;

        using var enumerator = new MMDeviceEnumerator();
        var devices = ResolveTargets(enumerator, options);
        Ui.Info($"Тон:   {frequency:0} Гц, {seconds:0.0} с, громкость {gain:0.00}");

        var targets = devices.Select(d => new PlaybackTarget(d, Signals.CreateTone(frequency, gain, seconds))).ToList();
        return RunPlayback(targets, options);
    }

    private static int RunPlayback(List<PlaybackTarget> targets, CliOptions options)
    {
        Ui.Dim($"Буфер WASAPI: {options.LatencyMs} мс, тишина до/после: {options.PreRollMs}/{options.PostRollMs} мс. Ctrl+C — остановить.");
        Ui.Info("▶ Играю…");
        var completed = Player.PlayToEnd(targets, options.LatencyMs,
            preRollMs: options.PreRollMs, postRollMs: options.PostRollMs);
        if (completed)
        {
            Ui.Ok("■ Готово.");
        }
        else
        {
            Ui.Warn("■ Остановлено.");
        }

        return 0;
    }

    /// <summary>Основной выход (VB-CABLE или --device) и, по желанию, устройство по умолчанию.</summary>
    private static List<MMDevice> ResolveTargets(MMDeviceEnumerator enumerator, CliOptions options)
    {
        var main = options.Device is null
            ? AudioDevices.FindVbCableRender(enumerator)
            : AudioDevices.FindRenderByName(enumerator, options.Device);
        var devices = new List<MMDevice> { main };

        if (options.AlsoDefault)
        {
            var fallback = AudioDevices.DefaultRender(enumerator);
            if (fallback is null)
            {
                Ui.Warn("! В Windows нет устройства вывода по умолчанию — --also-default пропущен.");
            }
            else if (fallback.ID == main.ID)
            {
                Ui.Warn("! Устройство по умолчанию совпадает с основным — --also-default пропущен.");
            }
            else if (AudioDevices.IsVbCable(fallback))
            {
                Ui.Warn("! Устройство вывода по умолчанию в Windows — VB-CABLE. Сделайте наушники устройством");
                Ui.Warn("  по умолчанию, иначе весь звук компьютера попадёт в микрофон. --also-default пропущен.");
            }
            else
            {
                devices.Add(fallback);
            }
        }

        foreach (var device in devices)
        {
            Ui.Info($"Вывод: {device.FriendlyName}");
        }

        return devices;
    }

    private static AudioFileReader OpenFile(string path)
    {
        try
        {
            return new AudioFileReader(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CliException($"Не удалось открыть «{Path.GetFileName(path)}»: {ex.Message}");
        }
    }

    private static void PrintSection(string title, List<MMDevice> devices, string? defaultId, string? cableId, string cableNote)
    {
        Ui.Heading(title + ":");
        foreach (var device in devices)
        {
            var marks = new List<string>();
            if (device.ID == defaultId)
            {
                marks.Add("по умолчанию");
            }

            if (device.ID == cableId)
            {
                marks.Add(cableNote);
            }
            else if (AudioDevices.IsVbCable(device))
            {
                marks.Add("VB-CABLE, не используется");
            }

            var format = AudioDevices.TryGetMixFormat(device) is { } f ? InternalFormat.Describe(f) : "формат неизвестен";
            var line = $"  {(device.ID == cableId ? "►" : " ")} {device.FriendlyName}";
            if (device.ID == cableId)
            {
                Ui.Ok(line);
            }
            else
            {
                Ui.Info(line);
            }

            Ui.Dim($"      {format}{(marks.Count > 0 ? "   [" + string.Join("; ", marks) + "]" : "")}");
        }

        Ui.Info("");
    }

    private static MMDevice? TryFind(Func<MMDevice> find)
    {
        try
        {
            return find();
        }
        catch (VbCableNotFoundException)
        {
            return null;
        }
    }
}
