using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "webui": optional access to qBittorrent's WebUI, per Windows user. Used to shut qBittorrent down
/// through its API and to read its libtorrent version. Secrets are stored with DPAPI; pass "-" to
/// read one from standard input so it stays out of the shell history.
/// </summary>
internal static class WebUiFlow
{
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        CommandLine command = context.Command;
        var warnings = new List<string>();
        UserSettings user = UserSettings.Load(context.Paths.UserConfigFile, warnings);
        foreach (string warning in warnings)
            context.Say("Warning: " + warning);

        if (command.Has("--clear"))
        {
            (user with { WebUi = null }).Save(context.Paths.UserConfigFile);
            context.Say("WebUI settings removed.");
            return ExitCodes.Ok;
        }

        if (command.Get("--url") is { } urlText)
        {
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || !WebUiClient.IsAllowedUrl(url))
            {
                context.Say("--url must be an http(s) address on this computer, e.g. http://localhost:8080");
                return ExitCodes.Usage;
            }
            if (command.Has("--api-key") && command.Has("--username"))
            {
                context.Say("Use either --api-key or --username/--password, not both.");
                return ExitCodes.Usage;
            }

            string? apiKey = ReadSecret(command.Get("--api-key"));
            string? password = ReadSecret(command.Get("--password"));
            if ((command.Get("--api-key") == "-" && apiKey is null) || (command.Get("--password") == "-" && password is null))
            {
                context.Say("'-' reads the secret from standard input, which must be redirected: echo KEY | QbtAutoUpdate.exe webui ...");
                return ExitCodes.Usage;
            }

            user = user with { WebUi = new WebUiSettings(url, apiKey, command.Get("--username"), password) };
            user.Save(context.Paths.UserConfigFile);
            context.Say($"WebUI saved: {url} ({user.WebUi.AuthenticationKind}).");
        }

        if (user.WebUi is null)
        {
            context.Say("No WebUI configured. Example: QbtAutoUpdate.exe webui --url http://localhost:8080 --api-key - --test");
            return ExitCodes.Ok;
        }

        if (!command.Has("--test"))
        {
            context.Say($"WebUI: {user.WebUi.Url} ({user.WebUi.AuthenticationKind}). Add --test to check it.");
            return ExitCodes.Ok;
        }

        using WebUiClient client = context.CreateWebUi(user)!;
        try
        {
            string version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            string? libtorrent = await client.GetLibtorrentVersionAsync(cancellationToken).ConfigureAwait(false);
            Variant? variant = UpdatePlanner.VariantFromLibtorrentVersion(libtorrent);
            context.Say($"WebUI OK: qBittorrent {version}, libtorrent {libtorrent ?? "?"}{(variant is { } v ? $" ({v.Describe()})" : "")}.");
            return ExitCodes.Ok;
        }
        catch (WebUiException ex)
        {
            context.Say("WebUI test failed: " + ex.Message);
            return ExitCodes.Error;
        }
    }

    /// <summary>"-" reads one line from redirected standard input; any other value is used as is.</summary>
    private static string? ReadSecret(string? value)
    {
        if (value != "-")
            return string.IsNullOrEmpty(value) ? null : value;
        if (!Console.IsInputRedirected)
            return null;
        string? line = Console.In.ReadLine()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }
}
