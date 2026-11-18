using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Xoard.Core.Audio;
using Xoard.Core.Shell;

namespace Xoard.Core.Recording;

/// <summary>Процесс в снимке: родитель и имя файла.</summary>
internal readonly record struct ProcessNode(int Parent, string Name);

/// <summary>
/// Какие программы писать отдельной петлёй (захват звука дерева процессов, Windows 10 2004+).
/// Чистая функция — проверяется тестами.
/// </summary>
internal static class AppSelection
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Из программ со звуковыми сессиями выбирает, чьи деревья процессов писать и на какую дорожку:
    /// Discord и его дочерние процессы — «Discord», остальное — «Игра». Пропускаются: сам Xoard и его
    /// дочерние процессы; предки Xoard (Проводник и т. п. — их дерево захватило бы звуки Xoard);
    /// для «Игры» — предки Discord; процессы, чей предок и так пишется (иначе звук удвоился бы).
    /// </summary>
    public static Dictionary<int, TrackKind> Choose(IEnumerable<int> sessionPids, IReadOnlyDictionary<int, ProcessNode> processes, int self)
    {
        var discordPids = processes.Where(p => SetupChecks.IsDiscord(p.Value.Name)).Select(p => p.Key).ToHashSet();
        var candidates = new Dictionary<int, TrackKind>();
        foreach (var pid in sessionPids.Distinct())
        {
            if (pid <= 4 || pid == self || !processes.ContainsKey(pid))
                continue;
            if (IsAncestor(processes, self, pid) || IsAncestor(processes, pid, self))
                continue;

            var discord = discordPids.Contains(pid) || discordPids.Any(d => IsAncestor(processes, d, pid));
            if (!discord && discordPids.Any(d => IsAncestor(processes, pid, d)))
                continue;
            candidates[pid] = discord ? TrackKind.Discord : TrackKind.Game;
        }

        // Предок уже пишется вместе со всем деревом — потомка не пишем
        return candidates
            .Where(c => !candidates.Keys.Any(other => other != c.Key && IsAncestor(processes, other, c.Key)))
            .ToDictionary(c => c.Key, c => c.Value);
    }

    /// <summary><paramref name="ancestor"/> — предок <paramref name="pid"/> (по цепочке родителей).</summary>
    public static bool IsAncestor(IReadOnlyDictionary<int, ProcessNode> processes, int ancestor, int pid)
    {
        var current = pid;
        for (var depth = 0; depth < MaxDepth && processes.TryGetValue(current, out var node); depth++)
        {
            var parent = node.Parent;
            if (parent == ancestor)
                return true;
            if (parent == current || parent <= 4)
                return false;
            current = parent;
        }

        return false;
    }
}

/// <summary>
/// Отдельные петли захвата звука программ: Discord — на дорожку «Discord», остальные программы —
/// на «Игра». Раз в пару секунд смотрит, у каких программ есть звуковые сессии, запускает петли для
/// новых и закрывает петли завершившихся. Пакеты отдаёт в <see cref="DataAvailable"/> (поток захвата).
/// </summary>
internal sealed partial class AppAudioCaptures : IAsyncDisposable
{
    private const int MaxCaptures = 24;

    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Dictionary<int, AppCapture> captures = [];
    private bool disposed;

    /// <summary>Пакет петли программы: кто, байты (float 48 кГц стерео), флаги.</summary>
    public event Action<AppCapture, ReadOnlySpan<byte>, AudioClientBufferFlags>? DataAvailable;

