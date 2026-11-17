using System.IO.Pipes;
using System.Text;
using Xoard.Core.Control;
using Xoard.Protocol;

namespace Xoard.Core.Tests.Control;

/// <summary>Сервер named pipe + клиент: настоящий канал с уникальным именем и поддельным приложением.</summary>
public sealed class ControlPipeTests : IAsyncLifetime
{
    private readonly string pipe = "Xoard.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeXoard fake = new();
    private ControlServer? server;

    private ControlClient Client => new(pipe);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        server = new ControlServer(fake, pipe);
        server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
            await server.DisposeAsync();
    }

    [Fact]
    public async Task List_sounds_round_trips()
    {
        var list = await Client.SendAsync<SoundList>(ControlCommands.ListSounds, null, Ct);

        Assert.NotNull(list);
        Assert.Equal(["Airhorn", "Ба-дум-тсс"], list.Sounds.Select(s => s.Name));
        Assert.Equal("Ctrl+1", list.Sounds[0].Hotkey);
    }

    [Fact]
    public async Task Play_sound_passes_arguments()
    {
        var info = await Client.SendAsync<PlayInfo>(ControlCommands.PlaySound, new PlaySoundArgs("air", null, true), Ct);

        Assert.Equal("airhorn.mp3", info?.Id);
        Assert.Contains("play:air:True", fake.Calls);
    }

    [Fact]
    public async Task Command_errors_come_back_with_code_and_russian_message()
    {
        var ex = await Assert.ThrowsAsync<ControlException>(() =>
            Client.SendAsync(ControlCommands.PlaySound, new PlaySoundArgs("нет такого"), Ct));

        Assert.Equal(ControlErrorCodes.NotFound, ex.Code);
        Assert.Contains("не найден", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_arguments_and_unknown_commands_are_rejected()
    {
        var noArgs = await Assert.ThrowsAsync<ControlException>(() => Client.SendAsync(ControlCommands.SetVolume, null, Ct));
        Assert.Equal(ControlErrorCodes.BadRequest, noArgs.Code);

        var unknown = await Assert.ThrowsAsync<ControlException>(() => Client.SendAsync("explode", null, Ct));
        Assert.Equal(ControlErrorCodes.UnknownCommand, unknown.Code);
    }

    [Fact]
    public async Task Set_volume_and_stop_all_work()
    {
        var volume = await Client.SendAsync<VolumeInfo>(ControlCommands.SetVolume, new SetVolumeArgs("cable", 35), Ct);
        await Client.SendAsync(ControlCommands.StopAll, null, Ct);

        Assert.Equal(35, volume?.Cable);
        Assert.Contains("stop", fake.Calls);
    }

    [Fact]
    public async Task Garbage_line_gets_bad_request_and_connection_survives()
    {
        await using var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(2000, Ct);

        await raw.WriteAsync(Encoding.UTF8.GetBytes("not json\n"), Ct);
        var bad = await LineFraming.ReadAsync<ControlResponse>(raw, Ct);
        Assert.False(bad?.Ok);
        Assert.Equal(ControlErrorCodes.BadRequest, bad?.Error?.Code);

        await LineFraming.WriteAsync(raw, new ControlRequest("2", ControlCommands.GetStatus), Ct);
        var good = await LineFraming.ReadAsync<ControlResponse>(raw, Ct);
        Assert.True(good?.Ok);
        Assert.Equal("2", good?.Id);
    }

    [Fact]
    public async Task Parallel_clients_are_served()
    {
        var tasks = Enumerable.Range(0, 10).Select(_ => Client.SendAsync<SoundList>(ControlCommands.ListSounds, null, Ct));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(2, r?.Sounds.Count));
    }

    [Fact]
    public async Task Missing_app_gives_clear_error_quickly()
    {
        var client = new ControlClient("Xoard.Test.Nobody." + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<XoardUnavailableException>(() => client.SendAsync(ControlCommands.GetStatus, null, Ct));

        Assert.Contains("Xoard не запущен", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }
}
