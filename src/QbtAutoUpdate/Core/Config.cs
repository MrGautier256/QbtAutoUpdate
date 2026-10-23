using System.Text.Json;

namespace QbtAutoUpdate.Core;

public enum UpdateMode
{
    /// <summary>Show a notification with "Update now / Later / Skip this version".</summary>
    Notify,
    /// <summary>Close, install and restart without asking.</summary>
    Auto,
}

/// <summary>Which variant to install: keep the installed one, or force a series (an explicit user choice).</summary>
public enum VariantSetting
{
    Auto,
    Lt2,
    Lt12,
}

public enum NotificationLevel
{
    All,
    Errors,
    None,
}

/// <summary>
/// Machine-wide settings, %ProgramData%\QbtAutoUpdate\config.json (written by setup as admin,
/// readable by all users). A missing or damaged file yields safe defaults.
/// </summary>
public sealed record MachineConfig
{
    /// <summary>Master switch for automatic checks (the "on/off" of the settings dialog).</summary>
    public bool Enabled { get; init; } = true;
    public UpdateMode Mode { get; init; } = UpdateMode.Notify;
    public int CheckIntervalHours { get; init; } = 6;
    public VariantSetting Variant { get; init; } = VariantSetting.Auto;
    public int MinReleaseAgeHours { get; init; }
    public bool SkipOnMeteredConnection { get; init; } = true;
    public int CloseTimeoutSeconds { get; init; } = 120;
    public bool RestoreDesktopShortcut { get; init; } = true;
    public bool BackupConfig { get; init; } = true;
    public bool BackupTorrents { get; init; }
    public NotificationLevel Notifications { get; init; } = NotificationLevel.All;
    /// <summary>Never enabled by default: killing qBittorrent can lose resume data (see README).</summary>
    public bool AllowForceClose { get; init; }
    /// <summary>Set by "rollback": automatic runs do not reinstall the version that was rolled back.</summary>
    public QbtVersion? SkippedVersion { get; init; }

    public static readonly MachineConfig Default = new();

    public static MachineConfig Load(string path, List<string> warnings)
    {
        using JsonDocument? document = JsonFields.TryLoad(path, "config.json", warnings);
        if (document is null)
            return Default;

        var f = new JsonFields(document.RootElement, "config.json", warnings);
        return new MachineConfig
        {
            Enabled = f.Bool("enabled", Default.Enabled),
            Mode = f.Choice("mode", Default.Mode, ("notify", UpdateMode.Notify), ("auto", UpdateMode.Auto)),
            CheckIntervalHours = f.Int("checkIntervalHours", Default.CheckIntervalHours, 1, 168),
            Variant = f.Choice("variant", Default.Variant, ("auto", VariantSetting.Auto), ("lt2", VariantSetting.Lt2), ("lt12", VariantSetting.Lt12)),
            MinReleaseAgeHours = f.Int("minReleaseAgeHours", Default.MinReleaseAgeHours, 0, 720),
            SkipOnMeteredConnection = f.Bool("skipOnMeteredConnection", Default.SkipOnMeteredConnection),
            CloseTimeoutSeconds = f.Int("closeTimeoutSeconds", Default.CloseTimeoutSeconds, 10, 1800),
            RestoreDesktopShortcut = f.Bool("restoreDesktopShortcut", Default.RestoreDesktopShortcut),
            BackupConfig = f.Bool("backupConfig", Default.BackupConfig),
            BackupTorrents = f.Bool("backupTorrents", Default.BackupTorrents),
            Notifications = f.Choice("notifications", Default.Notifications, ("all", NotificationLevel.All), ("errors", NotificationLevel.Errors), ("none", NotificationLevel.None)),
            AllowForceClose = f.Bool("allowForceClose", Default.AllowForceClose),
            SkippedVersion = f.Version("skippedVersion"),
        };
    }

    public void Save(string path) => JsonFiles.WriteAtomically(path, Write);

    public string ToJson() => JsonFiles.ToText(Write);

