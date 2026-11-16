using System.IO.Pipes;
using System.Text.Json;
using Soundboard.Protocol;

namespace Soundboard.Core.Control;

/// <summary>
/// Разбор команд протокола и вызов <see cref="ISoundboardControl"/> (не зависит от транспорта).
/// </summary>
public sealed class ControlDispatcher(ISoundboardControl control)
{
    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id ?? "";
        try
        {
            object? result = request.Command switch
            {
                ControlCommands.ListSounds => await control.ListSoundsAsync(ct).ConfigureAwait(false),
                ControlCommands.PlaySound => await control.PlaySoundAsync(Args<PlaySoundArgs>(request), ct).ConfigureAwait(false),
                ControlCommands.StopAll => await StopAllAsync(ct).ConfigureAwait(false),
                ControlCommands.SetVolume => await control.SetVolumeAsync(Args<SetVolumeArgs>(request), ct).ConfigureAwait(false),
                ControlCommands.GetStatus => await control.GetStatusAsync(ct).ConfigureAwait(false),
                _ => throw new ControlException(ControlErrorCodes.UnknownCommand, $"Неизвестная команда «{request.Command}»."),
            };
            return ControlResponse.Success(id, result);
        }
        catch (ControlException ex)
        {
            return ControlResponse.Failure(id, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return ControlResponse.Failure(id, ControlErrorCodes.BadRequest, $"Неверные аргументы: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return ControlResponse.Failure(id, ControlErrorCodes.Unavailable, "Команда отменена.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ControlResponse.Failure(id, ControlErrorCodes.Internal, ex.Message);
        }
    }

    private async Task<object> StopAllAsync(CancellationToken ct)
    {
        await control.StopAllAsync(ct).ConfigureAwait(false);
        return new { stopped = true };
    }

    private static T Args<T>(ControlRequest request)
    {
        if (request.Args is not { ValueKind: JsonValueKind.Object } args)
            throw new ControlException(ControlErrorCodes.BadRequest, "Нужны аргументы команды.");
        return args.Deserialize<T>(ControlProtocol.Json)
            ?? throw new ControlException(ControlErrorCodes.BadRequest, "Нужны аргументы команды.");
    }
}

/// <summary>
/// Сервер named pipe «Soundboard.Control»: принимает подключения только текущего пользователя
/// Windows (PipeOptions.CurrentUserOnly), каждый клиент — в своей задаче; запросы — строки JSON.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ControlDispatcher dispatcher;
    private readonly string pipeName;
    private readonly CancellationTokenSource cts = new();
    private Task? acceptLoop;

    public ControlServer(ISoundboardControl control, string pipeName = ControlProtocol.PipeName)
    {
        dispatcher = new ControlDispatcher(control);
        this.pipeName = pipeName;
    }

    /// <summary>Последняя ошибка цикла приёма (для статуса).</summary>
    public string? LastError { get; private set; }

    public void Start() => acceptLoop ??= Task.Run(() => AcceptLoopAsync(cts.Token));

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync().ConfigureAwait(false);
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановлен
            }
        }

        cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex)
            {
                // Имя занято другим экземпляром приложения — пробуем позже
                LastError = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                LastError = ex.Message;
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(pipe, ct), ct);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    string? line;
                    try
                    {
                        line = await LineFraming.ReadLineAsync(pipe, idle.Token).ConfigureAwait(false);
                    }
                    catch (ControlException ex)
                    {
                        await LineFraming.WriteAsync(pipe, ControlResponse.Failure("", ex.Code, ex.Message), ct).ConfigureAwait(false);
                        return;
                    }

                    if (line is null)
                        return;

                    ControlResponse response;
                    ControlRequest? request = null;
                    try
                    {
                        request = JsonSerializer.Deserialize<ControlRequest>(line, ControlProtocol.Json);
                    }
                    catch (JsonException)
                    {
                        // Ответим ошибкой ниже
                    }

                    response = request is null || string.IsNullOrEmpty(request.Command)
                        ? ControlResponse.Failure(request?.Id ?? "", ControlErrorCodes.BadRequest, "Неверный запрос: нужен JSON с полями id и command.")
                        : await dispatcher.HandleAsync(request, ct).ConfigureAwait(false);

                    await LineFraming.WriteAsync(pipe, response, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Клиент ушёл или сервер останавливается
            }
        }
    }
}
