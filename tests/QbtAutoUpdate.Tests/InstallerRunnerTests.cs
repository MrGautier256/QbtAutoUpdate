using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

public sealed class InstallerRunnerTests : IClassFixture<PgpKeys>, IDisposable
{
    private const string Lt12Name = "qbittorrent_5.3.0_lt12_x64_setup.exe";
    private const string Lt2Name = "qbittorrent_5.3.0_x64_setup.exe";

    private readonly PgpKeys _keys;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeEnvironment _environment = new FakeEnvironment().Installed();
    private readonly FakeInstaller _installer;

    public InstallerRunnerTests(PgpKeys keys)
    {
        _keys = keys;
        Directory.CreateDirectory(_dir);
        _installer = new FakeInstaller(_environment);
    }

    private InstallerFile Signed(string name, string fileVersion = "5.3.0", TestKey? signer = null)
    {
        string path = Path.Combine(_dir, name);
        byte[] content = [.. "MZ"u8, .. Guid.NewGuid().ToByteArray()];
        File.WriteAllBytes(path, content);
        File.WriteAllBytes(path + ".asc", (signer ?? _keys.Signer).Sign(content));
        _environment.MetadataByPath[path] = new ExeMetadata(QbtVersion.Parse(fileVersion), "The qBittorrent project", "qBittorrent", PeMachine.I386);
        return new InstallerFile(path, path + ".asc", name);
    }

    private InstallOutcome Install(InstallerFile file, Variant expected = Variant.Lt12, IReadOnlyList<QbtProcess>? running = null, CpuArch machine = CpuArch.X64, bool allowDowngrade = false)
    {
        InstallInfo before = new InstallDetector(_environment).Detect();
        var runner = new InstallerRunner(_keys.Signer.Verifier(), _environment, NullLog.Instance, () => running ?? [], _installer, machine, () => DateTimeOffset.UtcNow);
        return runner.Install(file, before, expected, allowDowngrade);
    }

    [Fact]
    public void Installs_silently_into_the_current_folder_and_verifies_the_result()
    {
        InstallOutcome outcome = Install(Signed(Lt12Name));

        Assert.Equal(InstallStatus.Installed, outcome.Status);
        Assert.Equal([@"/S /D=C:\Program Files\qBittorrent"], _installer.Calls);
        Assert.Equal(QbtVersion.Parse("5.3.0"), outcome.After!.Version);
    }

