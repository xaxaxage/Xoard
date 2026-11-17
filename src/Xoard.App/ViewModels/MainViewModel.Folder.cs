using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Xoard.Core.Library;

namespace Xoard.App.ViewModels;

/// <summary>Своя папка звуков: выбрать другую (с переносом звуков или без) или вернуть обычную.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Попросить окно сменить папку: null — выбрать папку в проводнике, путь — сразу эту
    /// (например, обычную «Документы\Xoard\Sounds»). Окно спросит, переносить ли звуки.
    /// </summary>
    public event Action<string?>? SoundsFolderChangeRequested;

    public bool IsCustomSoundsFolder => !SameFolder(library.Folder, defaultSoundsFolder);

    public string DefaultSoundsFolder => defaultSoundsFolder;

    [RelayCommand]
    private void ChooseSoundsFolder() => SoundsFolderChangeRequested?.Invoke(null);

    [RelayCommand]
    private void ResetSoundsFolder() => SoundsFolderChangeRequested?.Invoke(defaultSoundsFolder);

    /// <summary>Сколько звуков сейчас в папке (для вопроса «перенести N звуков?»).</summary>
    public int SoundCount => Sounds.Count;

    public bool IsCurrentSoundsFolder(string folder) => SameFolder(folder, library.Folder);

    /// <summary>
    /// Сменить папку звуков. <paramref name="move"/> — перенести файлы звуков из старой папки
    /// (одноимённые в новой не трогаются). Клавиши звуков привязаны к именам файлов и сохраняются.
    /// Возвращает текст ошибки или null.
    /// </summary>
    public async Task<string?> ChangeSoundsFolderAsync(string folder, bool move)
    {
        folder = Path.GetFullPath(folder);
        if (SameFolder(folder, library.Folder))
            return null;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Не удалось открыть папку: {ex.Message}";
        }

        engine.StopAll();
        var oldFolder = library.Folder;
        var (moved, kept) = move
            ? await Task.Run(() => MoveSounds(oldFolder, folder)).ConfigureAwait(true)
            : (0, 0);

        // Снять клавиши звуков старой папки, загрузить новую
        foreach (var item in Sounds)
            hotkeys?.Unregister(SoundHotkeyPrefix + item.File.FileName);
        Sounds.Clear();
        VisibleSounds.Clear();
        byFile.Clear();
        byKey.Clear();

        library = new SoundLibrary(folder, new WindowsRecycleBin());
        config = config with { SoundsFolder = SameFolder(folder, defaultSoundsFolder) ? null : folder };
        ScheduleSave();

        foreach (var file in library.Scan(config.SoundOrder))
        {
            var item = new SoundItem(this, file);
            Sounds.Add(item);
            byFile[file.FileName] = item;
        }

        RegisterSoundHotkeys();
        RefreshVisible();
        foreach (var item in Sounds.ToList())
            _ = LoadAsync(item);

        OnPropertyChanged(nameof(SoundsFolder));
        OnPropertyChanged(nameof(IsCustomSoundsFolder));
        ShowToast(move
            ? $"Папка звуков сменена, перенесено звуков: {moved}" + (kept > 0 ? $" (не перенесено {kept} — такие имена уже были в новой папке или файл занят)." : ".")
            : "Папка звуков сменена.", InfoBarSeverity.Success);
        return null;
    }

    /// <summary>Перенести файлы звуков; одноимённые в новой папке и занятые файлы остаются на месте.</summary>
    private static (int Moved, int Kept) MoveSounds(string from, string to)
    {
        int moved = 0, kept = 0;
        if (!Directory.Exists(from))
            return (0, 0);
        foreach (var file in Directory.EnumerateFiles(from).Where(SoundLibrary.IsSupported).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target))
            {
                kept++;
                continue;
            }

            try
            {
                File.Move(file, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept++;
            }
        }

        return (moved, kept);
    }

    /// <summary>Клавиши звуков текущей папки (клавиши «Стоп всё», «Запись», «Повтор» не трогаются).</summary>
    private void RegisterSoundHotkeys()
    {
        if (hotkeys is null)
            return;
        foreach (var (file, text) in config.SoundHotkeys)
        {
            if (byFile.TryGetValue(file, out var item) && Xoard.Core.Hotkeys.HotkeyGesture.TryParse(text, out var g))
            {
                item.HotkeyText = g.ToString();
                hotkeys.Register(SoundHotkeyPrefix + file, g);
            }
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
