namespace QbtAutoUpdate.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_means_no_verb()
    {
        CommandLine command = CommandLine.Parse([]);

        Assert.Null(command.Verb);
        Assert.Null(command.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("help")]
    public void Help(string arg) => Assert.True(CommandLine.Parse([arg]).Help);

    [Fact]
    public void Global_flags()
    {
        CommandLine command = CommandLine.Parse(["check", "--dry-run", "--verbose"]);

        Assert.Equal("check", command.Verb);
        Assert.True(command.DryRun);
        Assert.True(command.Verbose);
    }

    [Fact]
    public void Setup_options_with_both_syntaxes()
    {
        CommandLine command = CommandLine.Parse(["SETUP", "--mode", "auto", "--interval=6", "--variant", "lt12", "--quiet"]);

        Assert.Null(command.Error);
        Assert.Equal("setup", command.Verb);
        Assert.Equal("auto", command.Get("--mode"));
        Assert.Equal("6", command.Get("--interval"));
        Assert.Equal("lt12", command.Get("--variant"));
        Assert.True(command.Has("--quiet"));
    }

    [Fact]
    public void Webui_options_accept_a_dash_for_standard_input()
    {
        CommandLine command = CommandLine.Parse(["webui", "--url", "http://localhost:8080", "--api-key", "-", "--test"]);

        Assert.Null(command.Error);
        Assert.Equal("-", command.Get("--api-key"));
        Assert.True(command.Has("--test"));
    }

    [Fact]
    public void Restart_is_a_verb()
    {
        CommandLine command = CommandLine.Parse(["restart", "--dry-run"]);

        Assert.Equal("restart", command.Verb);
        Assert.True(command.DryRun);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("restart", "--url", "x")]
    [InlineData("check", "--mode", "auto")]
    [InlineData("setup", "--mode")]
    [InlineData("setup", "--quiet=yes")]
    [InlineData("status", "extra")]
    [InlineData("check", "--dry-run=true")]
    public void Errors(params string[] args) => Assert.NotNull(CommandLine.Parse(args).Error);

    [Theory]
    [InlineData("apply", "--dry-run")]
    [InlineData("apply", "C:\\evil.exe")]
    [InlineData("apply", "--verbose")]
    public void Apply_accepts_no_argument_at_all(params string[] args) => Assert.NotNull(CommandLine.Parse(args).Error);

    [Fact]
    public void Apply_alone_is_fine()
    {
        CommandLine command = CommandLine.Parse(["apply"]);

        Assert.Equal("apply", command.Verb);
        Assert.Null(command.Error);
    }
}
