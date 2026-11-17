using Microsoft.Win32;
using Xoard.Core.Audio;
using Xoard.Core.Audio.Devices;
using Xoard.Core.Audio.Status;
using Xoard.Core.Config;
using Xoard.Core.Shell;

namespace Xoard.Core.Tests.Shell;

public sealed class AutostartTests : IDisposable
{
    // Отдельный ключ HKCU — настоящий автозапуск Windows не трогаем
    private readonly string root = @"Software\Xoard.Tests\" + Guid.NewGuid().ToString("N");
    private readonly Autostart autostart;

    public AutostartTests() => autostart = new Autostart("Xoard", root + @"\Run", root + @"\Approved");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);

    [Fact]
    public void Enable_writes_quoted_path_with_tray_argument_and_disable_removes_it()
    {
        Assert.False(autostart.IsEnabled);
        Assert.Null(autostart.Command);

        autostart.Enable(@"C:\Program Files\Xoard\Xoard.exe");
        Assert.True(autostart.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\Xoard\\Xoard.exe\" --tray", autostart.Command);

        autostart.Disable();
        Assert.False(autostart.IsEnabled);
        Assert.Null(autostart.Command);
        autostart.Disable();   // повторно — без ошибок
    }

    [Fact]
    public void Disabled_in_task_manager_counts_as_off_and_enable_clears_it()
    {
        autostart.Enable(@"C:\Xoard\Xoard.exe");
        using (var key = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
            key.SetValue("Xoard", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(autostart.DisabledByUser);
        Assert.False(autostart.IsEnabled);

        autostart.Enable(@"C:\Xoard\Xoard.exe");
        Assert.False(autostart.DisabledByUser);
        Assert.True(autostart.IsEnabled);

        using (var key = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
            key.SetValue("Xoard", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(autostart.IsEnabled);   // чётный байт — включено
    }

    [Fact]
    public void MigrateFrom_moves_a_working_old_entry_and_drops_a_disabled_one()
    {
        var old = new Autostart("Soundboard", root + @"\Run", root + @"\Approved");
        Assert.False(autostart.MigrateFrom("Soundboard", @"C:\Xoard\Xoard.exe"));   // старой записи нет

        old.Enable(@"C:\Old\Soundboard.exe");
        Assert.True(autostart.MigrateFrom("Soundboard", @"C:\Xoard\Xoard.exe"));
        Assert.Null(old.Command);
        Assert.Equal("\"C:\\Xoard\\Xoard.exe\" --tray", autostart.Command);

        // Отключённый в диспетчере задач старый автозапуск не включаем под новым именем
        autostart.Disable();
        old.Enable(@"C:\Old\Soundboard.exe");
        using (var key = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
            key.SetValue("Soundboard", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.True(autostart.MigrateFrom("Soundboard", @"C:\Xoard\Xoard.exe"));
        Assert.Null(old.Command);
        Assert.Null(autostart.Command);
    }

    [Fact]
    public void UpdatePath_rewrites_only_a_moved_program()
    {
        Assert.False(autostart.UpdatePath(@"C:\New\Xoard.exe"));   // автозапуск выключен — не включаем
        Assert.Null(autostart.Command);

        autostart.Enable(@"C:\Old\Xoard.exe");
        Assert.False(autostart.UpdatePath(@"C:\Old\Xoard.exe"));
        Assert.True(autostart.UpdatePath(@"C:\New\Xoard.exe"));
        Assert.Equal("\"C:\\New\\Xoard.exe\" --tray", autostart.Command);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void Second_copy_is_refused_and_asks_the_first_to_show_itself()
    {
        var name = "Xoard.Tests." + Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim();

        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        first.ActivationRequested += (_, _) => activated.Set();

        Assert.Null(SingleInstance.TryAcquire(name));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken), "первая копия не получила просьбу показаться");

        first.Dispose();
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);   // после выхода первой копии можно запуститься снова
    }
}

public class SetupChecksTests
{
    private static VbCableStatus Cable(params string[] listeners) =>
        new(true, "CABLE Input (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)",
            listeners.Select((n, i) => new ListeningApp(100 + i, n)).ToList());

    private static readonly VbCableStatus NoCable = new(false, null, null, []);

    [Theory]
    [InlineData("Discord", true)]
    [InlineData("DiscordPTB", true)]
    [InlineData("discordcanary", true)]
    [InlineData("obs64", false)]
    [InlineData("Vesktop", false)]
    public void Recognizes_discord_processes(string name, bool expected) => Assert.Equal(expected, SetupChecks.IsDiscord(name));

    [Fact]
    public void Cable_check()
    {
        Assert.Equal(CheckState.Ok, SetupChecks.Cable(Cable()).State);
        Assert.Equal(CheckState.Error, SetupChecks.Cable(NoCable).State);
    }

    [Fact]
    public void Discord_check_tells_listening_from_running_and_not_running()
    {
        Assert.Equal(CheckState.Ok, SetupChecks.Discord(Cable("obs64", "DiscordPTB"), discordRunning: true).State);

        var notListening = SetupChecks.Discord(Cable("obs64"), discordRunning: true);
        Assert.Equal(CheckState.Warning, notListening.State);
        Assert.Contains("не слушает", notListening.Text, StringComparison.Ordinal);

        Assert.Contains("не запущен", SetupChecks.Discord(Cable(), discordRunning: false).Text, StringComparison.Ordinal);
        Assert.Contains("VB-CABLE", SetupChecks.Discord(NoCable, discordRunning: true).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_output_check()
    {
        Assert.Equal(CheckState.Ok, SetupChecks.DefaultOutput([]).State);
        var issue = IssueText.Create(IssueCode.DefaultOutputIsVbCable, "CABLE Input");
        Assert.Equal(CheckState.Error, SetupChecks.DefaultOutput([issue]).State);
    }
}

public sealed class LegacyMigrationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("xoard-migrate-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Moves_the_old_folder_with_everything_inside()
    {
        var legacy = Path.Combine(root, "Soundboard");
        Directory.CreateDirectory(Path.Combine(legacy, "Sounds"));
        File.WriteAllText(Path.Combine(legacy, "Sounds", "Wow.wav"), "x");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        var current = Path.Combine(root, "Xoard");

        Assert.Equal(current, LegacyMigration.MoveFolder(legacy, current));
        Assert.True(File.Exists(Path.Combine(current, "Sounds", "Wow.wav")));
        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Leaves_everything_alone_when_the_new_folder_already_exists_or_there_is_nothing_to_move()
    {
        var legacy = Path.Combine(root, "Soundboard");
        var current = Path.Combine(root, "Xoard");
        Assert.Equal(current, LegacyMigration.MoveFolder(legacy, current));   // ничего нет — ничего не создаём
        Assert.False(Directory.Exists(current));

        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "old.txt"), "old");
        Directory.CreateDirectory(current);
        Assert.Equal(current, LegacyMigration.MoveFolder(legacy, current));
        Assert.True(File.Exists(Path.Combine(legacy, "old.txt")));             // новые данные важнее — старые не трогаем
        Assert.False(File.Exists(Path.Combine(current, "old.txt")));
    }

    [Fact]
    public void Copies_when_the_old_folder_is_held_open()
    {
        var legacy = Path.Combine(root, "Soundboard");
        Directory.CreateDirectory(legacy);
        var file = Path.Combine(legacy, "settings.json");
        File.WriteAllText(file, "{}");
        var current = Path.Combine(root, "Xoard");

        // Открытый файл не даёт переместить папку — файлы копируются, старые остаются
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(current, LegacyMigration.MoveFolder(legacy, current));
        Assert.True(File.Exists(Path.Combine(current, "settings.json")));
        Assert.True(File.Exists(file));
    }
}

public class ShellConfigTests
{
    [Fact]
    public void New_settings_have_sensible_defaults_and_survive_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "sb-shell-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new ConfigStore(path);
            var config = store.Load();
            Assert.True(config.CloseToTray);
            Assert.False(config.TrayHintShown);
            Assert.False(config.SetupGuideShown);

            store.Save(config with { CloseToTray = false, TrayHintShown = true, SetupGuideShown = true });
            var loaded = new ConfigStore(path).Load();
            Assert.False(loaded.CloseToTray);
            Assert.True(loaded.TrayHintShown);
            Assert.True(loaded.SetupGuideShown);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
