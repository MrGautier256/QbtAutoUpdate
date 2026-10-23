using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Flows;

namespace QbtAutoUpdate.Tests;

public class ToastContentTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly QbtVersion V530 = QbtVersion.Parse("5.3.0");

    [Fact]
    public void Available_offers_update_later_and_skip_through_the_protocol()
    {
        XElement toast = XElement.Parse(ToastContent.Available(V530, QbtVersion.Parse("5.2.4"), Variant.Lt12, Token));

        Assert.Equal(ToastContent.NewsUrl, toast.Attribute("launch")!.Value);
        Assert.Equal(["qBittorrent 5.3.0 is available", "Installed: 5.2.4 (libtorrent 1.2). QbtAutoUpdate closes qBittorrent cleanly, installs the official, verified update and starts it again."],
            toast.Descendants("text").Select(t => t.Value));
        var actions = toast.Descendants("action").ToList();
        Assert.Equal(["Update now", "Later", "Skip this version"], actions.Select(a => a.Attribute("content")!.Value));
        Assert.All(actions, a => Assert.Equal("protocol", a.Attribute("activationType")!.Value));
        Assert.Equal(
            [$"qbtautoupdate:update?v=5.3.0&t={Token}", $"qbtautoupdate:later?v=5.3.0&t={Token}", $"qbtautoupdate:skip?v=5.3.0&t={Token}"],
            actions.Select(a => a.Attribute("arguments")!.Value));
    }

    [Fact]
    public void Close_failure_offers_a_retry()
    {
        XElement toast = XElement.Parse(ToastContent.CloseFailed(V530, "qBittorrent did not close within 120 s.", Token));

        Assert.Equal("Close qBittorrent to finish the update", toast.Descendants("text").First().Value);
        Assert.Equal($"qbtautoupdate:update?v=5.3.0&t={Token}", toast.Descendants("action").Single().Attribute("arguments")!.Value);
    }

    [Fact]
    public void Messages_are_escaped()
    {
        XElement toast = XElement.Parse(ToastContent.Failed("<b>bad</b> & \"quoted\"", Token));

        Assert.Equal("<b>bad</b> & \"quoted\"", toast.Descendants("text").Last().Value);
        Assert.Equal($"qbtautoupdate:logs?t={Token}", toast.Descendants("action").Single().Attribute("arguments")!.Value);
    }

    [Fact]
    public void Success_has_no_button()
    {
        XElement toast = XElement.Parse(ToastContent.Updated(V530, Variant.Lt2));

        Assert.Empty(toast.Descendants("action"));
        Assert.Equal("qBittorrent updated to 5.3.0", toast.Descendants("text").First().Value);
    }
}

