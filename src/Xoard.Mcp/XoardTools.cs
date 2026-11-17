using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xoard.Protocol;

namespace Xoard.Mcp;

/// <summary>
/// Инструменты MCP: каждый — одна команда протокола, переданная в запущенное приложение Xoard.
/// Ошибки (приложение не запущено, звук не найден…) возвращаются Claude как понятный текст.
/// </summary>
[McpServerToolType]
public sealed class XoardTools(ControlClient client)
{
    // Ответ читает модель, а не браузер: кириллица и символы — как есть, без \uXXXX
    private static readonly JsonSerializerOptions Pretty = new(ControlProtocol.Json)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "list_sounds", ReadOnly = true)]
    [Description("List the sounds in the user's Xoard soundboard: id (file name), name, duration, hotkey, whether it is playing.")]
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
                 "\"cable\" (sounds that friends hear), \"mic\" (the user's own voice sent to friends). percent: 0-200, 100 = normal (no boost), above 100 boosts.")]
    public Task<string> SetVolume(
        [Description("headphones, cable or mic")] string target,
        [Description("Volume in percent, 0-200 (100 = normal).")] double percent,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.SetVolume, new SetVolumeArgs(target, percent), cancellationToken);

    [McpServerTool(Name = "get_status", ReadOnly = true)]
    [Description("Xoard status: audio devices, whether VB-CABLE is installed and who listens to it, volumes, mode, what is playing, problems.")]
    public Task<string> GetStatus(CancellationToken cancellationToken) =>
        CallAsync(ControlCommands.GetStatus, null, cancellationToken);

    [McpServerTool(Name = "set_hotkey")]
    [Description("Assign or remove a global hotkey. target: a sound name/id, or \"stop_all\", \"record\" (start/stop recording), " +
                 "\"replay\" (save the last seconds of computer sound). hotkey: e.g. \"Ctrl+Alt+1\", \"F9\", \"Num5\"; empty string removes it.")]
    public Task<string> SetHotkey(
        [Description("Sound name/id, or stop_all, record, replay.")] string target,
        [Description("Key combination like \"Ctrl+Alt+1\"; empty to remove.")] string? hotkey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.SetHotkey, new SetHotkeyArgs(target, hotkey), cancellationToken);

    [McpServerTool(Name = "start_recording")]
    [Description("Start recording. source: \"system\" = everything the computer plays (videos, games, Discord; Xoard's own sounds are excluded), \"mic\" = the user's microphone.")]
    public Task<string> StartRecording(
        [Description("system or mic")] string source = "system",
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.StartRecording, new StartRecordingArgs(source), cancellationToken);

    [McpServerTool(Name = "stop_recording")]
    [Description("Stop the current recording and save it as a new sound in Xoard.")]
    public Task<string> StopRecording(
        [Description("Name for the new sound (optional).")] string? name = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.StopRecording, new SaveSoundArgs(name), cancellationToken);

    [McpServerTool(Name = "save_last_seconds")]
    [Description("Save the last N seconds of computer sound (the always-on replay buffer, 30 s by default) as a new sound — e.g. a funny moment that just happened.")]
    public Task<string> SaveLastSeconds(
        [Description("How many seconds to save (up to the replay buffer length).")] double seconds,
        [Description("Name for the new sound (optional).")] string? name = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.SaveLastSeconds, new SaveSoundArgs(name, seconds), cancellationToken);

    [McpServerTool(Name = "rename_sound")]
    [Description("Rename a sound (its file). Identify the sound by name (exact or unique part) or id.")]
    public Task<string> RenameSound(
        [Description("New name.")] string newName,
        [Description("Current name or a unique part of it.")] string? name = null,
        [Description("Sound id from list_sounds; use instead of name.")] string? id = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.RenameSound, new RenameSoundArgs(name, id, newName), cancellationToken);

    [McpServerTool(Name = "delete_sound")]
    [Description("Delete a sound: the file goes to the Windows Recycle Bin (can be restored).")]
    public Task<string> DeleteSound(
        [Description("Sound name or a unique part of it.")] string? name = null,
        [Description("Sound id from list_sounds; use instead of name.")] string? id = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.DeleteSound, new SoundRefArgs(name, id), cancellationToken);

    [McpServerTool(Name = "trim_sound")]
    [Description("Trim a sound to keep only [start, end) seconds. The original goes to the Recycle Bin.")]
    public Task<string> TrimSound(
        [Description("Start of the part to keep, seconds.")] double start,
        [Description("End of the part to keep, seconds.")] double end,
        [Description("Sound name or a unique part of it.")] string? name = null,
        [Description("Sound id from list_sounds; use instead of name.")] string? id = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(ControlCommands.TrimSound, new TrimSoundArgs(name, id, start, end), cancellationToken);

    private async Task<string> CallAsync(string command, object? args, CancellationToken ct)
    {
        try
        {
            var result = await client.SendAsync(command, args, ct).ConfigureAwait(false);
            return result is { } r ? JsonSerializer.Serialize(r, Pretty) : "{ \"ok\": true }";
        }
        catch (XoardUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ControlException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
