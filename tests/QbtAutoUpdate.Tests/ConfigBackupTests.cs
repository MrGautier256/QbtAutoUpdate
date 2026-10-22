using System.IO.Compression;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public sealed class ConfigBackupTests : IDisposable
{
    private static readonly QbtVersion Old = QbtVersion.Parse("5.2.4");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    private string Backups => Path.Combine(_root, "backups");

    private QbtProfile Profile(string name = "qBittorrent")
    {
        string config = Path.Combine(_root, "Roaming", name);
        string data = Path.Combine(_root, "Local", name);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(data, "BT_backup"));
        // The files found in a real %APPDATA%\qBittorrent.
        foreach (string file in new[] { "qBittorrent.ini", "qBittorrent-data.ini", "categories.json", "watched_folders.json", "lockfile", "qBittorrent_new.ini.DFxmXU" })
            File.WriteAllText(Path.Combine(config, file), file);
        Directory.CreateDirectory(Path.Combine(config, "rss"));
        File.WriteAllText(Path.Combine(data, "BT_backup", "0123.fastresume"), "resume");
        return new QbtProfile(config, data);
    }

    [Fact]
    public void Copies_settings_files_only()
    {
        string folder = ConfigBackup.Create([Profile()], Old, Backups, includeTorrents: false, DateTimeOffset.Now, NullLog.Instance)!;

        string[] copied = Directory.GetFiles(Path.Combine(folder, "config")).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["categories.json", "qBittorrent-data.ini", "qBittorrent.ini", "watched_folders.json"], copied);
        Assert.False(File.Exists(Path.Combine(folder, "BT_backup.zip")));
        Assert.StartsWith("5.2.4_", Path.GetFileName(folder), StringComparison.Ordinal);
    }

    [Fact]
    public void Archives_resume_data_when_asked()
    {
        string folder = ConfigBackup.Create([Profile()], Old, Backups, includeTorrents: true, DateTimeOffset.Now, NullLog.Instance)!;

        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(folder, "BT_backup.zip"));
        Assert.Equal("0123.fastresume", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void Keeps_one_folder_per_profile()
    {
        string folder = ConfigBackup.Create([Profile("qBittorrent"), Profile("qBittorrent_work")], Old, Backups, false, DateTimeOffset.Now, NullLog.Instance)!;

        Assert.True(File.Exists(Path.Combine(folder, "profile-1", "config", "qBittorrent.ini")));
        Assert.True(File.Exists(Path.Combine(folder, "profile-2", "config", "qBittorrent.ini")));
    }

    [Fact]
    public void Keeps_the_three_newest_backups()
    {
        QbtProfile profile = Profile();
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        for (int day = 0; day < 5; day++)
        {
            ConfigBackup.Create([profile], Old, Backups, false, start.AddDays(day), NullLog.Instance);
            Thread.Sleep(20); // distinct creation times
        }

        string[] kept = Directory.GetDirectories(Backups).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(3, kept.Length);
        Assert.All(kept, k => Assert.DoesNotContain("20261001", k, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_backups_in_the_same_second_do_not_collide()
    {
        QbtProfile profile = Profile();
        DateTimeOffset now = DateTimeOffset.Now;

        string first = ConfigBackup.Create([profile], Old, Backups, false, now, NullLog.Instance)!;
        string second = ConfigBackup.Create([profile], Old, Backups, false, now, NullLog.Instance)!;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Nothing_to_copy_gives_no_folder()
    {
        var missing = new QbtProfile(Path.Combine(_root, "none"), Path.Combine(_root, "none-data"));
        var log = new ListLog();

        Assert.Null(ConfigBackup.Create([missing], Old, Backups, true, DateTimeOffset.Now, log));
        Assert.True(log.Contains("does not exist"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
