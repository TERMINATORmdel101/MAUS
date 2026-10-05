using Maus.Core.Diagnostics;

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
        Assert.Contains("MAUS " + AppVersion.Display + " · 2026-09-30 14:03:12", text, StringComparison.Ordinal);
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
