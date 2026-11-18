using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xoard.Core.Audio.Mic;

/// <summary>Подавитель шума голоса: кадр моно 48 кГц в шкале ±32768.</summary>
internal interface IVoiceDenoiser
{
    /// <summary>
    /// Обрабатывает кадр из <see cref="RnNoise.FrameSize"/> отсчётов и возвращает вероятность речи 0..1.
    /// Выход задержан ровно на кадр относительно входа.
    /// </summary>
    float Process(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>Сбрасывает состояние (новый поток с микрофона).</summary>
    void Reset();
}

/// <summary>
/// RNNoise 0.2 (Xiph, BSD-3) из rnnoise.dll рядом с программой; как собрана библиотека —
/// в .github/workflows/rnnoise.yml. Кадр 480 отсчётов (10 мс), выход задержан на кадр.
/// Один экземпляр — один поток вызовов (поток захвата микрофона).
/// </summary>
internal sealed partial class RnNoise : IVoiceDenoiser, IDisposable
{
    /// <summary>Размер кадра RNNoise при 48 кГц.</summary>
    public const int FrameSize = 480;

    private const string Library = "rnnoise";

    private readonly StateHandle state;

    private RnNoise(StateHandle state) => this.state = state;

    /// <summary>Почему библиотека не загрузилась (null — загрузилась или ещё не пробовали).</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Создаёт подавитель; null, если rnnoise.dll нет или она не подошла (причина — в <see cref="LoadError"/>).</summary>
    public static RnNoise? TryCreate()
    {
        try
        {
            var frame = GetFrameSize();
            if (frame != FrameSize)
            {
                LoadError = $"rnnoise.dll работает кадрами по {frame} отсчётов, а нужно {FrameSize}.";
                return null;
            }

            var handle = Create(IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                LoadError = "rnnoise.dll не смогла создать состояние.";
                return null;
            }

            LoadError = null;
            return new RnNoise(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LoadError = $"Нет библиотеки шумоподавления (rnnoise.dll): {ex.Message}";
            return null;
        }
    }

    /// <inheritdoc />
    public unsafe float Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length < FrameSize || output.Length < FrameSize)
            throw new ArgumentException($"Нужен кадр из {FrameSize} отсчётов.");
        ObjectDisposedException.ThrowIf(state.IsClosed, this);

        fixed (float* inPtr = input)
        fixed (float* outPtr = output)
            return ProcessFrame(state, outPtr, inPtr);
    }

    /// <inheritdoc />
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(state.IsClosed, this);
        _ = Init(state, IntPtr.Zero);
    }

    /// <inheritdoc />
    public void Dispose() => state.Dispose();

    [LibraryImport(Library, EntryPoint = "rnnoise_get_frame_size")]
    private static partial int GetFrameSize();

    [LibraryImport(Library, EntryPoint = "rnnoise_create")]
    private static partial StateHandle Create(IntPtr model);

    [LibraryImport(Library, EntryPoint = "rnnoise_init")]
    private static partial int Init(StateHandle state, IntPtr model);

    [LibraryImport(Library, EntryPoint = "rnnoise_destroy")]
    private static partial void Destroy(IntPtr state);

    [LibraryImport(Library, EntryPoint = "rnnoise_process_frame")]
    private static unsafe partial float ProcessFrame(StateHandle state, float* output, float* input);

    /// <summary>Состояние RNNoise (DenoiseState*), освобождается rnnoise_destroy.</summary>
    private sealed class StateHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public StateHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            Destroy(handle);
            return true;
        }
    }
}
