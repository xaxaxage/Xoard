using Xoard.Core.Audio;
using Microsoft.UI.Xaml.Controls;
using Xoard.Core.Audio.Status;
using Xoard.Core.Recording;
using Xoard.Protocol;

namespace Xoard.App.ViewModels;

/// <summary>
/// Команды управления (named pipe → MCP для Claude). Состояние интерфейса меняется только в его
/// потоке, поэтому команды переносятся туда; звук запускается через потокобезопасный движок.
/// </summary>
public sealed partial class MainViewModel : IXoardControl
{
    public Task<SoundList> ListSoundsAsync(CancellationToken ct) =>
        OnUi(() => new SoundList(Sounds.Select(ToInfo).ToList()));

    public Task<PlayInfo> PlaySoundAsync(PlaySoundArgs args, CancellationToken ct) => OnUi(() =>
    {
        var item = FindSound(args);
        if (!engineRunning)
            throw new ControlException(ControlErrorCodes.Unavailable, "Звуковой движок не запущен (см. окно Xoard).");
        if (item.Handle is null)
        {
            throw new ControlException(ControlErrorCodes.Unavailable,
                item.HasError ? $"«{item.Name}» не загружен: {item.Error}" : $"«{item.Name}» ещё загружается, повторите через секунду.");
        }

        var options = new PlayOptions(Outputs: args.PreviewOnly ? OutputMask.Headphones : OutputMask.Both);
        var result = engine.Play(item.Handle, options);
        switch (result.Status)
        {
            case PlayStatus.Started:
                break;
            case PlayStatus.NotLoaded:
                _ = ReloadAndPlayAsync(item, options);
                break;
            case PlayStatus.NoOutput:
                throw new ControlException(ControlErrorCodes.Unavailable, args.PreviewOnly
                    ? "Нет наушников для предпрослушивания."
                    : "Некуда выводить звук: нет ни VB-CABLE, ни наушников.");
            default:
                throw new ControlException(ControlErrorCodes.Unavailable, "Звуковой движок не запущен.");
        }

        return new PlayInfo(item.File.FileName, item.Name, item.Handle.Duration.TotalSeconds, args.PreviewOnly);
    });

    public Task StopAllAsync(CancellationToken ct)
    {
        engine.StopAll();
        return Task.CompletedTask;
    }

    public Task<VolumeInfo> SetVolumeAsync(SetVolumeArgs args, CancellationToken ct) => OnUi(() =>
    {
        if (double.IsNaN(args.Percent) || args.Percent < 0 || args.Percent > 100)
            throw new ControlException(ControlErrorCodes.BadRequest, "percent должен быть от 0 до 100.");

        switch ((args.Target ?? "").Trim().ToLowerInvariant())
        {
            case "headphones" or "наушники":
                HeadphonesLevel = Math.Round(args.Percent);
                break;
            case "cable" or "friends" or "кабель" or "друзьям":
                CableLevel = Math.Round(args.Percent);
                break;
            case "mic" or "microphone" or "voice" or "микрофон" or "голос":
                MicLevel = Math.Round(args.Percent);
                break;
            default:
                throw new ControlException(ControlErrorCodes.BadRequest, "target должен быть headphones, cable или mic.");
        }

        return Volumes();
    });

    public Task<StatusInfo> GetStatusAsync(CancellationToken ct) => OnUi(() =>
    {
        var s = engine.Status;
        return new StatusInfo(
            engineRunning ? s.State.ToString().ToLowerInvariant() : "stopped",
            s.VbCable.Installed,
            s.VbCable.RenderName,
            s.VbCable.CaptureName,
            s.VbCable.ListeningApps.Select(a => a.ProcessName).ToList(),
            new OutputInfo(s.Headphones.State.ToString().ToLowerInvariant(), s.Headphones.DeviceName),
            new OutputInfo(s.Cable.State.ToString().ToLowerInvariant(), s.Cable.DeviceName),
            new MicInfo(s.Mic.Enabled, s.Mic.Muted, s.Mic.Capturing, s.Mic.DeviceName),
            Volumes(),
            ModeIndex == 1 ? "restart" : "overlap",
            s.Playing.Select(p => new PlayingInfo(p.Name, Math.Round(p.PositionSec, 1), Math.Round(p.DurationSec, 1))).ToList(),
            Sounds.Count,
            s.Issues.Select(i => i.Message).ToList());
    });