public class ToastActionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("qbtautoupdate:update?v=5.3.0&t=" + Token, "update", "5.3.0")]
    [InlineData("qbtautoupdate:later?v=5.3.0&t=" + Token, "later", "5.3.0")]
    [InlineData("QBTAUTOUPDATE:Skip?v=5.3.0&t=" + Token, "skip", "5.3.0")]
    [InlineData("qbtautoupdate:logs?t=" + Token, "logs", null)]
    [InlineData("qbtautoupdate:update/?t=" + Token + "&v=5.3.0", "update", "5.3.0")]
    public void Parses_buttons(string uri, string action, string? version)
    {
        ToastAction? parsed = ToastAction.Parse(uri);

        Assert.NotNull(parsed);
        Assert.Equal(action, parsed.Action);
        Assert.Equal(version is null ? null : QbtVersion.Parse(version), parsed.Version);
        Assert.True(parsed.IsAuthorizedBy(Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example/qbtautoupdate:update?t=x")]
    [InlineData("qbtautoupdate:uninstall?t=" + Token)]
    [InlineData("qbtautoupdate:update?v=5.3.0")]
    [InlineData("qbtautoupdate:update?v=5.3.0&t=")]
    [InlineData("qbtautoupdate:skip?v=not-a-version&t=" + Token)]
    public void Rejects_anything_else(string? uri) => Assert.Null(ToastAction.Parse(uri));

    [Fact]
    public void A_web_page_without_the_users_secret_is_not_authorized()
    {
        ToastAction action = ToastAction.Parse("qbtautoupdate:update?v=5.3.0&t=guessed")!;

        Assert.False(action.IsAuthorizedBy(Token));
        Assert.False(action.IsAuthorizedBy(null));
        Assert.False(ToastAction.Parse("qbtautoupdate:update?t=" + Token)!.IsAuthorizedBy(""));
    }

    [Fact]
    public void Tokens_are_random_and_long()
    {
        string first = ToastAction.NewToken();

        Assert.Equal(32, first.Length);
        Assert.NotEqual(first, ToastAction.NewToken());
    }

    [Fact]
    public void A_notification_click_becomes_the_internal_activate_command()
    {
        CommandLine command = CommandLine.Parse(["qbtautoupdate:later?v=5.3.0&t=" + Token]);

        Assert.Equal("activate", command.Verb);
        Assert.Equal("qbtautoupdate:later?v=5.3.0&t=" + Token, command.Get("--uri"));
        Assert.NotNull(CommandLine.Parse(["qbtautoupdate:later?t=x", "--extra"]).Error);
    }
}

public class LocalizationTests
{
    private static Dictionary<string, string> Read(string culture)
    {
        var manager = new ResourceManager("QbtAutoUpdate.Resources.Strings", typeof(T).Assembly);
        ResourceSet set = manager.GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false)!;
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static readonly Regex Placeholder = new(@"\{(\d+)(:[^}]*)?\}");

    [Fact]
    public void Every_string_is_translated_to_french_with_the_same_placeholders()
    {
        Dictionary<string, string> english = Read("");
        Dictionary<string, string> french = Read("fr");

        Assert.Equal(english.Keys.Order(), french.Keys.Order());
        Assert.All(english, pair =>
        {
            var expected = Placeholder.Matches(pair.Value).Select(m => m.Groups[1].Value).Distinct().Order();
            var actual = Placeholder.Matches(french[pair.Key]).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(expected.SequenceEqual(actual), $"{pair.Key}: placeholders differ");
            Assert.False(string.IsNullOrWhiteSpace(french[pair.Key]), $"{pair.Key}: empty French text");
        });
    }

    [Fact]
    public void Every_key_used_in_the_code_exists()
    {
        string source = Path.Combine(RepositoryRoot(), "src", "QbtAutoUpdate");
        HashSet<string> known = [.. Read("").Keys];
        var used = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\bT\.(?:Get|Format)\(([^,)]*)"))
            .SelectMany(call => Regex.Matches(call.Groups[1].Value, @"""([A-Za-z0-9]+)""(?!\s*\+)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.True(known.Contains(key), $"{key} is missing from Strings.resx"));
        foreach (int code in new[] { 0, 1, 2, 1654, 1223, 1314, 1603, 1062, 15618 })
            Assert.Contains($"Nsis{code}", known);
    }

    [Fact]
    public void French_is_used_on_a_french_windows()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("qBittorrent 5.3.0 est disponible", T.Format("ToastAvailableTitle", "5.3.0"));
            Assert.Equal("Mettre à jour maintenant", T.Get("ToastUpdateNow"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Update now", T.Get("ToastUpdateNow")); // other languages fall back to English
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QbtAutoUpdate.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}

public class RollbackTests
{
    [Fact]
    public void Lists_stable_releases_from_the_real_root_feed()
    {
        List<QbtVersion> releases = SourceForgeClient.ParseReleaseVersions(Fixtures.Text("SourceForge", "rss_root.xml"));

        Assert.Contains(QbtVersion.Parse("5.3.0"), releases);
        Assert.Contains(QbtVersion.Parse("5.2.4"), releases);
        Assert.DoesNotContain(releases, v => v.ToString().Contains("rc", StringComparison.Ordinal));
        Assert.Equal(releases.Order(), releases);
    }

    [Theory]
    [InlineData("5.3.0", "5.2.4")]
    [InlineData("5.2.4", "5.2.3")]
    [InlineData("5.2.0", "5.1.4")]
    public void The_previous_release_is_the_highest_older_one(string installed, string expected)
    {
        List<QbtVersion> releases = SourceForgeClient.ParseReleaseVersions(Fixtures.Text("SourceForge", "rss_root.xml"));

        Assert.Equal(QbtVersion.Parse(expected), RollbackFlow.Previous(releases, QbtVersion.Parse(installed)));
    }

    [Fact]
    public void No_previous_release_when_nothing_is_older()
    {
        Assert.Null(RollbackFlow.Previous([QbtVersion.Parse("5.3.0")], QbtVersion.Parse("5.3.0")));
    }

    [Fact]
    public void The_previous_5_2_release_keeps_libtorrent_1_2_with_its_default_build()
    {
        // Rolling back a 5.3.0 lt12 install lands on the 5.2.4 *default* build: the naming flipped in 5.3.0.
        var listing = new FolderListing(QbtVersion.Parse("5.2.4"), SourceForgeClient.ParseRss(Fixtures.Rss("5.2.4"), QbtVersion.Parse("5.2.4")), ListingSource.Rss, false);

        Assert.Equal("qbittorrent_5.2.4_x64_setup.exe", ReleaseResolver.Resolve(listing, Variant.Lt12, CpuArch.X64).Installer!.Name);
    }

    [Fact]
    public void Config_records_and_clears_the_held_back_version()
    {
        ConfigChanges hold = ConfigChanges.Parse(CommandLine.Parse(["config", "--skipped-version", "5.3.0"]), out _)!;
        ConfigChanges clear = ConfigChanges.Parse(CommandLine.Parse(["config", "--skipped-version", "none"]), out _)!;

        MachineConfig held = hold.ApplyTo(MachineConfig.Default);
        Assert.Equal(QbtVersion.Parse("5.3.0"), held.SkippedVersion);
        Assert.Null(clear.ApplyTo(held).SkippedVersion);
        Assert.Null(ConfigChanges.Parse(CommandLine.Parse(["config", "--skipped-version", "latest"]), out _));
    }
}
