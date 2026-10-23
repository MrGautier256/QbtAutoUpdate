using Microsoft.Win32;
using QbtAutoUpdate.Core;
using Windows.UI.Notifications;
using WinRtXml = Windows.Data.Xml.Dom;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// Windows notifications through the WinRT API of the Windows SDK projection (no extra package).
/// An unpackaged app needs an AppUserModelID registered with a display name and icon; the buttons
/// use the "qbtautoupdate:" protocol, registered machine-wide by setup.
/// </summary>
public static class Toasts
{
    public const string AppId = "QbtAutoUpdate";
    public const string IconFileName = "QbtAutoUpdate.png";
    public const string ProtocolKey = @"Software\Classes\" + ToastContent.Scheme;
    private const string AppIdKey = @"Software\Classes\AppUserModelId\" + AppId;
    private const string IconResource = "QbtAutoUpdate.QbtAutoUpdate.png";

    /// <param name="tag">A newer notification with the same tag replaces the older one in the notification center.</param>
    public static void Show(string xml, string tag, AppPaths paths, ILog log)
    {
        try
        {
            RegisterUser(paths);
            var document = new WinRtXml.XmlDocument();
            document.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(document) { Tag = tag, Group = AppId });
            log.Info($"Notification shown ({tag}).");
        }
        catch (Exception ex)
        {
            // A notification that cannot be shown must never break an update.
            log.Warn($"Could not show a notification: {ex.Message}");
        }
    }

    /// <summary>The per-user AppUserModelID registration Windows needs to show toasts of an unpackaged app.</summary>
    public static void RegisterUser(AppPaths paths) => WriteAppId(Registry.CurrentUser, paths);

    /// <summary>
    /// Setup: the icon file, the machine-wide AppUserModelID, and the "qbtautoupdate:" protocol that starts
    /// the installed exe with the clicked action.
    /// </summary>
    public static void RegisterMachine(AppPaths paths)
    {
        using (Stream icon = typeof(Toasts).Assembly.GetManifestResourceStream(IconResource)!)
        using (var file = new FileStream(Path.Combine(paths.ProgramDir, IconFileName), FileMode.Create, FileAccess.Write))
            icon.CopyTo(file);

        WriteAppId(Registry.LocalMachine, paths);

        using RegistryKey protocol = Registry.LocalMachine.CreateSubKey(ProtocolKey);
        protocol.SetValue("", "URL:QbtAutoUpdate notification actions");
        protocol.SetValue("URL Protocol", "");
        using (RegistryKey defaultIcon = protocol.CreateSubKey("DefaultIcon"))
            defaultIcon.SetValue("", paths.InstalledExe + ",0");
        using RegistryKey command = protocol.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{paths.InstalledExe}\" \"%1\"");
    }

    /// <summary>Uninstall: machine registrations, and the current user's (other users' keys are harmless leftovers).</summary>
    public static void Unregister()
    {
        Registry.LocalMachine.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(AppIdKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(AppIdKey, throwOnMissingSubKey: false);
        try
        {
            ToastNotificationManager.History.Clear(AppId);
        }
        catch (Exception)
        {
            // Nothing to clear, or no notification platform in this session.
        }
    }

    private static void WriteAppId(RegistryKey hive, AppPaths paths)
    {
        using RegistryKey key = hive.CreateSubKey(AppIdKey);
        key.SetValue("DisplayName", "QbtAutoUpdate");
        string icon = Path.Combine(paths.ProgramDir, IconFileName);
        if (File.Exists(icon))
            key.SetValue("IconUri", icon);
    }
}
