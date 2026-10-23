using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace QbtAutoUpdate.Platform;

public sealed record ScheduledTaskInfo(bool Enabled, bool Running, DateTime? LastRun, int LastResult);

/// <summary>
/// The two tasks of the \QbtAutoUpdate\ folder, through the Task Scheduler 2.0 COM API.
/// Registering and deleting need administrator rights; reading and running "Apply" work for every
/// user thanks to the task's security descriptor.
/// </summary>
public sealed class ScheduledTasks
{
    public const string FolderPath = @"\QbtAutoUpdate";
    public const string CheckTask = "Check";
    public const string ApplyTask = "Apply";

    private readonly ITaskService _service;

    private ScheduledTasks(ITaskService service) => _service = service;

    public static ScheduledTasks Connect()
    {
        var service = Com.Create<ITaskService>(TaskSchedulerIds.ClsidTaskScheduler, TaskSchedulerIds.IidTaskService);
        service.Connect(default, default, default, default);
        return new ScheduledTasks(service);
    }

    /// <summary>Path of any folder, for read-only checks.</summary>
    internal string FolderPathOf(string path) => _service.GetFolder(path).GetPath();

    /// <summary>Creates or replaces a task from its XML definition.</summary>
    /// <param name="principalSid">The SID of the group (Check) or account (Apply) the task runs as.</param>
    /// <param name="sddl">Security descriptor of the task itself (who may read and run it), or null.</param>
    public void Register(string name, string xml, string principalSid, bool isGroup, string? sddl)
    {
        ITaskFolder folder = GetOrCreateFolder();
        ComVariant user = ComVariant.Create(principalSid);
        ComVariant descriptor = sddl is null ? default : ComVariant.Create(sddl);
        try
        {
            folder.RegisterTask(name, xml, TaskSchedulerIds.TASK_CREATE_OR_UPDATE, user, default,
                isGroup ? TaskSchedulerIds.TASK_LOGON_GROUP : TaskSchedulerIds.TASK_LOGON_SERVICE_ACCOUNT, descriptor);
        }
        finally
        {
            user.Dispose();
            descriptor.Dispose();
        }
    }

    public bool Exists(string name) => TryGetTask(name) is not null;

    public ScheduledTaskInfo? GetInfo(string name)
    {
        if (TryGetTask(name) is not { } task)
            return null;
        double lastRun = task.GetLastRunTime();
        int state = task.GetState();
        return new ScheduledTaskInfo(
            task.GetEnabled() != 0,
            state is TaskSchedulerIds.TASK_STATE_RUNNING or TaskSchedulerIds.TASK_STATE_QUEUED,
            lastRun > 1 ? DateTime.FromOADate(lastRun) : null, // 1899-12-30 means "never"
            task.GetLastTaskResult());
    }

    /// <summary>Starts a task on demand (no parameters).</summary>
    public void Run(string name)
    {
        IRegisteredTask task = TryGetTask(name) ?? throw new InvalidOperationException($"Scheduled task {FolderPath}\\{name} does not exist.");
        Com.Release(task.Run(default));
    }

    public void SetEnabled(string name, bool enabled)
    {
        if (TryGetTask(name) is { } task)
            task.SetEnabled(enabled ? (short)-1 : (short)0);
    }

    /// <summary>Deletes both tasks and the folder. Missing items are ignored.</summary>
    public void DeleteAll()
    {
        ITaskFolder folder;
        try
        {
            folder = _service.GetFolder(FolderPath);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return;
        }

        foreach (string name in new[] { CheckTask, ApplyTask })
        {
            try
            {
                folder.DeleteTask(name, 0);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
            }
        }
        _service.GetFolder(@"\").DeleteFolder(FolderPath.TrimStart('\\'), 0);
    }

    /// <summary>
    /// Failures callers may treat as "Task Scheduler not usable here". Source-generated COM turns
    /// well-known HRESULTs into .NET exceptions: 0x80070002 becomes FileNotFoundException,
    /// E_ACCESSDENIED becomes UnauthorizedAccessException, the rest COMException.
    /// </summary>
    public static bool IsFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException;

    private IRegisteredTask? TryGetTask(string name)
    {
        try
        {
            return _service.GetFolder(FolderPath).GetTask(name);
        }
        catch (Exception ex) when (IsNotFound(ex) || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ITaskFolder GetOrCreateFolder()
    {
        try
        {
            return _service.GetFolder(FolderPath);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return _service.GetFolder(@"\").CreateFolder(FolderPath.TrimStart('\\'), default);
        }
    }

    /// <summary>By HRESULT, not by type: see <see cref="IsFailure"/>.</summary>
    private static bool IsNotFound(Exception ex) =>
        ex.HResult is TaskSchedulerIds.HRESULT_FILE_NOT_FOUND or TaskSchedulerIds.HRESULT_PATH_NOT_FOUND;
}
