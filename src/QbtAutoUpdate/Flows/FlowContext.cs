using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>Services shared by the user-side flows (status, check, update).</summary>
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

    public static FlowContext Create(CommandLine command)
    {
        AppPaths paths = AppPaths.ForCurrentUser();
        var fileLog = new FileLog(paths.UserLogsDir, command.Verbose ? LogLevel.Debug : LogLevel.Info);
        ILog log = command.Verbose ? new CompositeLog(fileLog, new ConsoleLog()) : fileLog;
        return new FlowContext(command, paths, fileLog, log, Console.Out);
    }

    public InstallInfo DetectInstall() => new InstallDetector(new SystemInstallEnvironment()).Detect();

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