    // ───────── этап 5: клавиши, запись, повтор, переименование, удаление, обрезка ─────────

    public Task<HotkeyInfo> SetHotkeyAsync(SetHotkeyArgs args, CancellationToken ct) => OnUi(() =>
    {
        var target = (args.Target ?? "").Trim().ToLowerInvariant() switch
        {
            "stop_all" or "stop" or "стоп всё" or "стоп" => HotkeyTarget.StopAll,
            "record" or "recording" or "запись" => HotkeyTarget.Record,
            "replay" or "повтор" => HotkeyTarget.Replay,
            // Звук: точный id (имя файла) или имя/часть имени
            _ => HotkeyTarget.ForSound(byFile.TryGetValue(args.Target?.Trim() ?? "", out var byId) ? byId : FindSound(args.Target, null)),
        };

        Xoard.Core.Hotkeys.HotkeyGesture? gesture = null;
        if (!string.IsNullOrWhiteSpace(args.Hotkey))
        {
            if (!Xoard.Core.Hotkeys.HotkeyGesture.TryParse(args.Hotkey, out var g))
                throw new ControlException(ControlErrorCodes.BadRequest, $"Не понял сочетание «{args.Hotkey}». Примеры: Ctrl+Alt+1, F9, Num5.");
            gesture = g;
        }

        // Свои же сочетания не должны мешать проверке занятости
        SuspendHotkeys();
        string? error;
        try
        {
            error = SetHotkey(target, gesture);
        }
        finally
        {
            ResumeHotkeys();
        }

        if (error is not null)
            throw new ControlException(ControlErrorCodes.BadRequest, error);
        return new HotkeyInfo(target.Kind == HotkeyKind.Sound ? target.Sound!.Name : args.Target!, gesture?.ToString());
    });

    public Task<RecordingInfo> StartRecordingAsync(StartRecordingArgs args, CancellationToken ct) => OnUiAsync(async () =>
    {
        var source = (args.Source ?? "system").Trim().ToLowerInvariant() switch
        {
            "system" or "computer" or "компьютер" => RecordingSource.System,
            "mic" or "microphone" or "микрофон" => RecordingSource.Microphone,
            _ => throw new ControlException(ControlErrorCodes.BadRequest, "source должен быть system или mic."),
        };
        if (recorder.Recording is not null)
            throw new ControlException(ControlErrorCodes.BadRequest, "Запись уже идёт — сначала stop_recording.");

        try
        {
            await recorder.StartRecordingAsync(source, config.Audio.MicDeviceId).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlException(ControlErrorCodes.Unavailable, ex.Message);
        }

        IsRecording = true;
        return RecordingState();
    });

    public Task<SoundInfo> StopRecordingAsync(SaveSoundArgs args, CancellationToken ct) => OnUiAsync(async () =>
    {
        if (recorder.Recording is null)
            throw new ControlException(ControlErrorCodes.BadRequest, "Запись не идёт — сначала start_recording.");
        var path = await recorder.StopRecordingAsync(library.Folder, CleanName(args.Name)).ConfigureAwait(true);
        IsRecording = false;
        if (path is null)
            throw new ControlException(ControlErrorCodes.Unavailable, "Запись слишком короткая — ничего не сохранено.");
        var item = AddRecordedFile(path);
        ShowToast($"Запись сохранена: «{item.Name}».", InfoBarSeverity.Success);
        return await LoadedInfoAsync(item).ConfigureAwait(true);
    });

    public Task<SoundInfo> SaveLastSecondsAsync(SaveSoundArgs args, CancellationToken ct) => OnUiAsync(async () =>
    {
        var seconds = args.Seconds > 0 ? args.Seconds : ReplaySeconds;
        string path;
        try
        {
            path = await recorder.SaveReplayAsync(seconds, library.Folder, CleanName(args.Name)).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlException(ControlErrorCodes.Unavailable, ex.Message);
        }

        var item = AddRecordedFile(path);
        ShowToast($"Повтор сохранён: «{item.Name}».", InfoBarSeverity.Success);
        return await LoadedInfoAsync(item).ConfigureAwait(true);
    });

