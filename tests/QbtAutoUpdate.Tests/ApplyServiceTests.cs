using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

public sealed class ApplyServiceTests : IClassFixture<PgpKeys>, IDisposable
{
    private const string Lt12Name = "qbittorrent_5.3.0_lt12_x64_setup.exe";

    private readonly PgpKeys _keys;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly string _desktop;
    private readonly FakeEnvironment _environment = new FakeEnvironment().Installed();
    private readonly FakeInstaller _installer;
    private readonly ListLog _log = new();
    private IReadOnlyList<QbtProcess> _running = [];

    public ApplyServiceTests(PgpKeys keys)
    {
        _keys = keys;
        _paths = new AppPaths(Path.Combine(_root, "pf"), Path.Combine(_root, "pd"), Path.Combine(_root, "lad"));
        _desktop = Path.Combine(_root, "Public Desktop");
        Directory.CreateDirectory(_paths.InboxDir);
        Directory.CreateDirectory(_paths.WorkDir);
        Directory.CreateDirectory(_desktop);
        _installer = new FakeInstaller(_environment);
    }

    private ApplyOutcome Apply() => new ApplyService(_paths, _log, _environment, () => _running, _installer, _keys.Signer.Verifier(),
        CpuArch.X64, () => DateTimeOffset.UtcNow, _desktop).Run();

    /// <summary>Drops a signed installer in the inbox the way the user side does. Returns the request id.</summary>
    private string Drop(string name = Lt12Name, string fileVersion = "5.3.0", TestKey? signer = null, string? requestId = null)
    {
        requestId ??= Inbox.NewRequestId();
        string path = Path.Combine(_paths.InboxDir, Inbox.FileNameFor(requestId, name));
        byte[] content = [.. "MZ"u8, .. Guid.NewGuid().ToByteArray()];
        File.WriteAllBytes(path, content);
        File.WriteAllBytes(path + ".asc", (signer ?? _keys.Signer).Sign(content));
        // The installer is verified on its copy in work\: that is the path the version check sees.
        _environment.MetadataByPath[Path.Combine(_paths.WorkDir, name)] = new ExeMetadata(QbtVersion.Parse(fileVersion), "The qBittorrent project", "qBittorrent", PeMachine.I386);
        return requestId;
    }

    private UpdateState State => UpdateState.Load(_paths.StateFile, []);

    [Fact]
    public void Installs_the_dropped_pair_and_records_it()
    {
        string request = Drop();

        ApplyOutcome outcome = Apply();

        Assert.Equal(InstallStatus.Installed, outcome.Status);
        Assert.Equal(request, outcome.RequestId);
        Assert.Equal("installed", State.LastAttempt!.Result);
        Assert.Equal(request, State.LastAttempt.RequestId);
        Assert.Equal(QbtVersion.Parse("5.3.0"), State.LastInstall!.Version);
        Assert.Equal(Variant.Lt12, State.LastInstall.Variant);
        Assert.Empty(Directory.GetFiles(_paths.InboxDir));
        Assert.Empty(Directory.GetFiles(_paths.WorkDir));
    }

