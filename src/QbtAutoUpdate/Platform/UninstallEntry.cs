using Microsoft.Win32;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// QbtAutoUpdate's line in Settings › Apps › Installed apps: "Uninstall" runs the uninstall verb,
/// "Modify" opens the settings dialog.
/// </summary>
public static class UninstallEntry
{
    public const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QbtAutoUpdate";

    public static void Write(AppPaths paths, string version)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(Key);
        string exe = $"\"{paths.InstalledExe}\"";
        key.SetValue("DisplayName", "QbtAutoUpdate");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "QbtAutoUpdate contributors");
        key.SetValue("URLInfoAbout", "https://github.com/MrGautier256/QbtAutoUpdate");
        key.SetValue("DisplayIcon", paths.InstalledExe + ",0");
        key.SetValue("InstallLocation", paths.ProgramDir);
        key.SetValue("UninstallString", exe + " uninstall");
        key.SetValue("QuietUninstallString", exe + " uninstall --quiet");
        key.SetValue("ModifyPath", exe);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long size = new FileInfo(paths.InstalledExe).Length / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(size, int.MaxValue), RegistryValueKind.DWord);
    }

    public static void Delete() => Registry.LocalMachine.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
}
