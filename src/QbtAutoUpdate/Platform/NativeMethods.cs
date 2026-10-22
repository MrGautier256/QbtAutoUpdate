using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QbtAutoUpdate.Platform;

internal static partial class NativeMethods
{
    internal const int ATTACH_PARENT_PROCESS = -1;
    internal const int STD_OUTPUT_HANDLE = -11;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    internal const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    internal const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    internal const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    internal const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();
}
