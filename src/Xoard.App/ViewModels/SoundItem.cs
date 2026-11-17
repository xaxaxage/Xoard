using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xoard.Core.Audio.Sounds;
using Xoard.Core.Library;

namespace Xoard.App.ViewModels;

/// <summary>Кнопка звука в сетке.</summary>
public sealed partial class SoundItem : ObservableObject
{
    private readonly MainViewModel host;
    private int playingCount;
    private long lastStartTicks;

    public SoundItem(MainViewModel host, SoundFile file)
    {
        this.host = host;
        File = file;
        Name = file.Name;
    }

    /// <summary>Файл в папке звуков.</summary>
    public SoundFile File { get; set; }

    /// <summary>Загруженный звук (null — ещё грузится или ошибка).</summary>
    public SoundHandle? Handle { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string DurationText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Горячая клавиша («Ctrl+Alt+1») или пусто.</summary>
    [ObservableProperty]
    public partial string HotkeyText { get; set; } = "";

    public bool HasHotkey => HotkeyText.Length > 0;

    partial void OnHotkeyTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(Tooltip));
    }

    public bool HasError => Error is not null;

    /// <summary>Имя для экранного диктора и автоматизации (плитка сетки).</summary>
    public override string ToString() => Name;

    public string Tooltip => Error ?? $"{Name}{(DurationText.Length > 0 ? $" · {DurationText}" : "")}" +
        (HotkeyText.Length > 0 ? $" · {HotkeyText}" : "") + "\nКлик — играть, правая кнопка — действия";

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnDurationTextChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    /// <summary>Звук загружен.</summary>
    public void SetLoaded(SoundHandle handle)
    {
        Handle = handle;
        IsLoading = false;
        Error = null;
        var d = handle.Duration;
        DurationText = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", null) : d.ToString(@"m\:ss", null);
    }

    /// <summary>Ошибка загрузки.</summary>
    public void SetFailed(string message)
    {
        Handle = null;
        IsLoading = false;
        Error = message;
    }

    /// <summary>Событие движка: запуск начался.</summary>
    public void OnStarted()
    {
        playingCount++;
        lastStartTicks = Stopwatch.GetTimestamp();
        IsPlaying = true;
    }

    /// <summary>Событие движка: запуск закончился.</summary>
    public void OnEnded()
    {
        playingCount = Math.Max(0, playingCount - 1);
        if (playingCount == 0)
        {
            IsPlaying = false;
            Progress = 0;
        }
    }

    /// <summary>Обновить полосу прогресса (по времени последнего запуска).</summary>
    public void Tick()
    {
        if (!IsPlaying || Handle is null || Handle.Duration <= TimeSpan.Zero)
            return;
        var elapsed = Stopwatch.GetElapsedTime(lastStartTicks);
        Progress = Math.Clamp(elapsed / Handle.Duration, 0, 1);
    }

    [RelayCommand]
    private void Play() => host.Play(this, previewOnly: false);

    [RelayCommand]
    private void Preview() => host.Play(this, previewOnly: true);

    [RelayCommand]
    private void Rename() => host.RequestRename(this);

    [RelayCommand]
    private void AssignHotkey() => host.RequestHotkey(this);

    [RelayCommand]
    private void Trim() => host.RequestTrim(this);

    [RelayCommand]
    private void Delete() => host.Delete(this);

    [RelayCommand]
    private void ShowInFolder() => host.ShowInFolder(this);
}
