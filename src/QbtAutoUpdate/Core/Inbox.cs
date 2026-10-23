using System.Text.RegularExpressions;

namespace QbtAutoUpdate.Core;

/// <summary>A file found in the inbox, as the SYSTEM side sees it.</summary>
public sealed record InboxEntry(string Path, string Name, long Length, DateTime LastWriteUtc, bool IsReparsePoint);

/// <summary>An installer and its signature dropped by a user for the SYSTEM task.</summary>
public sealed record InboxPair(InboxEntry Installer, InboxEntry Signature, string RequestId, string InstallerName);

/// <summary>
/// Naming and selection rules of %ProgramData%\QbtAutoUpdate\inbox (spec §4.3, SYSTEM side, step 2).
/// Users may only create files there. Each request uses a unique prefix, so users never collide and
/// the requester can find its own result in state.json. Everything in the inbox is treated as hostile.
/// </summary>
public static partial class Inbox
{
    public const string Separator = "__";
    public const long MaxInstallerBytes = Downloader.MaxInstallerBytes;
    public const long MaxSignatureBytes = Downloader.MaxSignatureBytes;

    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    public static string FileNameFor(string requestId, string installerName) => requestId + Separator + installerName;

    /// <summary>
    /// Picks the most recent valid pair: a regular file with a known installer name, within size limits,
    /// next to its ".asc". Returns it with every other entry, which the caller deletes unread.
    /// </summary>
    public static (InboxPair? Pair, List<InboxEntry> Discard) Select(IReadOnlyList<InboxEntry> entries)
    {
        var byName = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<InboxPair>();
        foreach (InboxEntry entry in entries)
        {
            Match match = NameRegex().Match(entry.Name);
            if (!match.Success || entry.IsReparsePoint || entry.Length is <= 0 or > MaxInstallerBytes)
                continue;
            string installerName = match.Groups["name"].Value;
            if (installerName.EndsWith(".asc", StringComparison.OrdinalIgnoreCase) || InstallerRunner.ParseInstallerName(installerName) is null)
                continue;
            if (!byName.TryGetValue(entry.Name + ".asc", out InboxEntry? signature)
                || signature.IsReparsePoint || signature.Length is <= 0 or > MaxSignatureBytes)
                continue;
            candidates.Add(new InboxPair(entry, signature, match.Groups["id"].Value, installerName));
        }

        InboxPair? selected = candidates.OrderByDescending(c => c.Installer.LastWriteUtc).FirstOrDefault();
        var discard = entries.Where(e => selected is null || (e != selected.Installer && e != selected.Signature)).ToList();
        return (selected, discard);
    }

    [GeneratedRegex(@"^(?<id>[0-9a-f]{32})__(?<name>[^\\/:]+)$")]
    private static partial Regex NameRegex();
}
