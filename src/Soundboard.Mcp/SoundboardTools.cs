using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Soundboard.Protocol;

namespace Soundboard.Mcp;

/// <summary>
/// Инструменты MCP: каждый — одна команда протокола, переданная в запущенное приложение Soundboard.
/// Ошибки (приложение не запущено, звук не найден…) возвращаются Claude как понятный текст.
/// </summary>
[McpServerToolType]
public sealed class SoundboardTools(ControlClient client)
{
    // Ответ читает модель, а не браузер: кириллица и символы — как есть, без \uXXXX
    private static readonly JsonSerializerOptions Pretty = new(ControlProtocol.Json)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "list_sounds", ReadOnly = true)]
    [Description("List the sounds on the user's soundboard: id (file name), name, duration, hotkey, whether it is playing.")]
    public Task<string> ListSounds(CancellationToken cancellationToken) =>
        CallAsync(ControlCommands.ListSounds, null, cancellationToken);

    [McpServerTool(Name = "play_sound")]
    [Description("Play a sound through the user's microphone (VB-CABLE), so friends hear it in Discord and games; " +
                 "the user also hears it in their headphones. Pass the sound name (exact or a unique part) or its id from list_sounds.")]
    public Task<string> PlaySound(
        [Description("Sound name or a unique part of it, e.g. \"airhorn\".")] string? name = null,
        [Description("Exact sound id (file name) from list_sounds; use instead of name.")] string? id = null,
        [Description("true = play only in the user's headphones (preview), friends do not hear it.")] bool previewOnly = false,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.PlaySound, new PlaySoundArgs(name, id, previewOnly), cancellationToken);

    [McpServerTool(Name = "stop_all")]
    [Description("Stop all sounds that are currently playing (short fade-out).")]
    public Task<string> StopAll(CancellationToken cancellationToken) =>
        CallAsync(ControlCommands.StopAll, null, cancellationToken);

    [McpServerTool(Name = "set_volume")]
    [Description("Set a volume slider. target: \"headphones\" (sounds in the user's headphones), " +
                 "\"cable\" (sounds that friends hear), \"mic\" (the user's own voice sent to friends). percent: 0-100.")]
    public Task<string> SetVolume(
        [Description("headphones, cable or mic")] string target,
        [Description("Volume in percent, 0-100.")] double percent,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.SetVolume, new SetVolumeArgs(target, percent), cancellationToken);

    [McpServerTool(Name = "get_status", ReadOnly = true)]
    [Description("Soundboard status: audio devices, whether VB-CABLE is installed and who listens to it, volumes, mode, what is playing, problems.")]
    public Task<string> GetStatus(CancellationToken cancellationToken) =>
        CallAsync(ControlCommands.GetStatus, null, cancellationToken);

    private async Task<string> CallAsync(string command, object? args, CancellationToken ct)
    {
        try
        {
            var result = await client.SendAsync(command, args, ct).ConfigureAwait(false);
            return result is { } r ? JsonSerializer.Serialize(r, Pretty) : "{ \"ok\": true }";
        }
        catch (SoundboardUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ControlException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
