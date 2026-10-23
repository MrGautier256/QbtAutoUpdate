using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QbtAutoUpdate.Platform;

internal static partial class NativeMethods
{
    internal const int ATTACH_PARENT_PROCESS = -1;
    internal const int STD_OUTPUT_HANDLE = -11;

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    internal const uint TOKEN_DUPLICATE = 0x0002;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    internal const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    internal const int TokenElevation = 20;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;

    internal const int ProcessCommandLineInformation = 60;
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    internal const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    internal const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    internal const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    internal const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    // ---- Console ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetStdHandle(int stdHandle);

    // ---- Processes ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CommandLineToArgv(string commandLine, out int argumentCount);

    // ---- Tokens ----

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(SafeAccessTokenHandle existingToken, uint desiredAccess, nint tokenAttributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, char* applicationName, char* commandLine,
        uint creationFlags, nint environment, char* currentDirectory, STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);

    // ---- Shell ----

    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint window, out int processId);

    // ---- Restart Manager ----

    internal const int CCH_RM_SESSION_KEY = 32;
    internal const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public long ProcessStartTime; // FILETIME
    }

    [LibraryImport("rstrtmgr.dll")]
    internal static unsafe partial int RmStartSession(out uint sessionHandle, uint sessionFlags, char* sessionKey);

    [LibraryImport("rstrtmgr.dll")]
    internal static unsafe partial int RmRegisterResources(uint sessionHandle, uint fileCount, char** fileNames,
        uint applicationCount, RM_UNIQUE_PROCESS* applications, uint serviceCount, char** serviceNames);

    [LibraryImport("rstrtmgr.dll")]
    internal static partial int RmShutdown(uint sessionHandle, uint actionFlags, nint statusCallback);

    [LibraryImport("rstrtmgr.dll")]
    internal static partial int RmCancelCurrentTask(uint sessionHandle);

    [LibraryImport("rstrtmgr.dll")]
    internal static partial int RmEndSession(uint sessionHandle);

    // ---- Structures ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }
}
