using System.Runtime.InteropServices;

namespace Soundboard.Core.Audio;

/// <summary>Вызовы Win32, которых нет в .NET.</summary>
internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    internal static partial int GetThreadPriority(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(IntPtr process, int processInformationClass, ref ProcessPowerThrottlingState info, int size);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    internal const int ProcessPowerThrottling = 4;
    internal const uint PowerThrottlingCurrentVersion = 1;
    internal const uint PowerThrottlingExecutionSpeed = 0x1;
    internal const uint PowerThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>
    /// Отключает энергосберегающее «торможение» процесса (EcoQoS) и огрубление таймеров:
    /// приложение живёт в трее, но звук должен идти без задержек (ревью M2).
    /// </summary>
    internal static bool DisablePowerThrottling()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}