    /// <summary>Петли процессов поддерживаются (Windows 10 2004+).</summary>
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Последняя ошибка запуска петли (по-русски) или null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Имена программ, которые сейчас пишутся на дорожку.</summary>
    public IReadOnlyList<string> Names(TrackKind kind)
    {
        lock (captures)
            return [.. captures.Values.Where(c => c.Kind == kind).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Сверить петли с программами, у которых есть звук. Повторный вызов во время работы пропускается.</summary>
    public async Task RefreshAsync()
    {
        if (!Supported || !await refreshGate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            if (disposed)
                return;
            var processes = ProcessTree.Snapshot();
            var wanted = AppSelection.Choose(SessionPids(), processes, Environment.ProcessId);

            List<AppCapture> stale;
            lock (captures)
                stale = [.. captures.Values.Where(c => c.HasExited || !wanted.TryGetValue(c.Pid, out var kind) || kind != c.Kind)];
            foreach (var capture in stale)
                await StopAsync(capture).ConfigureAwait(false);

            foreach (var (pid, kind) in wanted)
            {
                lock (captures)
                {
                    if (captures.ContainsKey(pid) || captures.Count >= MaxCaptures)
                        continue;
                }

                await StartAsync(pid, kind, processes[pid].Name).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            LastError = $"Не удалось обновить список программ со звуком: {ex.Message}";
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Закрыть все петли.</summary>
    public async Task StopAllAsync()
    {
        await refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<AppCapture> all;
            lock (captures)
                all = [.. captures.Values];
            foreach (var capture in all)
                await StopAsync(capture).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        disposed = true;
        refreshGate.Dispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
    private async Task StartAsync(int pid, TrackKind kind, string name)
    {
        Process? process = null;
        WasapiRecorder? recorder = null;
        try
        {
            process = Process.GetProcessById(pid);
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 2))
                .BuildAsync().ConfigureAwait(false);
            var format = recorder.WaveFormat;
            if (format.SampleRate != AudioFormat.SampleRate || format.Channels != 2 || format.BitsPerSample != 32)
                throw new InvalidOperationException($"петля отдала формат {format}, а нужен float 48 кГц стерео");

            var capture = new AppCapture(pid, kind, Path.GetFileNameWithoutExtension(name), process, recorder);
            recorder.DataAvailable += (buffer, flags, _, _) => DataAvailable?.Invoke(capture, buffer, flags);
            lock (captures)
                captures[pid] = capture;
            recorder.StartRecording();
            process = null;
            recorder = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (captures)
                captures.Remove(pid);
            LastError = $"Не удалось писать звук «{name}»: {ex.Message}";
        }
        finally
        {
            recorder?.Dispose();
            process?.Dispose();
        }
    }

    private async Task StopAsync(AppCapture capture)
    {
        lock (captures)
            captures.Remove(capture.Pid);
        await Task.Run(() =>
        {
            try
            {
                capture.Recorder.StopRecording();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // Процесс уже завершился — петля и так мертва
            }

            capture.Recorder.Dispose();
            capture.Process.Dispose();
        }).ConfigureAwait(false);
    }

    /// <summary>Процессы со звуковыми сессиями на всех включённых устройствах вывода.</summary>
    private static HashSet<int> SessionPids()
    {
        var result = new HashSet<int>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateExpired)
                            result.Add((int)session.GetProcessID);
                    }
                }
                catch (COMException)
                {
                    // Устройство пропало во время опроса
                }
            }
        }

        return result;
    }
}

/// <summary>Петля одной программы.</summary>
internal sealed class AppCapture(int pid, TrackKind kind, string name, Process process, WasapiRecorder recorder)
{
    public int Pid { get; } = pid;

    public TrackKind Kind { get; } = kind;

    public string Name { get; } = name;

    public Process Process { get; } = process;

    public WasapiRecorder Recorder { get; } = recorder;

    /// <summary>Место пакетов на шкале времени (только поток захвата этой петли и под блокировкой службы).</summary>
    public SourcePlacer Placer { get; } = new();

    public bool HasExited
    {
        get
        {
            try
            {
                return Process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }
    }
}

/// <summary>Снимок процессов: pid → родитель и имя (CreateToolhelp32Snapshot).</summary>
internal static partial class ProcessTree
{
    private const uint SnapProcess = 0x2;

    public static unsafe Dictionary<int, ProcessNode> Snapshot()
    {
        var result = new Dictionary<int, ProcessNode>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (var ok = Process32First(snapshot, &entry); ok; ok = Process32Next(snapshot, &entry))
                result[(int)entry.ProcessId] = new ProcessNode((int)entry.ParentProcessId, new string(entry.ExeFile));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32First(IntPtr snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32Next(IntPtr snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }
}
