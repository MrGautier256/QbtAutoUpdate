namespace QbtAutoUpdate.Core;

/// <summary>
/// Every location the tool reads or writes. Nothing is hard-coded: the roots come from the
/// Windows known folders of the current machine and user.
/// </summary>
public sealed class AppPaths(string programFilesRoot, string programDataRoot, string localAppDataRoot)
{
    public const string ProductFolder = "QbtAutoUpdate";
    public const string ExeName = "QbtAutoUpdate.exe";

    /// <summary>%ProgramFiles%\QbtAutoUpdate: the only binary the SYSTEM task may run (admin-writable only).</summary>
    public string ProgramDir { get; } = Path.Combine(programFilesRoot, ProductFolder);

    public string InstalledExe => Path.Combine(ProgramDir, ExeName);

    /// <summary>%ProgramData%\QbtAutoUpdate: machine-wide data.</summary>
    public string MachineDataDir { get; } = Path.Combine(programDataRoot, ProductFolder);

    public string ConfigFile => Path.Combine(MachineDataDir, "config.json");
    public string StateFile => Path.Combine(MachineDataDir, "state.json");
    public string InboxDir => Path.Combine(MachineDataDir, "inbox");
    public string WorkDir => Path.Combine(MachineDataDir, "work");
    public string MachineLogsDir => Path.Combine(MachineDataDir, "logs");

    /// <summary>%LOCALAPPDATA%\QbtAutoUpdate: per-user data.</summary>
    public string UserDataDir { get; } = Path.Combine(localAppDataRoot, ProductFolder);

    public string UserConfigFile => Path.Combine(UserDataDir, "user.json");
    public string StagingDir => Path.Combine(UserDataDir, "staging");
    public string BackupsDir => Path.Combine(UserDataDir, "backups");
    public string UserLogsDir => Path.Combine(UserDataDir, "logs");

    public static AppPaths ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
