using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Xoard.Protocol;

/// <summary>Xoard не запущен (канал недоступен).</summary>
public sealed class XoardUnavailableException : Exception
{
    public XoardUnavailableException()
        : base("Xoard не запущен. Откройте приложение Xoard и повторите.")
    {
    }

    public XoardUnavailableException(string message)
        : base(message)
    {
    }

    public XoardUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Клиент протокола: одно соединение на запрос (просто и надёжно).</summary>
public sealed class ControlClient(string pipeName = ControlProtocol.PipeName)
{
    /// <summary>Сколько ждать подключения к приложению.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Сколько ждать ответа.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Отправить команду и получить результат. Ошибки — <see cref="ControlException"/> / <see cref="XoardUnavailableException"/>.</summary>
    public async Task<T?> SendAsync<T>(string command, object? args, CancellationToken ct)
    {
        var result = await SendAsync(command, args, ct).ConfigureAwait(false);
        return result is { } element ? element.Deserialize<T>(ControlProtocol.Json) : default;
    }

    /// <summary>Отправить команду и получить «сырой» результат.</summary>
    public async Task<JsonElement?> SendAsync(string command, object? args, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new XoardUnavailableException();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw new XoardUnavailableException("Не удалось подключиться к Xoard: " + ex.Message, ex);
        }

        var request = new ControlRequest(
            Guid.NewGuid().ToString("N"),
            command,
            args is null ? null : JsonSerializer.SerializeToElement(args, ControlProtocol.Json));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ResponseTimeout);
        await LineFraming.WriteAsync(pipe, request, cts.Token).ConfigureAwait(false);
        var response = await LineFraming.ReadAsync<ControlResponse>(pipe, cts.Token).ConfigureAwait(false)
            ?? throw new XoardUnavailableException("Xoard закрыл соединение, не ответив.");

        if (!response.Ok)
            throw new ControlException(response.Error?.Code ?? ControlErrorCodes.Internal, response.Error?.Message ?? "Неизвестная ошибка.");
        return response.Result;
    }
}

/// <summary>Кадры протокола: JSON-объект + перевод строки, UTF-8, с ограничением размера.</summary>
public static class LineFraming
{
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ControlProtocol.Json);
        if (bytes.Length > ControlProtocol.MaxMessageBytes)
            throw new ControlException(ControlErrorCodes.BadRequest, "Сообщение слишком большое.");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Читает одно сообщение; null — соединение закрыто.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        return line is null ? default : JsonSerializer.Deserialize<T>(line, ControlProtocol.Json);
    }

    /// <summary>Читает строку (без перевода строки); null — конец потока.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.WriteByte(one[0]);
            if (buffer.Length > ControlProtocol.MaxMessageBytes)
                throw new ControlException(ControlErrorCodes.BadRequest, "Сообщение слишком большое.");
        }
    }
}
