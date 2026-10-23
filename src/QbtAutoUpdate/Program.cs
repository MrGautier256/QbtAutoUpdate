using QbtAutoUpdate.Core;
using QbtAutoUpdate.Flows;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLine command = CommandLine.Parse(args);
        bool hasConsole = ConsoleHost.AttachToParent();

        if (command.Error is not null)
        {
            Console.Error.WriteLine(command.Error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Usage;
        }

        if (command.Help || (command.Verb is null && hasConsole))
        {
            Console.WriteLine(CommandLine.Usage);
            return ExitCodes.Ok;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using FlowContext context = FlowContext.Create(command);
        // Nobody watches the scheduled check or a notification click: they report through notifications.
        // A click always does, even when the shell that opened the link happened to have a console.
        context.ToastsEnabled = command.Verb == "activate" || (!hasConsole && command.Verb is "check" or "update");
        context.Log.Info($"QbtAutoUpdate {SafeHttpClient.ProductVersion} '{command.Verb ?? "(window)"}'{(command.DryRun ? " --dry-run" : "")} as {Environment.UserName}"
                         + (Elevation.IsSystem ? " (SYSTEM)" : Elevation.IsElevated ? " (elevated)" : ""));
        try
        {
            Task<int> run = command.Verb switch
            {
                // Double-click (no console): setup window, or settings window once installed.
                null => Task.FromResult(UiFlow.Run(context)),
                "status" => StatusFlow.RunAsync(context, cancellation.Token),
                "check" => CheckFlow.RunAsync(context, cancellation.Token),
                "update" => UpdateFlow.RunAsync(context, cancellation.Token),
                "restart" => RestartFlow.RunAsync(context, cancellation.Token),
                "webui" => WebUiFlow.RunAsync(context, cancellation.Token),
                "setup" => Task.FromResult(SetupFlow.Run(context)),
                "config" => Task.FromResult(ConfigFlow.Run(context)),
                "uninstall" => Task.FromResult(UninstallFlow.Run(context)),
                "apply" => Task.FromResult(ApplyFlow.Run(context)),
                "rollback" => RollbackFlow.RunAsync(context, cancellation.Token),
                "activate" => ActivationFlow.RunAsync(context, cancellation.Token),
                _ => Task.FromResult(NotAvailableYet(context, command.Verb)),
            };
            int exitCode = run.GetAwaiter().GetResult();
            context.Log.Info($"Exit code {exitCode}.");
            return exitCode;
        }
        catch (VersionSourceException ex)
        {
            context.Log.Error(ex.Message);
            Console.Error.WriteLine("Could not reach qbittorrent.org nor its GitHub mirror. Details: " + context.LogFile);
            return ExitCodes.SourceUnavailable;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.Log.Warn("Cancelled by the user.");
            return ExitCodes.Error;
        }
        catch (Exception ex)
        {
            context.Log.Error(ex.ToString());
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Details: " + context.LogFile);
            return ExitCodes.Error;
        }
    }

    private static int NotAvailableYet(FlowContext context, string verb)
    {
        context.Say($"'{verb}' is not available in this build yet.");
        return ExitCodes.NotAvailableYet;
    }
}
