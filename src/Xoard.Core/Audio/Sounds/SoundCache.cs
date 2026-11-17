using System.Collections.Concurrent;

namespace Xoard.Core.Audio.Sounds;

/// <summary>
/// Кэш декодированных звуков с вытеснением давно не использованных (LRU) в пределах бюджета памяти.
/// Вытеснение безопасно для играющих звуков: голос держит ссылку на массив сэмплов сам,
/// кэш лишь забывает запись. Одновременные загрузки одного файла объединяются.
/// </summary>
public sealed class SoundCache : IDisposable
{
    /// <summary>Бюджет памяти по умолчанию.</summary>
    public const long DefaultBudgetBytes = 256L << 20;

    private readonly long budgetBytes;
    private readonly TimeSpan maxDuration;
    private readonly SemaphoreSlim decodeSlots;
    private readonly ConcurrentDictionary<string, Lazy<Task<DecodedSound>>> inflight = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Lock gate = new();
    private long totalBytes;
    private long useCounter;

    public SoundCache(long budgetBytes = DefaultBudgetBytes, int maxConcurrentDecodes = 3, TimeSpan? maxDuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDecodes, 1);
        this.budgetBytes = budgetBytes;
        this.maxDuration = maxDuration ?? SoundDecoder.DefaultMaxDuration;
        decodeSlots = new SemaphoreSlim(maxConcurrentDecodes, maxConcurrentDecodes);
    }

    /// <summary>Занято памяти звуками в кэше.</summary>
    public long TotalBytes
    {
        get
        {
            lock (gate)
                return totalBytes;
        }
    }

    /// <summary>Число звуков в кэше.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>Загружает файл (или берёт из кэша). Ошибки — <see cref="SoundLoadException"/>.</summary>
    public async Task<SoundHandle> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
            throw new SoundLoadException(SoundLoadError.FileNotFound, file.FullName);

        var key = SoundFileStamp.KeyFor(file);
        var name = Path.GetFileNameWithoutExtension(file.Name);
        if (TryGet(key, out var cached))
            return new SoundHandle(key, file.FullName, name, cached.Duration);

        var lazy = inflight.GetOrAdd(key, _ => new Lazy<Task<DecodedSound>>(() => DecodeLimitedAsync(file.FullName, ct)));
        try
        {
            var sound = await lazy.Value.ConfigureAwait(false);
            Add(key, sound);
            return new SoundHandle(key, file.FullName, name, sound.Duration);
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<string, Lazy<Task<DecodedSound>>>(key, lazy));
        }
    }

    /// <summary>Кладёт в кэш звук, созданный в памяти (тестовые сигналы).</summary>
    public SoundHandle AddInMemory(string name, DecodedSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var key = SoundFileStamp.MemoryKeyPrefix + Guid.NewGuid().ToString("N");
        Add(key, sound);
        return new SoundHandle(key, "", name, sound.Duration);
    }

    /// <summary>Достаёт звук по ключу и отмечает его как недавно использованный.</summary>
    public bool TryGet(string key, out DecodedSound sound)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                entry.LastUse = ++useCounter;
                sound = entry.Sound;
                return true;
            }
        }

        sound = null!;
        return false;
    }

    /// <summary>Удаляет звук из кэша (играющие копии доиграют).</summary>
    public bool Remove(string key)
    {
        lock (gate)
        {
            if (!entries.Remove(key, out var entry))
                return false;
            totalBytes -= entry.Sound.Bytes;
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose() => decodeSlots.Dispose();

    private async Task<DecodedSound> DecodeLimitedAsync(string path, CancellationToken ct)
    {
        await decodeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SoundDecoder.DecodeAsync(path, maxDuration, ct).ConfigureAwait(false);
        }
        finally
        {
            decodeSlots.Release();
        }
    }

    private void Add(string key, DecodedSound sound)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                existing.LastUse = ++useCounter;
                return;
            }

            entries[key] = new Entry(sound) { LastUse = ++useCounter };
            totalBytes += sound.Bytes;

            // Вытесняем самые старые, но не только что добавленный
            while (totalBytes > budgetBytes && entries.Count > 1)
            {
                var oldest = entries.Where(e => e.Key != key).MinBy(e => e.Value.LastUse);
                entries.Remove(oldest.Key);
                totalBytes -= oldest.Value.Sound.Bytes;
            }
        }
    }

    private sealed class Entry(DecodedSound sound)
    {
        public DecodedSound Sound { get; } = sound;

        public long LastUse { get; set; }
    }
}
