using System.Globalization;
using System.Text;
using System.Xml;

namespace QbtAutoUpdate.Core;

/// <summary>
/// XML definitions of the two scheduled tasks (spec §4.6). Accounts are given as SIDs, never as names:
/// "BUILTIN\Users" is localized ("BUILTIN\Utilisateurs" on a French Windows).
/// </summary>
public static class TaskDefinitions
{
    public const string UsersSid = "S-1-5-32-545";
    public const string SystemSid = "S-1-5-18";

    /// <summary>
    /// Apply task security descriptor: full control for SYSTEM and Administrators, read and execute for
    /// Users, so a non-elevated process can start it (zero UAC prompt, standard accounts included).
    /// </summary>
    public const string ApplySddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)";

    private const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// "Check": runs as every signed-in member of Users, with their own token and least privilege;
    /// 3 minutes after logon and every N hours; catches up on missed runs; needs a network; one instance.
    /// </summary>
    public static string Check(string exePath, int intervalHours, bool enabled, DateTime startLocal) => Build(writer =>
    {
        WriteRegistration(writer, "Checks for qBittorrent updates for each signed-in user (QbtAutoUpdate).", @"\QbtAutoUpdate\Check");

        writer.WriteStartElement("Triggers", Namespace);
        writer.WriteStartElement("LogonTrigger", Namespace);
        writer.WriteElementString("Enabled", Namespace, "true");
        writer.WriteElementString("Delay", Namespace, "PT3M");
        writer.WriteEndElement();
        writer.WriteStartElement("TimeTrigger", Namespace);
        writer.WriteStartElement("Repetition", Namespace);
        writer.WriteElementString("Interval", Namespace, $"PT{intervalHours}H");
        writer.WriteElementString("StopAtDurationEnd", Namespace, "false");
        writer.WriteEndElement();
        // Local time without offset: the trigger follows the PC's clock and time zone.
        writer.WriteElementString("StartBoundary", Namespace, startLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        writer.WriteElementString("Enabled", Namespace, "true");
        // Spread the requests of all PCs a little.
        writer.WriteElementString("RandomDelay", Namespace, "PT10M");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("Principals", Namespace);
        writer.WriteStartElement("Principal", Namespace);
        writer.WriteAttributeString("id", "Users");
        writer.WriteElementString("GroupId", Namespace, UsersSid);
        writer.WriteElementString("RunLevel", Namespace, "LeastPrivilege");
        writer.WriteEndElement();
        writer.WriteEndElement();

        WriteSettings(writer, enabled, startWhenAvailable: true, needsNetwork: true, TimeSpan.FromHours(1));
        WriteAction(writer, "Users", exePath, "check");
    });

    /// <summary>"Apply": SYSTEM, highest privileges, no trigger (on demand only), fixed command line.</summary>
    public static string Apply(string exePath) => Build(writer =>
    {
        WriteRegistration(writer, "Installs a verified, signed qBittorrent update handed over by QbtAutoUpdate.", @"\QbtAutoUpdate\Apply");
        writer.WriteStartElement("Triggers", Namespace);
        writer.WriteEndElement();

        writer.WriteStartElement("Principals", Namespace);
        writer.WriteStartElement("Principal", Namespace);
        writer.WriteAttributeString("id", "System");
        writer.WriteElementString("UserId", Namespace, SystemSid);
        writer.WriteElementString("RunLevel", Namespace, "HighestAvailable");
        writer.WriteEndElement();
        writer.WriteEndElement();

        WriteSettings(writer, enabled: true, startWhenAvailable: false, needsNetwork: false, TimeSpan.FromMinutes(30));
        WriteAction(writer, "System", exePath, "apply");
    });

    private static void WriteRegistration(XmlWriter writer, string description, string uri)
    {
        writer.WriteStartElement("RegistrationInfo", Namespace);
        writer.WriteElementString("Author", Namespace, "QbtAutoUpdate");
        writer.WriteElementString("Description", Namespace, description);
        writer.WriteElementString("URI", Namespace, uri);
        writer.WriteEndElement();
    }

    private static void WriteSettings(XmlWriter writer, bool enabled, bool startWhenAvailable, bool needsNetwork, TimeSpan limit)
    {
        writer.WriteStartElement("Settings", Namespace);
        writer.WriteElementString("MultipleInstancesPolicy", Namespace, "IgnoreNew");
        writer.WriteElementString("DisallowStartIfOnBatteries", Namespace, "false");
        writer.WriteElementString("StopIfGoingOnBatteries", Namespace, "false");
        writer.WriteElementString("AllowHardTerminate", Namespace, "true");
        writer.WriteElementString("StartWhenAvailable", Namespace, startWhenAvailable ? "true" : "false");
        writer.WriteElementString("RunOnlyIfNetworkAvailable", Namespace, needsNetwork ? "true" : "false");
        writer.WriteStartElement("IdleSettings", Namespace);
        writer.WriteElementString("StopOnIdleEnd", Namespace, "false");
        writer.WriteElementString("RestartOnIdle", Namespace, "false");
        writer.WriteEndElement();
        writer.WriteElementString("AllowStartOnDemand", Namespace, "true");
        writer.WriteElementString("Enabled", Namespace, enabled ? "true" : "false");
        writer.WriteElementString("Hidden", Namespace, "false");
        writer.WriteElementString("RunOnlyIfIdle", Namespace, "false");
        writer.WriteElementString("ExecutionTimeLimit", Namespace, XmlConvert.ToString(limit));
        writer.WriteElementString("Priority", Namespace, "7");
        writer.WriteEndElement();
    }

    private static void WriteAction(XmlWriter writer, string context, string exePath, string arguments)
    {
        writer.WriteStartElement("Actions", Namespace);
        writer.WriteAttributeString("Context", context);
        writer.WriteStartElement("Exec", Namespace);
        writer.WriteElementString("Command", Namespace, $"\"{exePath}\"");
        writer.WriteElementString("Arguments", Namespace, arguments);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string Build(Action<XmlWriter> body)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Task", Namespace);
            writer.WriteAttributeString("version", "1.4");
            body(writer);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return builder.ToString();
    }
}
