using System.Globalization;
using System.IO.Compression;

namespace QbtAutoUpdate.Core;

/// <summary>
/// Copies qBittorrent's settings before an update, once it is closed (spec §4.7):
/// %LOCALAPPDATA%\QbtAutoUpdate\backups\&lt;old version&gt;_&lt;date&gt;\, keeping the newest <see cref="Keep"/>.
/// </summary>
public static class ConfigBackup
{
    public const int Keep = 3;

    /// <summary>
    /// Backs up each profile. Settings are the top-level *.ini and *.json files of the config folder
    /// (qBittorrent.ini, qBittorrent-data.ini, categories.json, watched_folders.json…); temporary
    /// "*_new.ini.XXXXXX" files and the lockfile are skipped by the extension filter. BT_backup is zipped
    /// only when <paramref name="includeTorrents"/> is set. Returns the backup folder, or null if nothing was copied.
    /// </summary>
    public static string? Create(IReadOnlyList<QbtProfile> profiles, QbtVersion oldVersion, string backupsRoot,
        bool includeTorrents, DateTimeOffset now, ILog log)
    {
        string stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(backupsRoot, $"{oldVersion}_{stamp}");
        for (int n = 2; Directory.Exists(target); n++)
            target = Path.Combine(backupsRoot, $"{oldVersion}_{stamp}_{n}");

        int copied = 0;
        for (int index = 0; index < profiles.Count; index++)
        {
            QbtProfile profile = profiles[index];
            string destination = profiles.Count == 1 ? target : Path.Combine(target, $"profile-{index + 1}");

            if (Directory.Exists(profile.ConfigDir))
            {
                string configTarget = Path.Combine(destination, "config");
                foreach (string file in Directory.EnumerateFiles(profile.ConfigDir))
                {
                    string extension = Path.GetExtension(file);
                    if (!extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(configTarget);
                    File.Copy(file, Path.Combine(configTarget, Path.GetFileName(file)));
                    copied++;
                }
            }
            else
            {
                log.Warn($"Config backup: {profile.ConfigDir} does not exist.");
            }

            string torrents = Path.Combine(profile.DataDir, "BT_backup");
            if (includeTorrents && Directory.Exists(torrents))
            {
                try
                {
                    Directory.CreateDirectory(destination);
                    ZipFile.CreateFromDirectory(torrents, Path.Combine(destination, "BT_backup.zip"), CompressionLevel.Fastest, includeBaseDirectory: false);
                    copied++;
                }
                catch (IOException ex)
                {
                    log.Warn($"Config backup: could not archive {torrents}: {ex.Message}");
                }
            }

            if (Directory.Exists(destination))
                File.WriteAllText(Path.Combine(destination, "source.txt"), $"config: {profile.ConfigDir}{Environment.NewLine}data: {profile.DataDir}{Environment.NewLine}");
        }

        if (copied == 0)
            return null;

        log.Info($"Backed up qBittorrent settings to {target}.");
        Prune(backupsRoot, Keep, log);
        return target;
    }

    internal static void Prune(string backupsRoot, int keep, ILog log)
    {
        var old = new DirectoryInfo(backupsRoot).EnumerateDirectories()
            .OrderByDescending(d => d.CreationTimeUtc)
            .ThenByDescending(d => d.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (DirectoryInfo directory in old)
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not delete the old backup {directory.Name}: {ex.Message}");
            }
        }
    }
}
