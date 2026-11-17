using System.Diagnostics;
using NAudio.CoreAudioApi;
using Soundboard.Core.Audio.Devices;
using Soundboard.Core.Audio.Sounds;
using Soundboard.Core.Recording;

namespace Soundboard.Poc;

/// <summary>
/// recording-selftest: повтор пишет звук других программ, но не свой.
/// Этот процесс играет 1000 Гц, отдельный процесс — 700 Гц (оба в CABLE Input, не в наушники);
/// в сохранённом повторе должно быть 700 Гц и не должно быть 1000 Гц.
/// </summary>
internal static class RecordingSelfTest
{
    public static int Run(CliOptions options)
    {
        using (var catalog = new MmDeviceCatalog())
        {
            var capture = VbCable.SelectCapture(catalog.Snapshot().Inputs)
                ?? throw new VbCableNotFoundException("Нужен VB-CABLE: тоны играются в CABLE Input, чтобы не звучать в наушниках.");
            var listeners = catalog.ListeningApps(capture.Id);
            if (listeners.Count > 0 && !options.Force)
            {
                Ui.Error($"CABLE Output сейчас слушают: {string.Join(", ", listeners.Select(l => l.ProcessName))} — тоны услышат собеседники.");
                return 6;
            }
        }

        var folder = Directory.CreateTempSubdirectory("sb-replay-").FullName;
        var service = new RecordingService();
        try
        {
            service.ConfigureReplayAsync(true, 10).GetAwaiter().GetResult();
            if (!service.ReplayEnabled)
            {
                Ui.Error($"Повтор не включился: {service.LastError}");
                return 3;
            }

            Ui.Heading("Самопроверка повтора");
            Ui.Info($"  Без звуков саундборда (петля процессов): {(service.ExcludesOwnSounds ? "да" : "нет — старая Windows, пишется всё")}");
            Thread.Sleep(500);

            // Куда играть: --device (часть имени), по умолчанию CABLE Input; громкость --volume
            var deviceName = options.Device ?? "CABLE Input";
            var volume = options.Volume ?? 0.25f;
            var playedDb = 20 * Math.Log10(volume);
            Ui.Info($"  Тоны: «{deviceName}», {playedDb:0} dBFS");

            // Чужой процесс: 700 Гц. Дочерний процесс входит в наше «дерево» и тоже исключается петлёй,
            // поэтому при «external» его запускает снаружи тот, кто запустил проверку (не наш потомок).
            var external = options.Positional.Contains("external");
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            using var other = external ? null : Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                $"tone --device \"{deviceName}\" --freq 700 --volume {volume.ToString(culture)} --seconds 2 --preroll 0 --postroll 0")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (external)
                Ui.Info("  Жду 700 Гц от внешнего процесса…");

            // Свой процесс: 1000 Гц туда же
            using (var enumerator = new MMDeviceEnumerator())
            {
                var target = AudioDevices.FindRenderByName(enumerator, deviceName);
                Player.PlayToEnd([new PlaybackTarget(target, Signals.CreateTone(1000, volume, 2))], 50);
            }

            other?.WaitForExit(10_000);
            Thread.Sleep(external ? 1500 : 300);

            var path = service.SaveReplayAsync(4, folder, "test").GetAwaiter().GetResult();
            var sound = SoundDecoder.Decode(path, TimeSpan.FromMinutes(1));
            var otherDb = Level(sound.Samples, 700);
            var ownDb = Level(sound.Samples, 1000);
            Ui.Info($"  В повторе: чужой звук 700 Гц {otherDb:0.0} dBFS, свой звук 1000 Гц {ownDb:0.0} dBFS, длина {sound.Duration.TotalSeconds:0.0} с");

            // Свой тон, если бы попал, был бы около playedDb; ниже на 12+ дБ — только утечка от чужого тона
            var ok = otherDb > playedDb - 6 && (!service.ExcludesOwnSounds || ownDb < playedDb - 12);
            if (ok)
                Ui.Ok("✓ Повтор пишет звук других программ" + (service.ExcludesOwnSounds ? " и не пишет звуки саундборда." : "."));
            else
                Ui.Error("✗ Повтор записал не то, что ожидалось (см. уровни выше).");
            return ok ? 0 : 5;
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Наибольший уровень частоты (Гёрцель по окнам 100 мс), dBFS.</summary>
    private static double Level(float[] stereo, double hz)
    {
        const int window = 4800;
        var coeff = 2 * Math.Cos(2 * Math.PI * hz / 48_000);
        var best = 0.0;
        for (var start = 0; start + window <= stereo.Length / 2; start += window)
        {
            double s1 = 0, s2 = 0;
            for (var i = 0; i < window; i++)
            {
                var s = stereo[2 * (start + i)] + (coeff * s1) - s2;
                s2 = s1;
                s1 = s;
            }

            var amplitude = 2 * Math.Sqrt(Math.Max((s1 * s1) + (s2 * s2) - (coeff * s1 * s2), 0)) / window;
            best = Math.Max(best, amplitude);
        }

        return 20 * Math.Log10(Math.Max(best, 1e-9));
    }
}
