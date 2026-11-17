using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xoard.Core.Control;

namespace Xoard.Core.Tests.Control;

/// <summary>
/// Сквозная проверка: собранный Xoard.Mcp.exe запускается как у Claude Desktop (stdio),
/// инструменты вызываются официальным MCP-клиентом, команды доходят до «приложения» по named pipe.
/// </summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string pipe = "Xoard.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeXoard fake = new();
    private ControlServer? server;

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

    private static string McpExe()
    {
        // artifacts\bin\Xoard.Core.Tests\<конфигурация>\ → artifacts\bin\Xoard.Mcp\<конфигурация>\
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = Path.Combine(testDir.Parent!.Parent!.FullName, "Xoard.Mcp", testDir.Name, "Xoard.Mcp.exe");
        Assert.True(File.Exists(path), $"не найден {path}");
        return path;
    }

    private static async Task<McpClient> StartAsync(string pipeName)
    {
        // .NET установлен в папку пользователя — подсказываем exe, где он
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "xoard",
            Command = McpExe(),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["XOARD_PIPE"] = pipeName,
                ["DOTNET_ROOT"] = dotnetRoot,
            },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Exposes_the_five_tools()
    {
        await using var client = await StartAsync(pipe);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            ["delete_sound", "get_status", "list_sounds", "play_sound", "rename_sound", "save_last_seconds", "set_hotkey",
             "set_volume", "start_recording", "stop_all", "stop_recording", "trim_sound"],
            tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task Stage5_tools_reach_the_app()
    {
        await using var client = await StartAsync(pipe);

        await client.CallToolAsync("set_hotkey", new Dictionary<string, object?> { ["target"] = "replay", ["hotkey"] = "Ctrl+Alt+R" }, cancellationToken: Ct);
        await client.CallToolAsync("start_recording", new Dictionary<string, object?> { ["source"] = "mic" }, cancellationToken: Ct);
        var stopped = await client.CallToolAsync("stop_recording", new Dictionary<string, object?> { ["name"] = "Мой смех" }, cancellationToken: Ct);
        var replay = await client.CallToolAsync("save_last_seconds", new Dictionary<string, object?> { ["seconds"] = 10, ["name"] = "Момент" }, cancellationToken: Ct);
        await client.CallToolAsync("rename_sound", new Dictionary<string, object?> { ["name"] = "air", ["newName"] = "Гудок" }, cancellationToken: Ct);
        await client.CallToolAsync("delete_sound", new Dictionary<string, object?> { ["id"] = "airhorn.mp3" }, cancellationToken: Ct);
        var trimmed = await client.CallToolAsync("trim_sound", new Dictionary<string, object?> { ["name"] = "air", ["start"] = 0.5, ["end"] = 2 }, cancellationToken: Ct);
        var badTrim = await client.CallToolAsync("trim_sound", new Dictionary<string, object?> { ["name"] = "air", ["start"] = 2, ["end"] = 1 }, cancellationToken: Ct);

        Assert.Contains("Мой смех", Text(stopped), StringComparison.Ordinal);
        Assert.Contains("Момент", Text(replay), StringComparison.Ordinal);
        Assert.Contains("\"durationSeconds\": 1.5", Text(trimmed), StringComparison.Ordinal);
        Assert.True(badTrim.IsError);
        Assert.Equal(
            ["hotkey:replay:Ctrl+Alt+R", "record:mic", "stop-record:Мой смех", "replay:10:Момент", "rename:air:Гудок",
             "delete:airhorn.mp3", "trim:air:0.5-2", "trim:air:2-1"],
            fake.Calls);
    }

    [Fact]
    public async Task Tools_reach_the_app_through_the_pipe()
    {
        await using var client = await StartAsync(pipe);

        var list = await client.CallToolAsync("list_sounds", cancellationToken: Ct);
        var play = await client.CallToolAsync("play_sound", new Dictionary<string, object?> { ["name"] = "air" }, cancellationToken: Ct);
        var volume = await client.CallToolAsync("set_volume", new Dictionary<string, object?> { ["target"] = "mic", ["percent"] = 40 }, cancellationToken: Ct);
        var stop = await client.CallToolAsync("stop_all", cancellationToken: Ct);
        var status = await client.CallToolAsync("get_status", cancellationToken: Ct);

        Assert.Contains("Ба-дум-тсс", Text(list), StringComparison.Ordinal);
        Assert.Contains("airhorn.mp3", Text(play), StringComparison.Ordinal);
        Assert.Contains("\"mic\": 40", Text(volume), StringComparison.Ordinal);
        Assert.NotEqual(true, stop.IsError);
        Assert.Contains("Discord", Text(status), StringComparison.Ordinal);
        Assert.Equal(["list", "play:air:False", "volume:mic:40", "stop", "status"], fake.Calls);
    }

    [Fact]
    public async Task App_errors_are_reported_to_claude_as_tool_errors()
    {
        await using var client = await StartAsync(pipe);

        var result = await client.CallToolAsync("play_sound", new Dictionary<string, object?> { ["name"] = "нет такого" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("не найден", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_app_is_a_clear_tool_error()
    {
        await using var client = await StartAsync("Xoard.Test.Nobody." + Guid.NewGuid().ToString("N"));

        var result = await client.CallToolAsync("get_status", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Xoard не запущен", Text(result), StringComparison.Ordinal);
    }
}
