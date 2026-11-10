using Maus.Core.Diagnostics;
using Maus.Core.Fixes;

namespace Maus.Core.Tests.Diagnostics;

public sealed class CrashLogTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 30, 14, 3, 12);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "maus-crashlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void The_log_keeps_the_version_the_step_the_error_and_the_last_actions()
    {
        var marker = "étape de test " + Guid.NewGuid().ToString("N");
        Breadcrumbs.Add(marker);

        var path = CrashLog.Write(_folder, new InvalidOperationException("boum"), "Erreur sur un fil d'arrière-plan", Now);

        Assert.Equal(Path.Combine(_folder, "erreur-20260930-140312.txt"), path);
        var text = File.ReadAllText(path!);
        Assert.Contains("MAUS " + AppVersion.Full + " · 2026-09-30 14:03:12", text, StringComparison.Ordinal);
        Assert.Contains("Erreur sur un fil d'arrière-plan", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boum", text, StringComparison.Ordinal);
        Assert.Contains("Dernières actions :", text, StringComparison.Ordinal);
        Assert.Contains(marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_errors_in_the_same_second_are_both_kept()
    {
        CrashLog.Write(_folder, new InvalidOperationException("première"), "tâche 1", Now);
        var path = CrashLog.Write(_folder, new ArgumentException("seconde"), "tâche 2", Now);

        var text = File.ReadAllText(path!);
        Assert.Contains("première", text, StringComparison.Ordinal);
        Assert.Contains("seconde", text, StringComparison.Ordinal);
    }

    /// <summary>Dossier protégé d'essai : accepté (créé dans le dossier temporaire du test) ou refusé.</summary>
    private sealed class Protector(bool safe) : IDirectoryProtector
    {
        public void EnsureProtected(string directory)
        {
            if (!safe)
            {
                throw new JournalUnsafeException("dossier non sûr");
            }

            Directory.CreateDirectory(directory);
        }

        public bool IsTrusted(string file) => true;

        public void ProtectFile(string file)
        {
        }
    }

    /// <summary>Remet le dossier des journaux d'avant le test : les autres tests écrivent toujours au même endroit.</summary>
    private static void Restore(string? folder)
    {
        if (folder is not null)
        {
            CrashLog.UseFolder(folder);
        }
    }

    [Fact]
    public void An_elevated_process_whose_protected_folder_is_refused_writes_nothing_to_disk()
    {
        var before = CrashLog.Folder;
        try
        {
            Assert.False(CrashLog.UseProtectedFolder(new Protector(safe: false), _folder));

            // Ni le dossier protégé ni le profil : les traces restent en mémoire.
            Assert.Null(CrashLog.Folder);
            Assert.Null(CrashLog.Write(new InvalidOperationException("boum"), "test"));
            Assert.False(Directory.Exists(_folder));
        }
        finally
        {
            Restore(before);
        }
    }

    [Fact]
    public void An_elevated_process_with_its_protected_folder_writes_its_log_there()
    {
        var before = CrashLog.Folder;
        try
        {
            Assert.True(CrashLog.UseProtectedFolder(new Protector(safe: true), _folder));

            Assert.Equal(_folder, CrashLog.Folder);
            var path = CrashLog.Write(new InvalidOperationException("boum"), "test");
            Assert.NotNull(path);
            Assert.Equal(_folder, Path.GetDirectoryName(path));
        }
        finally
        {
            Restore(before);
        }
    }

    [Fact]
    public void A_process_without_rights_keeps_its_log_in_the_user_profile() =>
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs"), CrashLog.UserFolder);

    [Fact]
    public void An_unwritable_folder_gives_no_path_and_no_error()
    {
        // Un fichier à la place du dossier : impossible d'y écrire, et le filet de sécurité ne doit pas planter à son tour.
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "logs");
        File.WriteAllText(blocker, "pas un dossier");

        Assert.Null(CrashLog.Write(blocker, new InvalidOperationException("boum"), "test", Now));
    }
}
