using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xoard.Mcp;
using Xoard.Protocol;

// MCP-сервер Xoard для Claude Desktop: stdio-транспорт; stdout занят протоколом MCP,
// поэтому журнал — только в stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// XOARD_PIPE — другое имя канала (для проверок)
var pipe = Environment.GetEnvironmentVariable("XOARD_PIPE");
builder.Services.AddSingleton(new ControlClient(string.IsNullOrWhiteSpace(pipe) ? ControlProtocol.PipeName : pipe));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<XoardTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
