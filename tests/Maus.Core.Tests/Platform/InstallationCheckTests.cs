using System.Security.Cryptography;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

public sealed class InstallationCheckTests
{
    private const string Installed = @"C:\Program Files\MAUS\MAUS.exe";
    private const string Downloaded = @"C:\Users\moi\Downloads\MAUS\MAUS.exe";
    private const string Journal = @"C:\ProgramData\MAUS\journal";

    private static readonly string[] Roots = [@"C:\Program Files"];

    private sealed class FakeProbe : IInstallationProbe
    {
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FolderAccess Access { get; set; } = FolderAccess.Denied;

        public string? HashOf(string file) => Hashes.GetValueOrDefault(Path.GetFileName(file));

        public FolderAccess AccessOf(string folder) => Access;
    }

    private static InstallationLine Line(IReadOnlyList<InstallationLine> lines, string title) => lines.Single(l => l.Title == title);

    private static FakeProbe Healthy() => new()
    {
        Hashes = { ["Maus.Core.dll"] = "AAAA", ["MAUS.dll"] = "BBBB" },
    };

    private static IReadOnlyList<InstallationLine> Run(FakeProbe probe, string executable = Installed) =>
        InstallationCheck.Run(executable, Roots, new Dictionary<string, string> { ["Maus.Core"] = "aaaa" }, Journal, probe);

    [Fact]
    public void A_normal_installation_passes_with_the_hash_to_compare()
    {
        var lines = Run(Healthy());

        Assert.Equal(InstallationVerdict.Ok, Line(lines, "Dossier de MAUS").Verdict);
        Assert.Equal(InstallationVerdict.Ok, Line(lines, "Bibliothèques de MAUS").Verdict);
        Assert.Equal(InstallationVerdict.Ok, Line(lines, "Journal des corrections").Verdict);
        var program = Line(lines, "Empreinte du programme");
        Assert.Equal(InstallationVerdict.Info, program.Verdict);
        Assert.Contains("BBBB", program.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_outside_a_protected_folder_is_flagged()
    {
        var folder = Line(Run(Healthy(), Downloaded), "Dossier de MAUS");

        Assert.Equal(InstallationVerdict.Warning, folder.Verdict);
        Assert.Contains("Downloads", folder.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CCCC")]
    [InlineData(null)]
    public void A_changed_or_missing_library_is_named(string? found)
    {
        var probe = Healthy();
        probe.Hashes.Remove("Maus.Core.dll");
        if (found is not null)
        {
            probe.Hashes["Maus.Core.dll"] = found;
        }

        var libraries = Line(Run(probe), "Bibliothèques de MAUS");

        Assert.Equal(InstallationVerdict.Warning, libraries.Verdict);
        Assert.Contains("Maus.Core", libraries.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_embedded_hashes_nothing_is_claimed()
    {
        var libraries = Line(InstallationCheck.Run(Installed, Roots, new Dictionary<string, string>(), Journal, Healthy()), "Bibliothèques de MAUS");

        Assert.Equal(InstallationVerdict.Info, libraries.Verdict);
    }

    [Fact]
    public void A_missing_program_file_is_flagged()
    {
        var probe = Healthy();
        probe.Hashes.Remove("MAUS.dll");

        Assert.Equal(InstallationVerdict.Warning, Line(Run(probe), "Empreinte du programme").Verdict);
    }

    [Theory]
    [InlineData(FolderAccess.Absent, InstallationVerdict.Info)]
    [InlineData(FolderAccess.Denied, InstallationVerdict.Ok)]
    [InlineData(FolderAccess.AdminsOnly, InstallationVerdict.Ok)]
    [InlineData(FolderAccess.OpenToOthers, InstallationVerdict.Warning)]
    [InlineData(FolderAccess.Unknown, InstallationVerdict.Info)]
    public void The_journal_folder_is_judged_on_who_can_write(FolderAccess access, InstallationVerdict verdict)
    {
        var probe = Healthy();
        probe.Access = access;

        Assert.Equal(verdict, Line(Run(probe), "Journal des corrections").Verdict);
    }

    [Fact]
    public void The_page_says_it_is_not_an_absolute_proof() =>
        Assert.Contains("pas une preuve absolue", InstallationCheck.Limit, StringComparison.Ordinal);

    [Fact]
    public void The_real_probe_hashes_files_and_tells_a_missing_folder()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            var probe = new WindowsInstallationProbe();

            Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), probe.HashOf(file));
            Assert.Null(probe.HashOf(file + ".absent"));
            Assert.Equal(FolderAccess.Absent, probe.AccessOf(file + ".absent"));
            Assert.Equal(FolderAccess.OpenToOthers, probe.AccessOf(Path.GetTempPath()));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
