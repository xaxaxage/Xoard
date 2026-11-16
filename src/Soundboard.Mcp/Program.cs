using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soundboard.Mcp;
using Soundboard.Protocol;

// MCP-сервер Soundboard для Claude Desktop: stdio-транспорт; stdout занят протоколом MCP,
// поэтому журнал — только в stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// SOUNDBOARD_PIPE — другое имя канала (для проверок)
var pipe = Environment.GetEnvironmentVariable("SOUNDBOARD_PIPE");
builder.Services.AddSingleton(new ControlClient(string.IsNullOrWhiteSpace(pipe) ? ControlProtocol.PipeName : pipe));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SoundboardTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
