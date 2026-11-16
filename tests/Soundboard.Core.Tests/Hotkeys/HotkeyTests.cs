using Soundboard.Core.Config;
using Soundboard.Core.Hotkeys;

namespace Soundboard.Core.Tests.Hotkeys;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F1", "Ctrl+Alt+F1")]
    [InlineData("ctrl+shift+a", "Ctrl+Shift+A")]
    [InlineData("Alt+Control+5", "Ctrl+Alt+5")]
    [InlineData("Num5", "Num5")]
    [InlineData("Shift+Num+", "Shift+Num+")]
    [InlineData("Win+Pause", "Win+Pause")]
    [InlineData(" F9 ", "F9")]
    public void Parses_and_formats(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(expected, g.ToString());
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var again));
        Assert.Equal(g, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+Alt+")]
    [InlineData(null)]
    public void Rejects_bad_text(string? text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Modifier_only_gesture_is_invalid()
    {
        Assert.False(new HotkeyGesture(HotkeyModifiers.Control, 0x11).IsValid);
        Assert.True(new HotkeyGesture(HotkeyModifiers.None, 0x70).IsValid);
    }

    [Fact]
    public void Registers_and_delivers_press()
    {
        using var manager = new HotkeyManager();
        using var pressed = new ManualResetEventSlim();
        string? name = null;
        manager.Pressed += (_, e) =>
        {
            name = e.Name;
            pressed.Set();
        };

        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x86); // F23
        Assert.Equal(HotkeyResult.Registered, manager.Register("sound:a.mp3", gesture));
        Assert.Equal(gesture, manager.Registered["sound:a.mp3"]);

        Assert.True(manager.SimulatePress("sound:a.mp3"));
        Assert.True(pressed.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken), "нажатие не дошло");
        Assert.Equal("sound:a.mp3", name);
    }

    [Fact]
    public void Taken_combination_is_reported_and_freed_after_unregister()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87); // F24
        using var first = new HotkeyManager();
        using var second = new HotkeyManager();

        Assert.Equal(HotkeyResult.Registered, first.Register("stop", gesture));
        Assert.Equal(HotkeyResult.InUse, second.Register("stop", gesture));

        first.Unregister("stop");
        Assert.Equal(HotkeyResult.Registered, second.Register("stop", gesture));
        second.UnregisterAll();
        Assert.Empty(second.Registered);
    }

    [Fact]
    public void Invalid_gesture_is_not_registered()
    {
        using var manager = new HotkeyManager();
        Assert.Equal(HotkeyResult.Invalid, manager.Register("x", new HotkeyGesture(HotkeyModifiers.Control, 0x11)));
    }

    [Fact]
    public void Hotkeys_are_saved_in_config()
    {
        var dir = Directory.CreateTempSubdirectory("sb-hk-").FullName;
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "settings.json"));
            store.Save(new AppConfig
            {
                SoundHotkeys = new Dictionary<string, string> { ["boom.mp3"] = "Ctrl+Alt+1" },
                StopAllHotkey = "Pause",
            });

            var loaded = store.Load();
            Assert.Equal("Ctrl+Alt+1", loaded.SoundHotkeys["boom.mp3"]);
            Assert.Equal("Pause", loaded.StopAllHotkey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
