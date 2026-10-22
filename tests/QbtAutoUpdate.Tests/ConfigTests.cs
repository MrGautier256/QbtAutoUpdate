using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    private string File(string content)
    {
        string path = Path.Combine(_dir, "config.json");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_missing_file_gives_defaults_silently()
    {
        var warnings = new List<string>();

        MachineConfig config = MachineConfig.Load(Path.Combine(_dir, "none.json"), warnings);

        Assert.Equal(MachineConfig.Default, config);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Safe_defaults()
    {
        MachineConfig config = MachineConfig.Default;

        Assert.Equal(UpdateMode.Notify, config.Mode);
        Assert.Equal(VariantSetting.Auto, config.Variant);
        Assert.Equal(6, config.CheckIntervalHours);
        Assert.Equal(120, config.CloseTimeoutSeconds);
        Assert.True(config.SkipOnMeteredConnection);
        Assert.False(config.AllowForceClose);
        Assert.False(config.BackupTorrents);
    }

    [Fact]
    public void Reads_every_setting()
    {
        string path = File("""
            {
              "enabled": false, "mode": "auto", "checkIntervalHours": 12, "variant": "lt12",
              "minReleaseAgeHours": 48, "skipOnMeteredConnection": false, "closeTimeoutSeconds": 300,
              "restoreDesktopShortcut": false, "backupConfig": false, "backupTorrents": true,
              "notifications": "errors", "allowForceClose": true, "futureSetting": 1
            }
            """);
        var warnings = new List<string>();

        MachineConfig config = MachineConfig.Load(path, warnings);

        Assert.Empty(warnings);
        Assert.Equal(new MachineConfig
        {
            Enabled = false, Mode = UpdateMode.Auto, CheckIntervalHours = 12, Variant = VariantSetting.Lt12,
            MinReleaseAgeHours = 48, SkipOnMeteredConnection = false, CloseTimeoutSeconds = 300,
            RestoreDesktopShortcut = false, BackupConfig = false, BackupTorrents = true,
            Notifications = NotificationLevel.Errors, AllowForceClose = true,
        }, config);
    }

    [Fact]
    public void A_bad_value_only_resets_that_value()
    {
        string path = File("""{ "mode": "sometimes", "checkIntervalHours": 0, "variant": "lt20", "allowForceClose": "yes", "closeTimeoutSeconds": 90 }""");
        var warnings = new List<string>();

        MachineConfig config = MachineConfig.Load(path, warnings);

        Assert.Equal(UpdateMode.Notify, config.Mode);
        Assert.Equal(6, config.CheckIntervalHours);
        Assert.Equal(VariantSetting.Auto, config.Variant);
        Assert.False(config.AllowForceClose);
        Assert.Equal(90, config.CloseTimeoutSeconds);
        Assert.Equal(4, warnings.Count);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void A_corrupt_file_gives_defaults_with_a_warning(string content)
    {
        var warnings = new List<string>();

        Assert.Equal(MachineConfig.Default, MachineConfig.Load(File(content), warnings));
        Assert.Single(warnings);
    }

    [Fact]
    public void Round_trips()
    {
        var config = new MachineConfig { Mode = UpdateMode.Auto, Variant = VariantSetting.Lt2, CheckIntervalHours = 3 };
        string path = Path.Combine(_dir, "sub", "config.json");

        config.Save(path);

        Assert.Equal(config, MachineConfig.Load(path, []));
    }

    [Fact]
    public void User_settings_round_trip()
    {
        string path = Path.Combine(_dir, "user.json");
        var user = new UserSettings { SkippedVersion = QbtVersion.Parse("5.3.0"), SnoozeUntil = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.FromHours(2)) };

        user.Save(path);

        Assert.Equal(user, UserSettings.Load(path, []));
    }

    [Fact]
    public void State_round_trips()
    {
        string path = Path.Combine(_dir, "state.json");
        var state = new UpdateState
        {
            LastInstall = new InstallRecord(QbtVersion.Parse("5.3.0"), Variant.Lt12, DateTimeOffset.UnixEpoch, "success", 0, null),
            BusySince = DateTimeOffset.UnixEpoch,
        };

        state.Save(path);

        Assert.Equal(state, UpdateState.Load(path, []));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