    private void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteBoolean("enabled", Enabled);
        w.WriteString("mode", Mode == UpdateMode.Auto ? "auto" : "notify");
        w.WriteNumber("checkIntervalHours", CheckIntervalHours);
        w.WriteString("variant", Variant switch { VariantSetting.Lt2 => "lt2", VariantSetting.Lt12 => "lt12", _ => "auto" });
        w.WriteNumber("minReleaseAgeHours", MinReleaseAgeHours);
        w.WriteBoolean("skipOnMeteredConnection", SkipOnMeteredConnection);
        w.WriteNumber("closeTimeoutSeconds", CloseTimeoutSeconds);
        w.WriteBoolean("restoreDesktopShortcut", RestoreDesktopShortcut);
        w.WriteBoolean("backupConfig", BackupConfig);
        w.WriteBoolean("backupTorrents", BackupTorrents);
        w.WriteString("notifications", Notifications switch { NotificationLevel.Errors => "errors", NotificationLevel.None => "none", _ => "all" });
        w.WriteBoolean("allowForceClose", AllowForceClose);
        if (SkippedVersion is { } skipped)
            w.WriteString("skippedVersion", skipped.ToString());
        w.WriteEndObject();
    }
}

/// <summary>
/// Per-user choices, %LOCALAPPDATA%\QbtAutoUpdate\user.json. WebUI secrets are stored encrypted
/// with DPAPI for the current user and kept in clear only in memory.
/// </summary>
public sealed record UserSettings
{
    public QbtVersion? SkippedVersion { get; init; }
    public DateTimeOffset? SnoozeUntil { get; init; }
    public WebUiSettings? WebUi { get; init; }
    /// <summary>Secret carried by notification buttons (ToastAction); only this user can read user.json.</summary>
    public string? NotificationToken { get; init; }
    /// <summary>Last version announced by a notification, so it is not announced again every few hours.</summary>
    public QbtVersion? NotifiedVersion { get; init; }
    public DateTimeOffset? NotifiedAt { get; init; }

    public static readonly UserSettings Default = new();

    public static UserSettings Load(string path, List<string> warnings)
    {
        using JsonDocument? document = JsonFields.TryLoad(path, "user.json", warnings);
        if (document is null)
            return Default;

        var f = new JsonFields(document.RootElement, "user.json", warnings);
        return new UserSettings
        {
            SkippedVersion = f.Version("skippedVersion"),
            SnoozeUntil = f.Date("snoozeUntil"),
            WebUi = LoadWebUi(f.Object("webUi"), warnings),
            NotificationToken = f.String("notificationToken"),
            NotifiedVersion = f.Version("notifiedVersion"),
            NotifiedAt = f.Date("notifiedAt"),
        };
    }

    public void Save(string path) => JsonFiles.WriteAtomically(path, w =>
    {
        w.WriteStartObject();
        if (SkippedVersion is { } skipped)
            w.WriteString("skippedVersion", skipped.ToString());
        if (SnoozeUntil is { } snooze)
            w.WriteString("snoozeUntil", JsonFiles.ToIso(snooze));
        if (NotificationToken is { } token)
            w.WriteString("notificationToken", token);
        if (NotifiedVersion is { } notified)
            w.WriteString("notifiedVersion", notified.ToString());
        if (NotifiedAt is { } notifiedAt)
            w.WriteString("notifiedAt", JsonFiles.ToIso(notifiedAt));
        if (WebUi is { } webUi)
        {
            w.WriteStartObject("webUi");
            w.WriteString("url", webUi.Url.AbsoluteUri);
            if (webUi.ApiKey is { } apiKey)
                w.WriteString("apiKeyProtected", Platform.Dpapi.Protect(apiKey));
            if (webUi.Username is { } username)
                w.WriteString("username", username);
            if (webUi.Password is { } password)
                w.WriteString("passwordProtected", Platform.Dpapi.Protect(password));
            w.WriteEndObject();
        }
        w.WriteEndObject();
    });

    private static WebUiSettings? LoadWebUi(JsonFields? f, List<string> warnings)
    {
        if (f is null)
            return null;
        if (!Uri.TryCreate(f.String("url"), UriKind.Absolute, out Uri? url) || !WebUiClient.IsAllowedUrl(url))
        {
            warnings.Add("user.json: \"webUi.url\" must be an http(s) URL on localhost; the WebUI is not used.");
            return null;
        }

        string? apiKeyProtected = f.String("apiKeyProtected");
        string? passwordProtected = f.String("passwordProtected");
        string? apiKey = Platform.Dpapi.Unprotect(apiKeyProtected);
        string? password = Platform.Dpapi.Unprotect(passwordProtected);
        if ((apiKeyProtected is not null && apiKey is null) || (passwordProtected is not null && password is null))
        {
            warnings.Add("user.json: the WebUI secret cannot be decrypted (other user or machine); configure the WebUI again.");
            return null;
        }
        return new WebUiSettings(url, apiKey, f.String("username"), password);
    }
}
