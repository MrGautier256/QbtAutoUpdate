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
        ConsoleHost.AttachToParent();

        if (command.Error is not null)
        {
            Console.Error.WriteLine(command.Error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Usage;
        }

        // Double-click without arguments opens the setup or settings dialog from phase 3 on.
        if (command.Verb is null || command.Help)
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
        context.Log.Info($"QbtAutoUpdate {SafeHttpClient.ProductVersion} '{command.Verb}'{(command.DryRun ? " --dry-run" : "")} as {Environment.UserName}");
        try
        {
            Task<int> run = command.Verb switch
            {
                "status" => StatusFlow.RunAsync(context, cancellation.Token),
                "check" => CheckFlow.RunAsync(context, cancellation.Token),
                "update" => UpdateFlow.RunAsync(context, cancellation.Token),
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
