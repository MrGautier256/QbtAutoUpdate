using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>Services shared by the flows.</summary>
internal sealed class FlowContext : IDisposable
{
    private readonly FileLog _fileLog;

    private FlowContext(CommandLine command, AppPaths paths, FileLog fileLog, ILog log, TextWriter output)
    {
        Command = command;
        Paths = paths;
        _fileLog = fileLog;
        Log = log;
        Output = output;
        Http = new SafeHttpClient(log);
        Versions = new VersionSource(Http, log);
        SourceForge = new SourceForgeClient(Http, log);
        Downloader = new Downloader(Http, log);
    }

    public CommandLine Command { get; }
    public AppPaths Paths { get; }
    public ILog Log { get; }
    /// <summary>User-facing output (the attached console, if any).</summary>
    public TextWriter Output { get; }
    public SafeHttpClient Http { get; }
    public VersionSource Versions { get; }
    public SourceForgeClient SourceForge { get; }
    public Downloader Downloader { get; }
    public string LogFile => _fileLog.CurrentFile;

    /// <param name="output">Where user-facing messages go: the console by default, a buffer for the dialogs.</param>
    public static FlowContext Create(CommandLine command, TextWriter? output = null)
    {
        AppPaths paths = AppPaths.ForCurrentUser();
        // Machine operations log to %ProgramData% once elevated; a non-elevated process never creates
        // that folder (a user-created folder there would not be trusted by the SYSTEM side).
        bool machine = command.Verb is "apply" or "setup" or "config" && (Elevation.IsElevated || Elevation.IsSystem);
        var fileLog = new FileLog(machine ? paths.MachineLogsDir : paths.UserLogsDir, command.Verbose ? LogLevel.Debug : LogLevel.Info);
        ILog log = command.Verbose && output is null ? new CompositeLog(fileLog, new ConsoleLog()) : fileLog;
        return new FlowContext(command, paths, fileLog, log, output ?? Console.Out);
    }

    /// <summary>Stops writing the log file (uninstall deletes the folder it lives in).</summary>
    public void StopFileLogging() => _fileLog.Enabled = false;

    /// <summary>
    /// True for runs nobody watches (the scheduled check, a notification click): their results are
    /// reported as Windows notifications. Terminal and window runs show them themselves.
    /// </summary>
    public bool ToastsEnabled { get; set; }

    public void NotifyAvailable(MachineConfig config, UserSettings user, UpdatePlan plan) =>
        Toast(config, isError: false, always: true, "available",
            () => ToastContent.Available(plan.Latest!.Version, plan.Install.Version, plan.Variant!.Value, TokenFor(user)));

    public void NotifyUpdated(MachineConfig config, QbtVersion version, Variant variant) =>
        Toast(config, isError: false, always: false, "result", () => ToastContent.Updated(version, variant));

    public void NotifyFailure(MachineConfig config, UserSettings user, string message) =>
        Toast(config, isError: true, always: false, "result", () => ToastContent.Failed(message, TokenFor(user)));

    public void NotifyCloseFailed(MachineConfig config, UserSettings user, QbtVersion target, string reason) =>
        Toast(config, isError: true, always: false, "result", () => ToastContent.CloseFailed(target, reason, TokenFor(user)));

    public void NotifyInfo(MachineConfig config, string title, string message) =>
        Toast(config, isError: false, always: true, "result", () => ToastContent.Info(title, message));

    /// <param name="always">Shown unless notifications are off: the "available" notification is what notify mode is for.</param>
    private void Toast(MachineConfig config, bool isError, bool always, string tag, Func<string> xml)
    {
        if (!ToastsEnabled || config.Notifications == NotificationLevel.None)
            return;
        if (config.Notifications == NotificationLevel.Errors && !isError && !always)
            return;
        Toasts.Show(xml(), tag, Paths, Log);
    }

    /// <summary>The user's notification secret, created and saved on first use.</summary>
    public string TokenFor(UserSettings user)
    {
        if (!string.IsNullOrEmpty(user.NotificationToken))
            return user.NotificationToken;
        UserSettings current = UserSettings.Load(Paths.UserConfigFile, []);
        if (string.IsNullOrEmpty(current.NotificationToken))
        {
            current = current with { NotificationToken = ToastAction.NewToken() };
            current.Save(Paths.UserConfigFile);
        }
        return current.NotificationToken!;
    }

    /// <summary>Detects qBittorrent, including an install interrupted by a power cut (state.json).</summary>
    public InstallInfo DetectInstall(UpdateState state) =>
        InstallInfoRecovery.Recover(new InstallDetector(new SystemInstallEnvironment()).Detect(), state);

    public UpdatePlanner CreatePlanner(UserSettings user) => new(
        Versions, SourceForge, Log, SystemInfo.NativeArchitecture(), () => SystemInfo.IsMeteredConnection(Log), () => DateTimeOffset.Now,
        user.WebUi is null ? null : cancellationToken => ReadVariantFromWebUiAsync(user, cancellationToken));

    /// <summary>A WebUI client when the user configured one, otherwise null.</summary>
    public WebUiClient? CreateWebUi(UserSettings user) => user.WebUi is { } settings ? new WebUiClient(settings) : null;

    private async Task<Variant?> ReadVariantFromWebUiAsync(UserSettings user, CancellationToken cancellationToken)
    {
        using WebUiClient? client = CreateWebUi(user);
        if (client is null)
            return null;
        try
        {
            string? libtorrent = await client.GetLibtorrentVersionAsync(cancellationToken).ConfigureAwait(false);
            Log.Info($"WebUI reports libtorrent {libtorrent}.");
            return UpdatePlanner.VariantFromLibtorrentVersion(libtorrent);
        }
        catch (WebUiException ex)
        {
            Log.Warn($"WebUI buildInfo unavailable: {ex.Message}");
            return null;
        }
    }

    public (MachineConfig Config, UserSettings User, UpdateState State) LoadSettings()
    {
        var warnings = new List<string>();
        MachineConfig config = MachineConfig.Load(Paths.ConfigFile, warnings);
        UserSettings user = UserSettings.Load(Paths.UserConfigFile, warnings);
        UpdateState state = UpdateState.Load(Paths.StateFile, warnings);
        foreach (string warning in warnings)
            Log.Warn(warning);
        return (config, user, state);
    }

    /// <summary>Writes a line to the console and to the log file.</summary>
    public void Say(string message)
    {
        Output.WriteLine(message);
        _fileLog.Write(LogLevel.Info, message);
    }

    public void Dispose()
    {
        Http.Dispose();
        _fileLog.Dispose();
    }

    private sealed class ConsoleLog : ILog
    {
        public void Write(LogLevel level, string message) =>
            Console.Out.WriteLine($"  [{level.ToString().ToLowerInvariant()}] {message}");
    }
}