    [Fact]
    public void Nothing_to_do_with_an_empty_inbox()
    {
        ApplyOutcome outcome = Apply();

        Assert.Null(outcome.Status);
        Assert.Empty(_installer.Calls);
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public void Junk_in_the_inbox_is_deleted_unread()
    {
        File.WriteAllText(Path.Combine(_paths.InboxDir, "evil.exe"), "MZ");
        File.WriteAllText(Path.Combine(_paths.InboxDir, Inbox.FileNameFor(Inbox.NewRequestId(), Lt12Name)), "MZ no signature");

        Assert.Null(Apply().Status);
        Assert.Empty(Directory.GetFiles(_paths.InboxDir));
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Only_the_newest_pair_is_installed()
    {
        string older = Drop();
        Thread.Sleep(20);
        string newer = Drop();
        File.SetLastWriteTimeUtc(Path.Combine(_paths.InboxDir, Inbox.FileNameFor(older, Lt12Name)), DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(newer, Apply().RequestId);
        Assert.Single(_installer.Calls);
        Assert.Empty(Directory.GetFiles(_paths.InboxDir));
    }

    [Fact]
    public void A_tampered_installer_is_refused_and_recorded()
    {
        string request = Drop();
        File.AppendAllText(Path.Combine(_paths.InboxDir, Inbox.FileNameFor(request, Lt12Name)), "tampered");

        ApplyOutcome outcome = Apply();

        Assert.Equal(InstallStatus.Refused, outcome.Status);
        Assert.Equal("refused", State.LastAttempt!.Result);
        Assert.Null(State.LastInstall);
        Assert.Empty(_installer.Calls);
        Assert.Empty(Directory.GetFiles(_paths.WorkDir));
    }

    [Fact]
    public void A_foreign_signature_is_refused()
    {
        Drop(signer: _keys.Other);

        Assert.Equal(InstallStatus.Refused, Apply().Status);
    }

    [Fact]
    public void An_older_installer_is_refused()
    {
        Drop("qbittorrent_5.2.3_x64_setup.exe", "5.2.3");

        Assert.Equal(InstallStatus.Refused, Apply().Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void A_running_qbittorrent_anywhere_means_busy()
    {
        _running = [new QbtProcess(1234, 3, null, null)];
        Drop();

        ApplyOutcome outcome = Apply();

        Assert.Equal(InstallStatus.Busy, outcome.Status);
        Assert.Equal("busy", State.LastAttempt!.Result);
        Assert.NotNull(State.BusySince);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void Busy_is_cleared_by_the_next_success()
    {
        new UpdateState { BusySince = DateTimeOffset.UtcNow.AddHours(-1) }.Save(_paths.StateFile);
        Drop();

        Apply();

        Assert.Null(State.BusySince);
    }

    [Fact]
    public void The_public_desktop_shortcut_is_put_back()
    {
        string shortcut = Path.Combine(_desktop, ApplyService.DesktopShortcutName);
        File.WriteAllBytes(shortcut, [1, 2, 3, 4]);
        _installer.OnRun = () => File.Delete(shortcut); // what qBittorrent's uninstaller does
        Drop();

        Apply();

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(shortcut));
    }

    [Fact]
    public void No_shortcut_is_created_when_there_was_none()
    {
        Drop();

        Apply();

        Assert.False(File.Exists(Path.Combine(_desktop, ApplyService.DesktopShortcutName)));
    }

    [Fact]
    public void The_shortcut_is_not_restored_when_the_setting_is_off()
    {
        new MachineConfig { RestoreDesktopShortcut = false }.Save(_paths.ConfigFile);
        string shortcut = Path.Combine(_desktop, ApplyService.DesktopShortcutName);
        File.WriteAllBytes(shortcut, [1]);
        _installer.OnRun = () => File.Delete(shortcut);
        Drop();

        Apply();

        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void The_variant_comes_from_the_installed_binary_and_a_mismatch_is_refused()
    {
        _environment.ScanResult = FakeEnvironment.Lt2;
        Drop(); // an lt12 installer for an lt2 installation

        Assert.Equal(InstallStatus.Refused, Apply().Status);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public void An_unknown_variant_is_refused()
    {
        _environment.ScanResult = BinaryScanResult.Empty;
        Drop();

        Assert.Equal(InstallStatus.Refused, Apply().Status);
        Assert.Contains("unknown", State.LastAttempt!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interrupted_install_is_done_again()
    {
        // The PC stopped after the old uninstaller ran: no registry keys, no qbittorrent.exe left.
        var bare = new FakeEnvironment();
        var installer = new FakeInstaller(bare)
        {
            OnRun = () => bare.Installed(),
        };
        new UpdateState
        {
            LastAttempt = new ApplyAttempt(DateTimeOffset.UtcNow.AddHours(-2), ApplyAttempt.Installing, "Installing", "x",
                QbtVersion.Parse("5.3.0"), Variant.Lt12, FakeEnvironment.Dir),
        }.Save(_paths.StateFile);
        string request = Drop();
        bare.MetadataByPath[Path.Combine(_paths.WorkDir, Lt12Name)] = _environment.MetadataByPath[Path.Combine(_paths.WorkDir, Lt12Name)];

        ApplyOutcome outcome = new ApplyService(_paths, _log, bare, () => [], installer, _keys.Signer.Verifier(),
            CpuArch.X64, () => DateTimeOffset.UtcNow, _desktop).Run();

        Assert.Equal(InstallStatus.Installed, outcome.Status);
        Assert.Equal([@"/S /D=C:\Program Files\qBittorrent"], installer.Calls);
        Assert.Equal(request, State.LastAttempt!.RequestId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
