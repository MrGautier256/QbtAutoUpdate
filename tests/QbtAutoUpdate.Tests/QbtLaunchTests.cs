using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

public class QbtLaunchTests
{
    [Fact]
    public void A_double_clicked_torrent_is_not_replayed()
    {
        // The real command line seen on a Windows 11 machine running 5.2.4.
        string[] argv = ProcessTools.SplitCommandLine(@"""C:\Program Files\qBittorrent\qbittorrent.exe"" ""C:\Users\someone\Downloads\Some.Linux.ISO.torrent""");

        QbtLaunch launch = QbtLaunch.FromArguments(argv.Skip(1));

        Assert.Empty(launch.Arguments);
        Assert.Equal(1, launch.DroppedCount);
    }

    [Fact]
    public void The_run_key_form_written_by_qbittorrent_is_kept()
    {
        // HKCU\...\Run\qBittorrent as written by qBittorrent's own "start with Windows" option.
        string[] argv = ProcessTools.SplitCommandLine(@"""C:\Program Files\qBittorrent\qbittorrent.exe"" ""--profile="" ""--configuration=""");

        QbtLaunch launch = QbtLaunch.FromArguments(argv.Skip(1));

        Assert.Equal(["--profile=", "--configuration="], launch.Arguments);
        Assert.Null(launch.ProfileDir);
        Assert.Null(launch.ConfigurationName);
        Assert.Equal(0, launch.DroppedCount);
    }

    [Fact]
    public void Instance_options_are_kept_and_torrent_options_dropped()
    {
        QbtLaunch launch = QbtLaunch.FromArguments(
        [
            "--profile=D:\\qbt profile", "--configuration=work", "--webui-port=8081", "--torrenting-port=6881",
            "--relative-fastresume", "--no-splash", "--confirm-legal-notice",
            "--save-path=D:\\Downloads", "--add-stopped=true", "--skip-dialog=true", "--category=iso", "--sequential",
            "--first-and-last", "--seed-mode",
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", "https://example.org/a.torrent", "--weird.torrent",
            "--unknown", "-h",
        ]);

        Assert.Equal(
            ["--profile=D:\\qbt profile", "--configuration=work", "--webui-port=8081", "--torrenting-port=6881",
             "--relative-fastresume", "--no-splash", "--confirm-legal-notice"],
            launch.Arguments);
        Assert.Equal(@"D:\qbt profile", launch.ProfileDir);
        Assert.Equal("work", launch.ConfigurationName);
        Assert.Equal(12, launch.DroppedCount);
    }

    [Theory]
    [InlineData("--profile=\"D:\\p\"", @"D:\p")]
    [InlineData("--profile='D:\\p'", @"D:\p")]
    [InlineData("--profile=\"D:\\p'", "\"D:\\p'")]
    public void Values_are_unquoted_like_qbittorrent_does(string argument, string expected) =>
        Assert.Equal(expected, QbtLaunch.FromArguments([argument]).ProfileDir);

    [Fact]
    public void Options_without_value_syntax_are_dropped()
    {
        // qBittorrent rejects "--profile D:\p": only "--profile=D:\p" is valid.
        QbtLaunch launch = QbtLaunch.FromArguments(["--profile", @"D:\p"]);

        Assert.Empty(launch.Arguments);
        Assert.Null(launch.ProfileDir);
    }

    private static QbtProfile? Resolve(QbtLaunch launch, params string[] existingDirs) =>
        launch.ResolveProfile(@"C:\Program Files\qBittorrent", @"C:\Users\u\AppData\Roaming", @"C:\Users\u\AppData\Local",
            d => existingDirs.Contains(d, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Default_profile()
    {
        Assert.Equal(new QbtProfile(@"C:\Users\u\AppData\Roaming\qBittorrent", @"C:\Users\u\AppData\Local\qBittorrent"), Resolve(QbtLaunch.Default));
    }

    [Fact]
    public void Configuration_name_adds_a_suffix()
    {
        QbtLaunch launch = QbtLaunch.FromArguments(["--configuration=work"]);

        Assert.Equal(new QbtProfile(@"C:\Users\u\AppData\Roaming\qBittorrent_work", @"C:\Users\u\AppData\Local\qBittorrent_work"), Resolve(launch));
    }

    [Fact]
    public void Custom_profile_root()
    {
        QbtLaunch launch = QbtLaunch.FromArguments([@"--profile=D:\qbt", "--configuration=work"]);

        Assert.Equal(new QbtProfile(@"D:\qbt\qBittorrent_work\config", @"D:\qbt\qBittorrent_work\data"), Resolve(launch));
    }

    [Fact]
    public void Portable_profile_next_to_the_exe()
    {
        QbtProfile? profile = Resolve(QbtLaunch.Default, @"C:\Program Files\qBittorrent\profile");

        Assert.Equal(new QbtProfile(@"C:\Program Files\qBittorrent\profile\qBittorrent\config", @"C:\Program Files\qBittorrent\profile\qBittorrent\data"), profile);
    }

    [Fact]
    public void A_relative_profile_cannot_be_located()
    {
        Assert.Null(Resolve(QbtLaunch.FromArguments(["--profile=..\\qbt"])));
    }
}

public class CommandLineQuotingTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData(@"C:\Program Files\qBittorrent\qbittorrent.exe")]
    [InlineData(@"--profile=C:\path with space\")]
    [InlineData(@"trailing\\")]
    [InlineData("quote\"inside")]
    [InlineData(@"backslash\""quote")]
    [InlineData("")]
    [InlineData("tab\there")]
    public void Round_trips_through_CommandLineToArgvW(string argument)
    {
        string[] split = ProcessTools.SplitCommandLine(CommandLineQuoting.Join(["app.exe", argument]));

        Assert.Equal(["app.exe", argument], split);
    }

    [Fact]
    public void Reads_the_command_line_of_a_running_process()
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, Environment.ProcessId);

        string? commandLine = ProcessTools.GetCommandLine(handle);

        // .NET rebuilds Environment.CommandLine, so compare the program path rather than the raw text.
        Assert.NotNull(commandLine);
        Assert.Equal(Environment.ProcessPath, Path.GetFullPath(ProcessTools.SplitCommandLine(commandLine)[0]), ignoreCase: true);
    }
}
