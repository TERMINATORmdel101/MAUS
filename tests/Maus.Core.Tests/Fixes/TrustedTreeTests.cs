using System.Security.AccessControl;
using Maus.Core.Fixes;

namespace Maus.Core.Tests.Fixes;

/// <summary>
/// Contrôle d'une sauvegarde de pilote avant de la lister ou de la réinstaller (0.8.6) : le dossier, ses sous-dossiers et
/// chacun de leurs fichiers doivent être réservés aux administrateurs, sans lien.
/// </summary>
public sealed class TrustedTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maus-arbre-" + Guid.NewGuid().ToString("N"));

    public TrustedTreeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "display", "sous-dossier"));
        File.WriteAllText(Path.Combine(_root, "oem12.inf"), "[Version]");
        File.WriteAllText(Path.Combine(_root, "display", "pilote.sys"), "x");
        File.WriteAllText(Path.Combine(_root, "display", "sous-dossier", "pilote.cat"), "x");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static DirectorySecurity Folder(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    private static bool Trusted(string _) => true;

    // Tel que le moteur le crée sous le dossier protégé : droits hérités de SYSTEM et des Administrateurs.
    [Theory]
    [InlineData("O:BAD:AI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)")]
    [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(D;OICI;FA;;;WD)")]
    public void A_folder_reserved_to_administrators_is_trusted(string sddl) =>
        Assert.True(WindowsDirectoryProtector.IsTrustedFolder(Folder(sddl)));

    [Theory]
    [InlineData("O:BUD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData("O:S-1-5-21-1-2-3-1001D:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x2;;;BU)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x4;;;AU)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x10000;;;WD)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICIIO;GA;;;CO)")]
    [InlineData("O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;S-1-5-21-1-2-3-1001)")]
    public void A_folder_another_account_can_change_is_not_trusted(string sddl) =>
        Assert.False(WindowsDirectoryProtector.IsTrustedFolder(Folder(sddl)));

    [Fact]
    public void A_backup_whose_folders_and_files_all_pass_is_trusted() =>
        Assert.True(WindowsDirectoryProtector.IsTrustedTree(_root, Trusted, Trusted));

    [Fact]
    public void One_file_deep_inside_that_does_not_pass_refuses_the_whole_backup() =>
        Assert.False(WindowsDirectoryProtector.IsTrustedTree(_root, Trusted, file => Path.GetFileName(file) != "pilote.cat"));

    [Fact]
    public void One_subfolder_that_does_not_pass_refuses_the_whole_backup() =>
        Assert.False(WindowsDirectoryProtector.IsTrustedTree(_root, folder => Path.GetFileName(folder) != "sous-dossier", Trusted));

    [Fact]
    public void Hidden_and_system_files_are_checked_too()
    {
        var hidden = Path.Combine(_root, "display", "cache.inf");
        File.WriteAllText(hidden, "[Version]");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        Assert.False(WindowsDirectoryProtector.IsTrustedTree(_root, Trusted, file => file != hidden));
    }

    [Fact]
    public void A_missing_backup_is_not_trusted() =>
        Assert.False(WindowsDirectoryProtector.IsTrustedTree(Path.Combine(_root, "absent"), Trusted, Trusted));

    /// <summary>Un dossier du profil (le dossier temporaire du compte qui lance les tests) donne la main à ce compte : refusé.</summary>
    [Fact]
    public void A_folder_that_the_account_running_the_tests_can_write_is_not_trusted() =>
        Assert.False(new WindowsDirectoryProtector().IsTrustedTree(_root));
}
