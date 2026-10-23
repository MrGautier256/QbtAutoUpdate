using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

/// <summary>Real Windows APIs, used read-only or in a temporary folder.</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    public PlatformTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Task_scheduler_com_interop_reads_a_folder()
    {
        ScheduledTasks tasks = ScheduledTasks.Connect();

        Assert.Equal(@"\Microsoft\Windows", tasks.FolderPathOf(@"\Microsoft\Windows"));
    }

    [Fact]
    public void A_missing_task_or_folder_is_reported_as_absent_not_thrown()
    {
        // Regression (sandbox, 2026-10-23): generated COM throws FileNotFoundException for 0x80070002,
        // not COMException, and setup crashed on a machine without the \QbtAutoUpdate folder.
        ScheduledTasks tasks = ScheduledTasks.Connect();

        Assert.Null(tasks.GetInfo("NoSuchTaskForQbtAutoUpdateTests"));
        Assert.False(tasks.Exists("NoSuchTaskForQbtAutoUpdateTests"));
        if (!tasks.Exists(ScheduledTasks.CheckTask) && !tasks.Exists(ScheduledTasks.ApplyTask))
            tasks.DeleteAll(); // folder absent on a development machine: must be a no-op
    }

    [Fact]
    public void Shell_link_round_trip()
    {
        string path = Path.Combine(_dir, "test.lnk");
        var shortcut = new ShortcutInfo(@"C:\Windows\System32\notepad.exe", "--a \"b c\"", @"C:\Windows", @"C:\Windows\System32\shell32.dll", 3, "QbtAutoUpdate test");

        Shortcuts.Create(path, shortcut);

        Assert.Equal(shortcut, Shortcuts.Read(path));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