    [Fact]
    public void Refuses_a_bad_signature()
    {
        InstallerFile file = Signed(Lt12Name);
        File.AppendAllText(file.Path, "tampered");

        Assert.Equal(InstallStatus.Refused, Install(file).Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Refuses_another_signer()
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed(Lt12Name, signer: _keys.Other)).Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Refuses_a_file_whose_version_resource_contradicts_its_name()
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed(Lt12Name, fileVersion: "5.2.4")).Status);
    }

    [Theory]
    [InlineData("qbittorrent_5.2.3_x64_setup.exe", "5.2.3")] // older
    [InlineData("qbittorrent_5.2.4_x64_setup.exe", "5.2.4")] // same, installation consistent
    public void Never_downgrades_nor_reinstalls_a_consistent_install(string name, string version)
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed(name, version)).Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Rollback_installs_an_older_version_but_still_checks_everything_else()
    {
        _installer.InstalledVersion = "5.2.3";

        Assert.Equal(InstallStatus.Refused, Install(Signed("qbittorrent_5.2.3_x64_setup.exe", "5.2.3", signer: _keys.Other), allowDowngrade: true).Status);
        Assert.Equal(InstallStatus.Refused, Install(Signed("qbittorrent_5.2.4_x64_setup.exe", "5.2.4"), allowDowngrade: true).Status);
        Assert.Empty(_installer.Calls);

        InstallOutcome outcome = Install(Signed("qbittorrent_5.2.3_x64_setup.exe", "5.2.3"), allowDowngrade: true);

        Assert.Equal(InstallStatus.Installed, outcome.Status);
        Assert.Equal(QbtVersion.Parse("5.2.3"), outcome.After!.Version);
    }

    [Fact]
    public void Reinstalls_the_same_version_over_an_inconsistent_install()
    {
        _environment.Files.Clear(); // qbittorrent.exe missing
        _installer.InstalledVersion = "5.2.4";
        InstallerFile file = Signed("qbittorrent_5.2.4_x64_setup.exe", "5.2.4");
        _environment.Files.Add(FakeEnvironment.Dir + @"\qbittorrent.exe"); // the installer puts it back
        InstallInfo before = new InstallDetector(new FakeEnvironment().Installed()) .Detect() with { ExeMissing = true, FileVersion = null };

        InstallOutcome outcome = new InstallerRunner(_keys.Signer.Verifier(), _environment, NullLog.Instance, () => [], _installer, CpuArch.X64, () => DateTimeOffset.UtcNow)
            .Install(file, before, Variant.Lt12);

        Assert.Equal(InstallStatus.Installed, outcome.Status);
    }

    [Fact]
    public void Refuses_to_switch_variant()
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed(Lt2Name), expected: Variant.Lt12).Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Refuses_unknown_names()
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed("setup.exe")).Status);
        Assert.Equal(InstallStatus.Refused, Install(Signed("qbittorrent_5.3.0_lt20_x64_setup.exe")).Status);
    }

    [Fact]
    public void Refuses_an_arm64_installer_on_x64()
    {
        Assert.Equal(InstallStatus.Refused, Install(Signed("qbittorrent_5.3.0_lt12_arm64_setup.exe")).Status);
    }

    [Fact]
    public void Waits_while_qbittorrent_runs_anywhere()
    {
        InstallOutcome outcome = Install(Signed(Lt12Name), running: [new QbtProcess(99, 2, null, null)]);

        Assert.Equal(InstallStatus.Busy, outcome.Status);
        Assert.Empty(_installer.Calls);
    }

    [Theory]
    [InlineData(1654, "refused this system")]
    [InlineData(15618, "qBittorrent was running")]
    [InlineData(1223, "prompt was cancelled")]
    [InlineData(4242, "unknown exit code 4242")]
    public void Reports_installer_failures(int exitCode, string expected)
    {
        _installer.ExitCode = exitCode;

        InstallOutcome outcome = Install(Signed(Lt12Name));

        Assert.Equal(InstallStatus.InstallerFailed, outcome.Status);
        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.Contains(expected, outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installer_that_never_finishes_is_reported()
    {
        _installer.ExitCode = null;

        Assert.Equal(InstallStatus.InstallerFailed, Install(Signed(Lt12Name)).Status);
    }

    [Fact]
    public void A_success_that_left_the_old_version_is_caught()
    {
        _installer.InstalledVersion = "5.2.4";

        Assert.Equal(InstallStatus.NotVerified, Install(Signed(Lt12Name)).Status);
    }

    [Fact]
    public void A_renamed_installer_of_the_other_variant_is_caught_afterwards()
    {
        // The residual risk of FINDINGS §6.3: a genuine default build renamed "lt12".
        _installer.InstalledScan = FakeEnvironment.Lt2;

        InstallOutcome outcome = Install(Signed(Lt12Name));

        Assert.Equal(InstallStatus.NotVerified, outcome.Status);
        Assert.Contains("libtorrent 2.x", outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qbittorrent_5.3.0_lt12_x64_setup.exe", "5.3.0", Variant.Lt12, CpuArch.X64)]
    [InlineData("qbittorrent_5.2.4_lt20_x64_setup.exe", "5.2.4", Variant.Lt2, CpuArch.X64)]
    [InlineData("QBITTORRENT_5.3.0_X64_SETUP.EXE", "5.3.0", Variant.Lt2, CpuArch.X64)]
    public void Parses_installer_names(string name, string version, Variant variant, CpuArch arch) =>
        Assert.Equal((QbtVersion.Parse(version), variant, arch), InstallerRunner.ParseInstallerName(name));

    [Theory]
    [InlineData("qbittorrent_5.3.0_x64_setup.exe.asc")]
    [InlineData("..\\qbittorrent_5.3.0_x64_setup.exe")]
    [InlineData("qbittorrent_5.3.0rc1_x64_setup.exe")]
    [InlineData("evil.exe")]
    public void Rejects_other_names(string name) => Assert.Null(InstallerRunner.ParseInstallerName(name));

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
