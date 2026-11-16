using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Soundboard.Core.Control;

namespace Soundboard.Core.Tests.Control;

/// <summary>
/// Сквозная проверка: собранный Soundboard.Mcp.exe запускается как у Claude Desktop (stdio),
/// инструменты вызываются официальным MCP-клиентом, команды доходят до «приложения» по named pipe.
/// </summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string pipe = "Soundboard.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeSoundboard fake = new();
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
        // artifacts\bin\Soundboard.Core.Tests\<конфигурация>\ → artifacts\bin\Soundboard.Mcp\<конфигурация>\
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = Path.Combine(testDir.Parent!.Parent!.FullName, "Soundboard.Mcp", testDir.Name, "Soundboard.Mcp.exe");
        Assert.True(File.Exists(path), $"не найден {path}");
        return path;
    }

    private static async Task<McpClient> StartAsync(string pipeName)
    {
        // .NET установлен в папку пользователя — подсказываем exe, где он
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "soundboard",
            Command = McpExe(),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["SOUNDBOARD_PIPE"] = pipeName,
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

        Assert.Equal(["get_status", "list_sounds", "play_sound", "set_volume", "stop_all"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
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
        await using var client = await StartAsync("Soundboard.Test.Nobody." + Guid.NewGuid().ToString("N"));

        var result = await client.CallToolAsync("get_status", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Soundboard не запущен", Text(result), StringComparison.Ordinal);
    }
}