    public Task<SoundInfo> RenameSoundAsync(RenameSoundArgs args, CancellationToken ct) => OnUiAsync(async () =>
    {
        var item = FindSound(args.Name, args.Id);
        var error = Rename(item, args.NewName ?? "");
        if (error is not null)
            throw new ControlException(ControlErrorCodes.BadRequest, error);
        return await LoadedInfoAsync(item).ConfigureAwait(true);
    });

    public Task<DeletedInfo> DeleteSoundAsync(SoundRefArgs args, CancellationToken ct) => OnUi(() =>
    {
        var item = FindSound(args.Name, args.Id);
        var info = new DeletedInfo(item.File.FileName, item.Name);
        Delete(item);
        if (Sounds.Contains(item))
            throw new ControlException(ControlErrorCodes.Unavailable, $"Не удалось удалить «{item.Name}».");
        return info;
    });

    public Task<SoundInfo> TrimSoundAsync(TrimSoundArgs args, CancellationToken ct) => OnUiAsync(async () =>
    {
        var item = FindSound(args.Name, args.Id);
        var error = await TrimAsync(item, args.Start, args.End).ConfigureAwait(true);
        if (error is not null)
            throw new ControlException(ControlErrorCodes.BadRequest, error);
        return await LoadedInfoAsync(item).ConfigureAwait(true);
    });

    private RecordingInfo RecordingState() => new(
        recorder.Recording is not null,
        recorder.Recording switch { RecordingSource.System => "system", RecordingSource.Microphone => "mic", _ => null },
        Math.Round(recorder.RecordingElapsed.TotalSeconds, 1),
        recorder.ReplayEnabled,
        Math.Round(recorder.ReplayBufferedSeconds, 1));

    /// <summary>Дождаться загрузки звука (до 10 с), чтобы вернуть его длительность.</summary>
    private async Task<SoundInfo> LoadedInfoAsync(SoundItem item)
    {
        for (var i = 0; i < 100 && item.IsLoading; i++)
            await Task.Delay(100).ConfigureAwait(true);
        return ToInfo(item);
    }

    private static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private SoundItem FindSound(string? name, string? id) => FindSound(new PlaySoundArgs(name, id));

    /// <summary>Выполнить асинхронную работу в потоке интерфейса и дождаться результата.</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    tcs.SetResult(await func().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new ControlException(ControlErrorCodes.Unavailable, "Xoard закрывается."));
        }

        return tcs.Task;
    }

    private VolumeInfo Volumes() => new(Math.Round(HeadphonesLevel), Math.Round(CableLevel), Math.Round(MicLevel), MicMuted);

    private static SoundInfo ToInfo(SoundItem s) => new(
        s.File.FileName,
        s.Name,
        Math.Round(s.Handle?.Duration.TotalSeconds ?? 0, 1),
        s.HasHotkey ? s.HotkeyText : null,
        s.IsPlaying,
        s.Handle is not null,
        s.Error);

    /// <summary>Звук по id (имени файла) или имени: точное совпадение, иначе единственное по части имени.</summary>
    private SoundItem FindSound(PlaySoundArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Id))
        {
            return byFile.GetValueOrDefault(args.Id.Trim())
                ?? throw new ControlException(ControlErrorCodes.NotFound, $"Звука с id «{args.Id}» нет. Список — list_sounds.");
        }

        var query = (args.Name ?? "").Trim();
        if (query.Length == 0)
            throw new ControlException(ControlErrorCodes.BadRequest, "Укажите name или id звука.");

        var exact = Sounds.Where(s => string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(s.File.FileName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return exact[0];

        var partial = Sounds.Where(s => s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ControlException(ControlErrorCodes.NotFound,
                $"Звук «{query}» не найден." + (Sounds.Count > 0 ? $" Есть: {string.Join(", ", Sounds.Take(15).Select(s => s.Name))}." : " Звуков пока нет.")),
            _ => throw new ControlException(ControlErrorCodes.Ambiguous,
                $"Под «{query}» подходит несколько звуков: {string.Join(", ", partial.Take(10).Select(s => s.Name))}. Уточните имя."),
        };
    }

    /// <summary>Выполнить в потоке интерфейса и дождаться результата.</summary>
    private Task<T> OnUi<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new ControlException(ControlErrorCodes.Unavailable, "Xoard закрывается."));
        }

        return tcs.Task;
    }
}
