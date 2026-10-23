namespace QbtAutoUpdate;

/// <summary>Parsed command line: <c>QbtAutoUpdate.exe &lt;verb&gt; [--option value] [--flag]</c>.</summary>
internal sealed record CommandLine(
    string? Verb,
    bool DryRun,
    bool Verbose,
    bool Help,
    IReadOnlyDictionary<string, string?> Options,
    string? Error)
{
    public static readonly IReadOnlyList<string> Verbs = ["setup", "uninstall", "config", "check", "update", "apply", "status", "rollback", "restart", "webui", "help"];

    private static readonly string[] GlobalFlags = ["--dry-run", "--verbose"];

    /// <summary>Verb-specific options; null value = flag, non-null = takes a value.</summary>
    private static readonly Dictionary<string, Dictionary<string, bool>> VerbOptions = new()
    {
        ["setup"] = new() { ["--mode"] = true, ["--interval"] = true, ["--variant"] = true, ["--quiet"] = false },
        ["uninstall"] = new() { ["--keep-logs"] = false, ["--quiet"] = false },
        ["config"] = new() { ["--enabled"] = true, ["--mode"] = true, ["--interval"] = true, ["--variant"] = true },
        ["webui"] = new()
        {
            ["--url"] = true, ["--api-key"] = true, ["--username"] = true, ["--password"] = true,
            ["--clear"] = false, ["--test"] = false,
        },
    };

    public string? Get(string option) => Options.TryGetValue(option, out string? value) ? value : null;

    public bool Has(string option) => Options.ContainsKey(option);

    /// <summary>The arguments that reproduce this command line, e.g. to run it again elevated.</summary>
    public List<string> ToArguments()
    {
        var arguments = new List<string> { Verb ?? "help" };
        foreach ((string name, string? value) in Options)
        {
            arguments.Add(name);
            if (value is not null)
                arguments.Add(value);
        }
        if (DryRun)
            arguments.Add("--dry-run");
        if (Verbose)
            arguments.Add("--verbose");
        return arguments;
    }

    public CommandLine With(string option, string? value = null)
    {
        var options = new Dictionary<string, string?>(Options, StringComparer.Ordinal) { [option] = value };
        return this with { Options = options };
    }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CommandLine(null, false, false, false, new Dictionary<string, string?>(), null);

        string first = args[0];
        if (first is "-h" or "-?" or "/?" or "--help")
            return new CommandLine("help", false, false, true, new Dictionary<string, string?>(), null);

        string verb = first.ToLowerInvariant();
        if (!Verbs.Contains(verb))
            return Fail(verb, $"Unknown command '{first}'.");

        // The SYSTEM task runs "apply" with a fixed command line; it must not accept anything else.
        if (verb == "apply" && args.Count > 1)
            return Fail(verb, "'apply' takes no arguments.");

        bool dryRun = false, verbose = false, help = false;
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        Dictionary<string, bool> allowed = VerbOptions.GetValueOrDefault(verb) ?? [];

        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            string name = arg, inlineValue = "";
            bool hasInlineValue = false;
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
                hasInlineValue = true;
            }
            name = name.ToLowerInvariant();

            if (name is "-h" or "-?" or "--help")
            {
                help = true;
            }
            else if (GlobalFlags.Contains(name) && !hasInlineValue)
            {
                dryRun |= name == "--dry-run";
                verbose |= name == "--verbose";
            }
            else if (allowed.TryGetValue(name, out bool takesValue))
            {
                if (!takesValue)
                {
                    if (hasInlineValue)
                        return Fail(verb, $"'{name}' does not take a value.");
                    options[name] = null;
                }
                else if (hasInlineValue)
                {
                    options[name] = inlineValue;
                }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options[name] = args[++i];
                }
                else
                {
                    return Fail(verb, $"'{name}' needs a value.");
                }
            }
            else
            {
                return Fail(verb, $"Unknown option '{arg}' for '{verb}'.");
            }
        }

        return new CommandLine(verb, dryRun, verbose, help || verb == "help", options, null);
    }

    public static string Usage => """
        QbtAutoUpdate - keeps qBittorrent up to date on Windows.

        Usage: QbtAutoUpdate.exe <command> [options]

        Commands:
          status      Installed version, variant, latest release, settings, log locations.
          check       Look for an update; then install it (auto mode) or offer it (notify mode).
          update      Download, verify and install the latest release now.
          setup       Install QbtAutoUpdate for this PC (administrator). Without --quiet, opens the setup window.
                        --mode auto|notify  --interval <hours>  --variant auto|lt2|lt12  --quiet
          uninstall   Remove QbtAutoUpdate (administrator).  --keep-logs  --quiet
          config      Change settings (administrator); without options, show them.
                        --enabled on|off  --mode auto|notify  --interval <hours>  --variant auto|lt2|lt12

        Without a command (double-click), opens the setup window, or the settings window once installed.
          rollback    Reinstall the previous qBittorrent release (administrator).
          restart     Close qBittorrent the way an update does and start it again (diagnostic).
          webui       Optional access to qBittorrent's WebUI, for this Windows user.
                        --url http://localhost:8080  --api-key <key>|-  --username <name> --password <pw>|-
                        --test  --clear      ("-" reads the secret from standard input)
          apply       Internal: run by the SYSTEM scheduled task only.

        Options:
          --dry-run   Do everything except closing qBittorrent and installing.
          --verbose   Detailed log output.

        Exit codes: 0 ok, 1 error, 2 usage, 3 unsupported installation,
                    4 release source unavailable, 5 verification failed,
                    6 qBittorrent busy (could not be closed, other session), 7 install failed,
                    9 not available yet.
        """;

    private static CommandLine Fail(string? verb, string error) =>
        new(verb, false, false, false, new Dictionary<string, string?>(), error);
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Unsupported = 3;
    public const int SourceUnavailable = 4;
    public const int VerificationFailed = 5;
    public const int Busy = 6;
    public const int InstallFailed = 7;
    public const int NotAvailableYet = 9;
}
