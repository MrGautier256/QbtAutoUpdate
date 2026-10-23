using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace QbtAutoUpdate.Core;

/// <summary>
/// Content of the Windows notifications (spec §4.9), as toast XML. Buttons use protocol activation:
/// Windows starts "QbtAutoUpdate.exe qbtautoupdate:&lt;action&gt;?v=&lt;version&gt;&amp;t=&lt;token&gt;", even when
/// the process that showed the notification has ended. The token, a per-user secret, keeps web pages
/// from triggering these actions through the protocol (DECISIONS D-38).
/// </summary>
public static class ToastContent
{
    public const string Scheme = "qbtautoupdate";
    public const string NewsUrl = "https://www.qbittorrent.org/news";

    public static string ActionUri(string action, string token, QbtVersion? version = null) =>
        $"{Scheme}:{action}?" + (version is { } v ? $"v={v}&" : "") + $"t={Uri.EscapeDataString(token)}";

    /// <summary>"qBittorrent X.Y.Z is available": Update now / Later (24 h) / Skip this version; the body opens the news page.</summary>
    public static string Available(QbtVersion target, QbtVersion? installed, Variant variant, string token) => Build(
        T.Format("ToastAvailableTitle", target),
        T.Format("ToastAvailableBody", installed?.ToString() ?? "?", variant.Describe()),
        NewsUrl,
        [
            (T.Get("ToastUpdateNow"), ActionUri("update", token, target)),
            (T.Get("ToastLater"), ActionUri("later", token, target)),
            (T.Get("ToastSkip"), ActionUri("skip", token, target)),
        ]);

    public static string Updated(QbtVersion version, Variant variant) =>
        Build(T.Format("ToastUpdatedTitle", version), T.Format("ToastUpdatedBody", variant.Describe()), NewsUrl, []);

    public static string Failed(string message, string token) =>
        Build(T.Get("ToastFailedTitle"), message, null, [(T.Get("ToastOpenLog"), ActionUri("logs", token))]);

    /// <summary>"Close qBittorrent to finish the update", with a Retry button (spec §4.3, step 5).</summary>
    public static string CloseFailed(QbtVersion target, string reason, string token) =>
        Build(T.Get("ToastCloseTitle"), T.Format("ToastCloseBody", target, reason), null, [(T.Get("ToastRetry"), ActionUri("update", token, target))]);

    public static string Info(string title, string message) => Build(title, message, null, []);

    private static string Build(string title, string body, string? launch, IReadOnlyList<(string Label, string Uri)> actions)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("toast");
            if (launch is not null)
            {
                writer.WriteAttributeString("launch", launch);
                writer.WriteAttributeString("activationType", "protocol");
            }
            writer.WriteStartElement("visual");
            writer.WriteStartElement("binding");
            writer.WriteAttributeString("template", "ToastGeneric");
            writer.WriteElementString("text", title);
            writer.WriteElementString("text", body);
            writer.WriteEndElement();
            writer.WriteEndElement();
            if (actions.Count > 0)
            {
                writer.WriteStartElement("actions");
                foreach ((string label, string uri) in actions)
                {
                    writer.WriteStartElement("action");
                    writer.WriteAttributeString("content", label);
                    writer.WriteAttributeString("activationType", "protocol");
                    writer.WriteAttributeString("arguments", uri);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        return text.ToString();
    }
}

/// <summary>A click on a notification button, as received on the command line.</summary>
public sealed record ToastAction(string Action, QbtVersion? Version, string Token)
{
    public static readonly IReadOnlyList<string> Actions = ["update", "later", "skip", "logs"];

    /// <summary>Parses "qbtautoupdate:skip?v=5.3.0&amp;t=…". Anything unexpected gives null.</summary>
    public static ToastAction? Parse(string? uri)
    {
        string prefix = ToastContent.Scheme + ":";
        if (uri is null || uri.Length > 512 || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string rest = uri[prefix.Length..];
        int question = rest.IndexOf('?');
        string action = (question < 0 ? rest : rest[..question]).Trim('/').ToLowerInvariant();
        if (!Actions.Contains(action))
            return null;

        string? version = null, token = null;
        foreach (string pair in (question < 0 ? "" : rest[(question + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
                continue;
            string value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            switch (pair[..equals])
            {
                case "v":
                    version = value;
                    break;
                case "t":
                    token = value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(token))
            return null;
        QbtVersion? parsed = null;
        if (version is not null)
        {
            if (!QbtVersion.TryParse(version, out QbtVersion v))
                return null;
            parsed = v;
        }
        return new ToastAction(action, parsed, token);
    }

    /// <summary>Constant-time comparison with the user's secret.</summary>
    public bool IsAuthorizedBy(string? userToken) =>
        !string.IsNullOrEmpty(userToken)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Token), Encoding.UTF8.GetBytes(userToken));

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
