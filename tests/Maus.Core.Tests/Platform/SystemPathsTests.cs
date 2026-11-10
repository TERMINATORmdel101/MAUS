using System.Security.AccessControl;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Platform;

[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>Ces tests changent l'environnement du processus : ils tournent seuls, et remettent tout en place.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class SystemPathsTests : IDisposable
{
    private static readonly string[] Names = ["SystemDrive", "SystemRoot", "windir", "ProgramData", "ALLUSERSPROFILE", "TEMP", "TMP"];

    private readonly Dictionary<string, string?> _saved = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    private readonly string _elsewhere = Path.Combine(Path.GetTempPath(), "maus-env-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_elsewhere))
        {
            Directory.Delete(_elsewhere, recursive: true);
        }
    }

    private void MoveEnvironment()
    {
        Directory.CreateDirectory(Path.Combine(_elsewhere, "ProgramData"));
        foreach (var name in new[] { "SystemDrive", "SystemRoot", "windir" })
        {
            Environment.SetEnvironmentVariable(name, _elsewhere);
        }

        Environment.SetEnvironmentVariable("ProgramData", Path.Combine(_elsewhere, "ProgramData"));
        Environment.SetEnvironmentVariable("ALLUSERSPROFILE", Path.Combine(_elsewhere, "ProgramData"));
    }

    [Fact]
    public void Every_reserved_folder_ignores_the_environment_of_the_process()
    {
        var root = SystemPaths.ProgramData;
        var windows = SystemPaths.WindowsFolder;

        MoveEnvironment();

        Assert.Equal(root, SystemPaths.ProgramData);
        Assert.Equal(windows, SystemPaths.WindowsFolder);
        foreach (var folder in new[]
        {
            FileJournalStore.DefaultDirectory,
            FilePreferencesStore.DefaultDirectory,
            CrashLog.ProtectedFolder,
            GraphicsDriverActions.BackupRoot,
        })
        {
            Assert.True(SystemPaths.IsUnder(folder, root + @"\MAUS"), folder);
        }
    }

    [Fact]
    public void A_folder_outside_the_expected_root_is_refused_and_left_alone()
    {
        MoveEnvironment();
        var folder = Path.Combine(_elsewhere, "ProgramData", "MAUS", "journal");

        Assert.Throws<JournalUnsafeException>(() => new WindowsDirectoryProtector().EnsureProtected(folder));

        Assert.False(Directory.Exists(Path.Combine(_elsewhere, "ProgramData", "MAUS")));
    }

    [Fact]
    public void Resetting_the_environment_gives_the_system_values_back()
    {
        var expected = SystemPaths.SystemVariables();
        MoveEnvironment();
        Environment.SetEnvironmentVariable("TEMP", _elsewhere);
        Environment.SetEnvironmentVariable("TMP", _elsewhere);

        SystemPaths.ResetEnvironment();

        foreach (var name in Names)
        {
            Assert.Equal(expected[name], Environment.GetEnvironmentVariable(name));
        }

        Assert.Equal(SystemPaths.WindowsFolder, Environment.GetEnvironmentVariable("SystemRoot"));
        Assert.Equal(SystemPaths.ProgramData, Environment.GetEnvironmentVariable("ProgramData"));
    }

    [Theory]
    [InlineData(@"%SystemDrive%\ProgramData", @"D:\ProgramData")]
    [InlineData(@"%systemdrive%\ProgramData\", @"D:\ProgramData")]
    [InlineData(@"E:\Donnees", @"E:\Donnees")]
    [InlineData(null, @"D:\ProgramData")]
    [InlineData("", @"D:\ProgramData")]
    [InlineData(@"%ALLUSERSPROFILE%", @"D:\ProgramData")]
    [InlineData(@"%USERPROFILE%\ProgramData", @"D:\ProgramData")]
    [InlineData(@"\\serveur\partage", @"D:\ProgramData")]
    [InlineData(@"relatif\ProgramData", @"D:\ProgramData")]
    [InlineData(@"D:\a\..\ProgramData", @"D:\ProgramData")]
    public void The_raw_machine_value_gives_the_root_with_no_variable_but_the_system_drive(string? raw, string expected) =>
        Assert.Equal(expected, SystemPaths.ProgramDataFrom(raw, @"D:\Windows"));

    [Theory]
    [InlineData(@"%SystemRoot%\TEMP", @"D:\Windows\TEMP")]
    [InlineData(@"%USERPROFILE%\Temp", @"D:\Windows\Temp")]
    public void The_machine_temporary_folder_comes_from_the_machine(string raw, string expected) =>
        Assert.Equal(expected, SystemPaths.TempFrom(raw, @"D:\Windows"));

    private static DirectorySecurity Folder(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    // Droits d'origine relevés tels quels sur Windows 11 : ceux de C:\ (le droit « LC » des utilisateurs authentifiés est celui de
    // créer un dossier à la racine) et ceux de C:\ProgramData. Le propriétaire varie selon l'installation de Windows.
    private const string RootRights = "D:PAI(A;;LC;;;AU)(A;OICIIO;SDGXGWGR;;;AU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;;0x1000a1;;;S-1-15-3-65536-1888954469-739942743-1668119174-2468466756-4239452838-1296943325-355587736-700089176)";
    private const string ProgramDataRights = "D:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;DCLCRPCR;;;BU)";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    [Theory]
    [InlineData("O:" + TrustedInstaller + "G:" + TrustedInstaller + RootRights)]
    [InlineData("O:SYG:SY" + RootRights)]
    [InlineData("O:BAG:SY" + RootRights)]
    [InlineData("O:SYG:SY" + ProgramDataRights)]
    [InlineData("O:BAG:SY" + ProgramDataRights)]
    [InlineData("O:" + TrustedInstaller + "G:SY" + ProgramDataRights)]
    public void An_ancestor_with_the_rights_of_windows_is_accepted(string sddl) =>
        Assert.True(WindowsDirectoryProtector.IsSafeAncestor(Folder(sddl)));

    [Theory]
    [InlineData("O:BUD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BU)")]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;OICI;0x1301bf;;;AU)")]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;;0x40;;;BU)")]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;;0x40000;;;BU)")]
    [InlineData("O:SYD:(A;OICI;FA;;;SY)(A;;0x80000;;;BU)")]
    public void An_ancestor_that_another_account_controls_is_refused(string sddl) =>
        Assert.False(WindowsDirectoryProtector.IsSafeAncestor(Folder(sddl)));

    private static FileSecurity FileRights(string sddl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    // Tel que le laisse une version précédente dans le dossier protégé : droits hérités du dossier, propriétaire Administrateurs.
    private const string WrittenByMaus = "O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)";

    [Theory]
    [InlineData(WrittenByMaus)]
    [InlineData("O:SYD:(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)")]
    public void A_file_with_correct_rights_stays_trusted(string sddl) =>
        Assert.True(WindowsDirectoryProtector.IsTrustedFile(FileRights(sddl), 1));

    [Theory]
    [InlineData("O:BUD:(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FW;;;BU)")]
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x2;;;AU)")]
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x10000;;;WD)")]
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;S-1-5-21-1-2-3-1001)")]
    public void A_file_another_account_can_write_is_not_trusted(string sddl) =>
        Assert.False(WindowsDirectoryProtector.IsTrustedFile(FileRights(sddl), 1));

    [Fact]
    public void A_file_with_a_second_name_is_not_trusted() =>
        Assert.False(WindowsDirectoryProtector.IsTrustedFile(FileRights(WrittenByMaus), 2));

    [Fact]
    public void The_number_of_names_of_a_file_is_read_from_the_disk()
    {
        Directory.CreateDirectory(_elsewhere);
        var first = Path.Combine(_elsewhere, "a.json");
        var second = Path.Combine(_elsewhere, "b.json");
        File.WriteAllText(first, "{}");

        Assert.Equal(1u, AtomicFile.LinkCount(first));

        using var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /H \"{second}\" \"{first}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        link.WaitForExit();

        Assert.Equal(2u, AtomicFile.LinkCount(first));
    }
}
