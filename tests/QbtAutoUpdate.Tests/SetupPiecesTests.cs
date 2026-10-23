using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Flows;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

public class TaskDefinitionsTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\QbtAutoUpdate\QbtAutoUpdate.exe";

    [Fact]
    public void Check_runs_for_every_user_with_least_privilege()
    {
        XDocument xml = XDocument.Parse(TaskDefinitions.Check(Exe, 6, enabled: true, new DateTime(2026, 10, 23, 14, 30, 0)));
        XElement principal = xml.Descendants(Ns + "Principal").Single();

        Assert.Equal("S-1-5-32-545", principal.Element(Ns + "GroupId")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal("PT3M", xml.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "Delay")!.Value);
        Assert.Equal("PT6H", xml.Descendants(Ns + "Interval").Single().Value);
        Assert.Equal("2026-10-23T14:30:00", xml.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.Equal("true", xml.Descendants(Ns + "StartWhenAvailable").Single().Value);
        Assert.Equal("true", xml.Descendants(Ns + "RunOnlyIfNetworkAvailable").Single().Value);
        Assert.Equal("IgnoreNew", xml.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT1H", xml.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", xml.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal($"\"{Exe}\"", xml.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("check", xml.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void Check_can_be_registered_disabled()
    {
        XDocument xml = XDocument.Parse(TaskDefinitions.Check(Exe, 12, enabled: false, DateTime.Now));

        Assert.Equal("false", xml.Descendants(Ns + "Settings").Single().Element(Ns + "Enabled")!.Value);
        Assert.Equal("PT12H", xml.Descendants(Ns + "Interval").Single().Value);
    }

    [Fact]
    public void Apply_is_system_on_demand_with_a_fixed_command_line()
    {
        XDocument xml = XDocument.Parse(TaskDefinitions.Apply(Exe));
        XElement principal = xml.Descendants(Ns + "Principal").Single();

        Assert.Equal("S-1-5-18", principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Empty(xml.Descendants(Ns + "Triggers").Single().Elements());
        Assert.Equal("apply", xml.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal("true", xml.Descendants(Ns + "AllowStartOnDemand").Single().Value);
    }

    [Fact]
    public void Apply_can_be_started_by_users_but_only_administrators_change_it()
    {
        var descriptor = new RawSecurityDescriptor(TaskDefinitions.ApplySddl);
        var aces = descriptor.DiscretionaryAcl!.OfType<CommonAce>().ToList();

        CommonAce users = Assert.Single(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinUsersSid));
        Assert.Equal(AceQualifier.AccessAllowed, users.AceQualifier);
        const int FileGenericWrite = 0x120116;
        Assert.Equal(0, users.AccessMask & (FileGenericWrite & ~0x120000));
        Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(aces, a => a.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }
}

public class InboxTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Name = "qbittorrent_5.3.0_lt12_x64_setup.exe";

    private static InboxEntry E(string name, long length = 1000, int minutesAgo = 0, bool link = false) =>
        new(@"C:\inbox\" + name, name, length, DateTime.UtcNow.AddMinutes(-minutesAgo), link);

    [Fact]
    public void Selects_a_valid_pair()
    {
        (InboxPair? pair, List<InboxEntry> discard) = Inbox.Select([E($"{Id}__{Name}"), E($"{Id}__{Name}.asc", 887)]);

        Assert.NotNull(pair);
        Assert.Equal(Id, pair.RequestId);
        Assert.Equal(Name, pair.InstallerName);
        Assert.Empty(discard);
    }

    [Fact]
    public void Takes_the_newest_pair_and_discards_the_rest()
    {
        const string Other = "fedcba9876543210fedcba9876543210";
        InboxEntry[] entries =
        [
            E($"{Id}__{Name}", minutesAgo: 10), E($"{Id}__{Name}.asc", 887, 10),
            E($"{Other}__{Name}", minutesAgo: 1), E($"{Other}__{Name}.asc", 887, 1),
            E("readme.txt"),
        ];

        (InboxPair? pair, List<InboxEntry> discard) = Inbox.Select(entries);

        Assert.Equal(Other, pair!.RequestId);
        Assert.Equal(3, discard.Count);
    }

    [Theory]
    [InlineData($"{Id}__{Name}", false)]                              // no signature
    [InlineData($"{Id}__setup.exe", true)]                            // unknown installer name
    [InlineData($"{Id}__qbittorrent_5.3.0rc1_x64_setup.exe", true)]    // pre-release
    [InlineData($"ABCDEF__{Name}", true)]                              // not a request id
    [InlineData(Name, true)]                                           // no request id at all
    public void Rejects_incomplete_or_unknown_requests(string installer, bool withSignature)
    {
        List<InboxEntry> entries = [E(installer)];
        if (withSignature)
            entries.Add(E(installer + ".asc", 887));

        (InboxPair? pair, List<InboxEntry> discard) = Inbox.Select(entries);

        Assert.Null(pair);
        Assert.Equal(entries.Count, discard.Count);
    }

    [Fact]
    public void Rejects_links_and_oversized_files()
    {
        Assert.Null(Inbox.Select([E($"{Id}__{Name}", link: true), E($"{Id}__{Name}.asc", 887)]).Pair);
        Assert.Null(Inbox.Select([E($"{Id}__{Name}"), E($"{Id}__{Name}.asc", 887, link: true)]).Pair);
        Assert.Null(Inbox.Select([E($"{Id}__{Name}", Inbox.MaxInstallerBytes + 1), E($"{Id}__{Name}.asc", 887)]).Pair);
        Assert.Null(Inbox.Select([E($"{Id}__{Name}"), E($"{Id}__{Name}.asc", Inbox.MaxSignatureBytes + 1)]).Pair);
        Assert.Null(Inbox.Select([E($"{Id}__{Name}", 0), E($"{Id}__{Name}.asc", 887)]).Pair);
    }
}

public class ConfigChangesTests
{
    [Fact]
    public void Parses_every_option()
    {
        CommandLine command = CommandLine.Parse(["config", "--enabled", "off", "--mode", "auto", "--interval", "12", "--variant", "lt12"]);

        ConfigChanges? changes = ConfigChanges.Parse(command, out string? error);

        Assert.Null(error);
        Assert.Equal(new ConfigChanges(false, UpdateMode.Auto, 12, VariantSetting.Lt12), changes);
        Assert.Equal(new MachineConfig { Enabled = false, Mode = UpdateMode.Auto, CheckIntervalHours = 12, Variant = VariantSetting.Lt12 },
            changes!.ApplyTo(MachineConfig.Default));
    }

    [Theory]
    [InlineData("--enabled", "maybe")]
    [InlineData("--mode", "sometimes")]
    [InlineData("--interval", "0")]
    [InlineData("--interval", "200")]
    [InlineData("--interval", "six")]
    [InlineData("--variant", "lt20")]
    public void Rejects_bad_values(string option, string value)
    {
        Assert.Null(ConfigChanges.Parse(CommandLine.Parse(["config", option, value]), out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Missing_options_keep_the_current_values()
    {
        var current = new MachineConfig { Mode = UpdateMode.Auto, CheckIntervalHours = 3 };

        MachineConfig result = ConfigChanges.Parse(CommandLine.Parse(["config", "--enabled", "off"]), out _)!.ApplyTo(current);

        Assert.Equal(current with { Enabled = false }, result);
    }

    [Fact]
    public void Arguments_round_trip_for_the_elevated_relaunch()
    {
        CommandLine command = CommandLine.Parse(["setup", "--mode", "auto", "--quiet", "--variant=lt12"]);

        Assert.Equal(command.Options, CommandLine.Parse(command.ToArguments()).Options);
    }
}

public sealed class StateRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Attempts_round_trip()
    {
        string path = Path.Combine(_dir, "state.json");
        var state = new UpdateState
        {
            LastAttempt = new ApplyAttempt(DateTimeOffset.UnixEpoch, "busy", "qBittorrent is running", "abc", QbtVersion.Parse("5.3.0"), Variant.Lt2, @"C:\qbt", 15618),
        };

        state.Save(path);

        Assert.Equal(state, UpdateState.Load(path, []));
    }

    [Fact]
    public void An_interrupted_install_is_recovered_from_state()
    {
        var state = new UpdateState
        {
            LastAttempt = new ApplyAttempt(DateTimeOffset.UtcNow, ApplyAttempt.Installing, "Installing", "abc", QbtVersion.Parse("5.3.0"), Variant.Lt12, @"C:\Program Files\qBittorrent"),
        };

        InstallInfo recovered = InstallInfoRecovery.Recover(new InstallInfo { Kind = InstallKind.NotInstalled }, state);

        Assert.True(recovered.IsSupported);
        Assert.True(recovered.IsInconsistent);
        Assert.Equal(@"C:\Program Files\qBittorrent", recovered.InstallDir);
        Assert.Equal(Variant.Lt12, recovered.DetectedVariant);
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("busy")]
    public void Only_an_unfinished_install_is_recovered(string result)
    {
        var state = new UpdateState { LastAttempt = new ApplyAttempt(DateTimeOffset.UtcNow, result, "", InstallDir: @"C:\qbt") };

        Assert.Equal(InstallKind.NotInstalled, InstallInfoRecovery.Recover(new InstallInfo { Kind = InstallKind.NotInstalled }, state).Kind);
    }

    [Fact]
    public void A_present_install_is_never_replaced_by_the_recovery()
    {
        var detected = new InstallInfo { Kind = InstallKind.Fork };
        var state = new UpdateState { LastAttempt = new ApplyAttempt(DateTimeOffset.UtcNow, ApplyAttempt.Installing, "", InstallDir: @"C:\qbt") };

        Assert.Same(detected, InstallInfoRecovery.Recover(detected, state));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}

public sealed class AclTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    public AclTests() => Directory.CreateDirectory(_dir);

    private static string Sddl(DirectorySecurity security) => security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [Fact]
    public void Work_is_for_system_and_administrators_only()
    {
        string sddl = Sddl(Acl.ForWork());

        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal); // protected: nothing inherited from ProgramData
        Assert.DoesNotContain(";BU)", sddl, StringComparison.Ordinal);
        Assert.Contains(";SY)", sddl, StringComparison.Ordinal);
        Assert.Contains(";BA)", sddl, StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_lets_users_create_files_but_not_folders_nor_read_others_files()
    {
        DirectorySecurity security = Acl.ForInbox();
        var users = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Single(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinUsersSid));

        Assert.True(users.FileSystemRights.HasFlag(FileSystemRights.CreateFiles));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.CreateDirectories));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.ListDirectory));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.Delete));
        Assert.Equal(InheritanceFlags.None, users.InheritanceFlags);
        Assert.Contains(";CO)", Sddl(security), StringComparison.Ordinal); // creator owner keeps their own files
    }

    [Fact]
    public void Machine_data_is_readable_by_users()
    {
        string sddl = Sddl(Acl.ForMachineData());

        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;0x1200a9;;;BU)", sddl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_writable_by_users_is_detected()
    {
        var info = new DirectoryInfo(_dir);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        Assert.True(Acl.IsWritableByNonAdmins(_dir));
    }

    [Fact]
    public void A_folder_created_by_a_user_or_a_junction_is_not_trusted()
    {
        // Run elevated (as on CI runners), the folder would belong to Administrators: give it to the user.
        var info = new DirectoryInfo(_dir);
        DirectorySecurity owner = info.GetAccessControl(AccessControlSections.Owner);
        owner.SetOwner(WindowsIdentity.GetCurrent().User!);
        info.SetAccessControl(owner);

        Assert.False(Acl.IsTrustedDirectory(_dir)); // owned by the current user, not by Administrators/SYSTEM
        Assert.False(Acl.IsTrustedDirectory(Path.Combine(_dir, "missing")));

        string target = Path.Combine(_dir, "target");
        string junction = Path.Combine(_dir, "junction");
        Directory.CreateDirectory(target);
        using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            mklink.WaitForExit();

        Assert.True(Directory.Exists(junction));
        Assert.False(Acl.IsTrustedDirectory(junction));
    }

    [Fact]
    public void Program_files_is_not_writable_by_users()
    {
        Assert.False(Acl.IsWritableByNonAdmins(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    public void Dispose()
    {
        foreach (string junction in Directory.EnumerateDirectories(_dir).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            Directory.Delete(junction);
        Directory.Delete(_dir, recursive: true);
    }
}
