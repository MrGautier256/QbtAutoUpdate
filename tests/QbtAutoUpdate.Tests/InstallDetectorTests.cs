using Microsoft.Win32;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

public class InstallDetectorTests
{
    private const string Dir = @"C:\Program Files\qBittorrent";
    private const string Exe = Dir + @"\qbittorrent.exe";

    private static readonly BinaryScanResult Lt12 = FakeEnvironment.Lt12;

    private static InstallInfo Detect(FakeEnvironment environment) => new InstallDetector(environment).Detect();

    [Fact]
    public void Nothing_installed()
    {
        Assert.Equal(InstallKind.NotInstalled, Detect(new FakeEnvironment()).Kind);
    }

    [Fact]
    public void Official_install_in_the_32_bit_view()
    {
        InstallInfo info = Detect(new FakeEnvironment().Installed());

        Assert.Equal(InstallKind.Installed, info.Kind);
        Assert.True(info.IsSupported);
        Assert.Equal(Dir, info.InstallDir);
        Assert.Equal(RegistryView.Registry32, info.RegistryView);
        Assert.Equal(QbtVersion.Parse("5.2.4"), info.Version);
        Assert.Equal(Variant.Lt12, info.DetectedVariant);
        Assert.False(info.IsInconsistent);
    }

    [Fact]
    public void Also_reads_the_64_bit_view()
    {
        InstallInfo info = Detect(new FakeEnvironment().Installed(RegistryView.Registry64));

        Assert.Equal(InstallKind.Installed, info.Kind);
        Assert.Equal(RegistryView.Registry64, info.RegistryView);
    }

    [Fact]
    public void The_file_version_wins_over_the_registry()
    {
        var environment = new FakeEnvironment().Installed(displayVersion: "5.2.3");

        InstallInfo info = Detect(environment);

        Assert.Equal(QbtVersion.Parse("5.2.4"), info.Version);
        Assert.True(info.IsInconsistent);
    }

    [Fact]
    public void A_missing_exe_is_an_inconsistent_but_supported_install()
    {
        var environment = new FakeEnvironment().Installed();
        environment.Files.Clear();

        InstallInfo info = Detect(environment);

        Assert.Equal(InstallKind.Installed, info.Kind);
        Assert.True(info.ExeMissing);
        Assert.True(info.IsInconsistent);
        Assert.Equal(QbtVersion.Parse("5.2.4"), info.Version);
        Assert.Null(info.DetectedVariant);
    }

    [Fact]
    public void Uses_the_uninstall_string_when_install_location_is_missing()
    {
        var environment = new FakeEnvironment().Installed();
        environment.Registry.Remove((RegistryView.Registry32, InstallDetector.AppKey, "InstallLocation"));

        Assert.Equal(Dir, Detect(environment).InstallDir);
    }

    [Fact]
    public void The_enhanced_edition_is_recognized_by_its_version()
    {
        var environment = new FakeEnvironment().Installed(displayVersion: "5.2.4.10");
        environment.Metadata = environment.Metadata with { FileVersion = QbtVersion.Parse("5.2.4.10") };

        Assert.Equal(InstallKind.Fork, Detect(environment).Kind);
    }

    [Fact]
    public void The_enhanced_edition_is_recognized_by_its_strings()
    {
        var environment = new FakeEnvironment().Installed();
        environment.ScanResult = Lt12 with { HasForkMarkers = true };

        Assert.Equal(InstallKind.Fork, Detect(environment).Kind);
    }

    [Fact]
    public void Upstream_four_part_versions_are_not_a_fork()
    {
        var environment = new FakeEnvironment().Installed(displayVersion: "4.3.0.1");
        environment.Metadata = environment.Metadata with { FileVersion = QbtVersion.Parse("4.3.0.1") };

        Assert.Equal(InstallKind.Installed, Detect(environment).Kind);
    }

    [Theory]
    [InlineData("Someone Else", "qBittorrent")]
    [InlineData("The qBittorrent Project", "qBittorrent Enhanced")]
    [InlineData(null, null)]
    public void Other_products_are_not_official(string? company, string? product)
    {
        var environment = new FakeEnvironment().Installed();
        environment.Metadata = environment.Metadata with { CompanyName = company, ProductName = product };

        Assert.Equal(InstallKind.NotOfficial, Detect(environment).Kind);
    }

    [Fact]
    public void Company_name_case_does_not_matter()
    {
        var environment = new FakeEnvironment().Installed();
        environment.Metadata = environment.Metadata with { CompanyName = "The qBittorrent project" };

        Assert.Equal(InstallKind.Installed, Detect(environment).Kind);
    }

    [Fact]
    public void Old_32_bit_installs_are_not_handled()
    {
        Assert.Equal(InstallKind.Legacy32Bit, Detect(new FakeEnvironment().Installed(dir: @"C:\Program Files (x86)\qBittorrent")).Kind);

        var environment = new FakeEnvironment().Installed();
        environment.Metadata = environment.Metadata with { Machine = PeMachine.I386 };
        Assert.Equal(InstallKind.Legacy32Bit, Detect(environment).Kind);
    }

    [Fact]
    public void A_running_copy_without_registry_keys_is_portable()
    {
        var environment = new FakeEnvironment();
        environment.Running.Add(@"D:\Apps\qBittorrent\qbittorrent.exe");
        environment.Directories.Add(@"D:\Apps\qBittorrent\profile");

        InstallInfo info = Detect(environment);

        Assert.Equal(InstallKind.Portable, info.Kind);
        Assert.True(info.HasPortableProfile);
        Assert.False(info.IsSupported);
    }

    [Fact]
    public void An_unregistered_32_bit_copy_is_reported()
    {
        var environment = new FakeEnvironment();
        environment.Files.Add(@"C:\Program Files (x86)\qBittorrent\qbittorrent.exe");

        Assert.Equal(InstallKind.Legacy32Bit, Detect(environment).Kind);
    }

    [Fact]
    public void A_profile_folder_in_a_registered_install_is_noted_but_supported()
    {
        var environment = new FakeEnvironment().Installed();
        environment.Directories.Add(Dir + @"\profile");

        InstallInfo info = Detect(environment);

        Assert.True(info.IsSupported);
        Assert.True(info.HasPortableProfile);
    }
}
