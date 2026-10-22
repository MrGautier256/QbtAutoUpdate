namespace QbtAutoUpdate.Core;

/// <summary>Configuration and data folders of one qBittorrent profile.</summary>
public sealed record QbtProfile(string ConfigDir, string DataDir);

/// <summary>
/// How a running qBittorrent was started, reduced to the options that describe the instance
/// (src/app/cmdoptions.cpp). File paths, magnet links and per-torrent options are dropped: replaying
/// them on restart would add the same torrent again (docs/FINDINGS.md §7.5).
/// </summary>
public sealed record QbtLaunch(IReadOnlyList<string> Arguments, string? ProfileDir, string? ConfigurationName, int DroppedCount)
{
    public static readonly QbtLaunch Default = new([], null, null, 0);

    private static readonly string[] Flags = ["--relative-fastresume", "--no-splash", "--confirm-legal-notice"];
    private static readonly string[] ValueOptions = ["--profile", "--configuration", "--webui-port", "--torrenting-port"];

    /// <summary>Filters qBittorrent's arguments (without the program name).</summary>
    public static QbtLaunch FromArguments(IEnumerable<string> arguments)
    {
        var kept = new List<string>();
        string? profile = null, configuration = null;
        int dropped = 0;

        foreach (string argument in arguments)
        {
            // qBittorrent itself treats any "--….torrent" argument as a file to add.
            if (argument.EndsWith(".torrent", StringComparison.Ordinal))
            {
                dropped++;
                continue;
            }

            if (Flags.Contains(argument, StringComparer.Ordinal))
            {
                kept.Add(argument);
                continue;
            }

            string? option = ValueOptions.FirstOrDefault(o => argument.StartsWith(o + "=", StringComparison.Ordinal));
            if (option is null)
            {
                dropped++;
                continue;
            }

            kept.Add(argument);
            string value = Unquote(argument[(option.Length + 1)..]);
            if (option == "--profile")
                profile = value.Length > 0 ? value : null;
            else if (option == "--configuration")
                configuration = value.Length > 0 ? value : null;
        }

        return new QbtLaunch(kept, profile, configuration, dropped);
    }

    /// <summary>
    /// Locates the profile the way src/base/profile_p.cpp does: a custom root (--profile, or a
    /// "profile" folder next to the exe for portable mode) holds qBittorrent[_name]\config and \data;
    /// otherwise %APPDATA%\qBittorrent[_name] and %LOCALAPPDATA%\qBittorrent[_name].
    /// Returns null for a relative --profile: qBittorrent resolved it against a working directory we cannot know.
    /// </summary>
    public QbtProfile? ResolveProfile(string? installDir, string appData, string localAppData, Func<string, bool> directoryExists)
    {
        string name = "qBittorrent" + (ConfigurationName is { Length: > 0 } configuration ? "_" + configuration : "");

        string? root = ProfileDir;
        if (root is not null && !Path.IsPathFullyQualified(root))
            return null;
        if (root is null && installDir is not null && directoryExists(Path.Combine(installDir, "profile")))
            root = Path.Combine(installDir, "profile");

        if (root is not null)
        {
            string basePath = Path.Combine(root, name);
            return new QbtProfile(Path.Combine(basePath, "config"), Path.Combine(basePath, "data"));
        }
        return new QbtProfile(Path.Combine(appData, name), Path.Combine(localAppData, name));
    }

    /// <summary>Same unquoting as qBittorrent's Utils::String::unquote(value, "'\"").</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
}
