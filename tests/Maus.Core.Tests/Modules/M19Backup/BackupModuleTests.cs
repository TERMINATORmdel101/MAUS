using Maus.Core.Modules.M19Backup;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M19Backup;

public class BackupModuleTests
{
    private const string SppClients = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string RestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    private static readonly string[] SystemDrive = ["C:"];

    private static FakeRegistry ProtectionOn() => new FakeRegistry().Set(RegistryHive.LocalMachine, SppClients, RestoreClient, SystemDrive);

    private static FakeCim Points(params string[] dates) =>
        new FakeCim().Answer(BackupModule.RestorePointsQuery, CimScopes.SystemRestore,
            dates.Select((d, i) => new Dictionary<string, object?> { ["SequenceNumber"] = (uint)i, ["CreationTime"] = d }).ToArray());

    private static async Task<Finding> Get(string id, FakeRegistry registry, FakeCim? cim = null, FakeFiles? files = null) =>
        (await new BackupModule().DetectAsync(TestContext.Create(registry: registry, cim: cim, files: files), CancellationToken.None)).Single(f => f.Id == id);

    [Fact]
    public async Task Protection_with_a_recent_point_is_compliant()
    {
        var finding = await Get("M19.restore-points", ProtectionOn(), Points("20260920100000.000000-000"));

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Contains("1 point", finding.Current, StringComparison.Ordinal);
        Assert.Null(finding.SettingsPage);
    }

    [Fact]
    public async Task Protection_without_any_point_opens_the_about_page()
    {
        var finding = await Get("M19.restore-points", ProtectionOn(), Points());

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("Protection du système", finding.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:about", finding.SettingsPage);
    }

    [Fact]
    public async Task Disabled_protection_is_a_warning()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SppClients, RestoreClient, Array.Empty<string>());

        var finding = await Get("M19.restore-points", registry);

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Equal("ms-settings:about", finding.SettingsPage);
    }

    [Fact]
    public async Task Documents_in_onedrive_are_not_green_and_both_retention_periods_are_given()
    {
        var registry = ProtectionOn()
            .Set(RegistryHive.CurrentUser, BackupModule.ShellFolders, "Personal", @"C:\Users\Alex\OneDrive\Documents")
            .Set(RegistryHive.CurrentUser, BackupModule.OneDriveAccounts + @"\Personal", "UserEmail", "a@exemple.fr");

        var finding = await Get("M19.personal-files", registry);

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("OneDrive (Documents)", finding.Current, StringComparison.Ordinal);
        Assert.Contains("non lue", finding.Current, StringComparison.Ordinal);
        Assert.Contains("30 jours avec un compte personnel", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("93 jours avec un compte professionnel ou scolaire", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("synchronisation", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_backup_is_reported_with_the_intentional_option()
    {
        var registry = ProtectionOn().Set(RegistryHive.CurrentUser, BackupModule.ShellFolders, "Personal", @"%USERPROFILE%\Documents");

        var finding = await Get("M19.personal-files", registry);

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("voulu", finding.Advice, StringComparison.Ordinal);
        Assert.Null(finding.SettingsPage);
    }

    [Fact]
    public async Task File_history_alone_is_reported_without_being_called_a_working_backup()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "FileHistory", "Configuration", "Config1.xml");

        var finding = await Get("M19.personal-files", ProtectionOn(), files: new FakeFiles().AddFile(config));

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Contains("non vérifié", finding.Current, StringComparison.Ordinal);
        Assert.Contains("dernière copie", finding.Advice, StringComparison.Ordinal);
    }
}
