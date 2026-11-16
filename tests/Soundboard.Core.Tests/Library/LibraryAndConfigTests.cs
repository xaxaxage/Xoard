using Soundboard.Core.Audio;
using Soundboard.Core.Config;
using Soundboard.Core.Library;

namespace Soundboard.Core.Tests.Library;

public sealed class LibraryAndConfigTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("sb-lib-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Deleted { get; } = [];

        public void Delete(string path)
        {
            Deleted.Add(path);
            File.Delete(path);
        }
    }

    private string Source(string name)
    {
        var dir = Path.Combine(root, "src");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private SoundLibrary NewLibrary(FakeRecycleBin? bin = null) => new(Path.Combine(root, "Sounds"), bin ?? new FakeRecycleBin());

    [Fact]
    public void Import_copies_supported_files_and_skips_others()
    {
        var lib = NewLibrary();
        var (added, skipped) = lib.Import([Source("a.mp3"), Source("b.txt"), Source("c.WAV")]);

        Assert.Equal(["a", "c"], added.Select(a => a.Name).Order());
        Assert.Single(skipped);
        Assert.True(File.Exists(Path.Combine(lib.Folder, "a.mp3")));
        Assert.True(File.Exists(Source("a.mp3")));   // оригинал остался
    }

    [Fact]
    public void Import_gives_unique_names()
    {
        var lib = NewLibrary();
        lib.Import([Source("boom.mp3")]);
        var (added, _) = lib.Import([Source("boom.mp3")]);

        Assert.Equal("boom (2)", Assert.Single(added).Name);
    }

    [Fact]
    public void Scan_keeps_saved_order_then_alphabetical()
    {
        var lib = NewLibrary();
        lib.Import([Source("b.mp3"), Source("a.mp3"), Source("c.mp3"), Source("d.mp3")]);

        var list = lib.Scan(["c.mp3", "gone.mp3", "a.mp3"]);

        Assert.Equal(["c", "a", "b", "d"], list.Select(s => s.Name));
    }

    [Fact]
    public void Rename_moves_file_and_keeps_extension()
    {
        var lib = NewLibrary();
        var file = lib.Import([Source("old.mp3")]).Added[0];

        var renamed = lib.Rename(file, "  Новый звук ");

        Assert.Equal("Новый звук", renamed.Name);
        Assert.Equal("Новый звук.mp3", renamed.FileName);
        Assert.False(File.Exists(file.Path));
        Assert.True(File.Exists(renamed.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("dot.")]
    public void Rename_rejects_bad_names(string name)
    {
        var lib = NewLibrary();
        var file = lib.Import([Source("x.mp3")]).Added[0];
        Assert.Throws<SoundLibraryException>(() => lib.Rename(file, name));
    }

    [Fact]
    public void Rename_to_existing_name_is_refused_but_case_change_is_allowed()
    {
        var lib = NewLibrary();
        var files = lib.Import([Source("one.mp3"), Source("two.mp3")]).Added;

        Assert.Throws<SoundLibraryException>(() => lib.Rename(files[0], "two"));
        Assert.Equal("ONE", lib.Rename(files[0], "ONE").Name);
    }

    [Fact]
    public void Delete_goes_to_recycle_bin()
    {
        var bin = new FakeRecycleBin();
        var lib = NewLibrary(bin);
        var file = lib.Import([Source("bye.mp3")]).Added[0];

        lib.Delete(file);

        Assert.Equal([file.Path], bin.Deleted);
        Assert.Empty(lib.Scan([]));
    }

    [Fact]
    public void Config_round_trips_and_missing_file_gives_defaults()
    {
        var store = new ConfigStore(Path.Combine(root, "cfg", "settings.json"));
        Assert.Equal(AudioSettings.Default, store.Load().Audio);

        var config = new AppConfig
        {
            Audio = AudioSettings.Default with { CableLevel = 0.5f, PlaybackMode = PlaybackMode.Restart, MicDeviceId = "mic-1" },
            SoundOrder = ["b.mp3", "a.mp3"],
            AnimationsEnabled = false,
            Window = new WindowPlacement(10, 20, 900, 600, false),
        };
        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(config.Audio, loaded.Audio);
        Assert.Equal(config.SoundOrder, loaded.SoundOrder);
        Assert.False(loaded.AnimationsEnabled);
        Assert.Equal(config.Window, loaded.Window);
        Assert.Contains("\"playbackMode\": \"Restart\"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Corrupt_config_is_backed_up_and_defaults_used()
    {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new ConfigStore(path);

        var config = store.Load();

        Assert.Equal(AudioSettings.Default, config.Audio);
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(root, "settings.json.bad-*"));
    }

    [Fact]
    public void Invalid_values_in_config_fall_back_to_defaults()
    {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, """{ "audio": { "cableLevel": 7 } }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(AudioSettings.Default, config.Audio);
    }
}
