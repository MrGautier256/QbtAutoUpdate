using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace QbtAutoUpdate.Platform;

// Source-generated COM interop (ComWrappers): works trimmed, unlike [ComImport].
// Only the methods that are called have real signatures; the others are placeholders that keep the
// vtable slots in order. Orders and GUIDs come from the Windows SDK headers taskschd.h, shobjidl_core.h
// and objidl.h.

internal static partial class Com
{
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_MULTITHREADED = 0x0;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Creates an in-process COM object and returns it as the requested source-generated interface.</summary>
    public static T Create<T>(Guid clsid, Guid iid) where T : class
    {
        // S_FALSE (already initialized) and RPC_E_CHANGED_MODE (STA thread) are both fine.
        CoInitializeEx(0, COINIT_MULTITHREADED);
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, CLSCTX_INPROC_SERVER, iid, out nint instance));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    public static void Release(nint pointer)
    {
        if (pointer != 0)
            Marshal.Release(pointer);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

/// <summary>IDispatch's four methods, so that dual interfaces get the right vtable layout. Never called.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("00020400-0000-0000-C000-000000000046")]
internal partial interface IDispatchSlots
{
    void GetTypeInfoCount();
    void GetTypeInfo();
    void GetIDsOfNames();
    void Invoke();
}

// ---- Task Scheduler 2.0 (taskschd.h) ----

internal static class TaskSchedulerIds
{
    public static readonly Guid ClsidTaskScheduler = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");
    public static readonly Guid IidTaskService = new("2faba4c7-4da9-4013-9697-20cc3fd40f85");

    public const int TASK_CREATE_OR_UPDATE = 6;
    public const int TASK_LOGON_GROUP = 4;
    public const int TASK_LOGON_SERVICE_ACCOUNT = 5;
    public const int TASK_STATE_RUNNING = 4;
    public const int TASK_STATE_QUEUED = 2;
    public const int HRESULT_FILE_NOT_FOUND = unchecked((int)0x80070002);
    public const int HRESULT_PATH_NOT_FOUND = unchecked((int)0x80070003);
    public const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("2faba4c7-4da9-4013-9697-20cc3fd40f85")]
internal partial interface ITaskService : IDispatchSlots
{
    ITaskFolder GetFolder(string path);
    void GetRunningTasks();
    void NewTask();
    void Connect(ComVariant serverName, ComVariant user, ComVariant domain, ComVariant password);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("8cfac062-a080-4c15-9a88-aa7c2af80dfc")]
internal partial interface ITaskFolder : IDispatchSlots
{
    string GetName();
    string GetPath();
    ITaskFolder GetFolder(string path);
    void GetFolders();
    ITaskFolder CreateFolder(string subFolderName, ComVariant sddl);
    void DeleteFolder(string subFolderName, int flags);
    IRegisteredTask GetTask(string path);
    void GetTasks();
    void DeleteTask(string name, int flags);
    IRegisteredTask RegisterTask(string path, string xmlText, int flags, ComVariant userId, ComVariant password, int logonType, ComVariant sddl);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("9c86f320-dee3-4dd1-b972-a303f26b061e")]
internal partial interface IRegisteredTask : IDispatchSlots
{
    string GetName();
    string GetPath();
    int GetState();
    short GetEnabled();
    void SetEnabled(short enabled);
    nint Run(ComVariant parameters);
    void RunEx();
    void GetInstances();
    double GetLastRunTime();
    int GetLastTaskResult();
    int GetNumberOfMissedRuns();
    double GetNextRunTime();
    void GetDefinition();
    string GetXml();
    string GetSecurityDescriptor(int securityInformation);
    void SetSecurityDescriptor(string sddl, int flags);
}

// ---- Shell links (shobjidl_core.h, objidl.h) ----

internal static class ShellLinkIds
{
    public static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    void GetPath(char* file, int maxPath, nint findData, uint flags);
    void GetIDList();
    void SetIDList();
    void GetDescription(char* name, int maxName);
    void SetDescription(string name);
    void GetWorkingDirectory(char* dir, int maxPath);
    void SetWorkingDirectory(string dir);
    void GetArguments(char* args, int maxPath);
    void SetArguments(string args);
    void GetHotkey();
    void SetHotkey();
    void GetShowCmd();
    void SetShowCmd();
    void GetIconLocation(char* iconPath, int maxPath, out int iconIndex);
    void SetIconLocation(string iconPath, int iconIndex);
    void SetRelativePath();
    void Resolve();
    void SetPath(string file);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);
    [PreserveSig]
    int IsDirty();
    void Load(string fileName, uint mode);
    void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted(string fileName);
}
