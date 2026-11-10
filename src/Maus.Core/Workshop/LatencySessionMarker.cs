using System.Diagnostics;
using System.Globalization;
using Maus.Core.Diagnostics;

namespace Maus.Core.Workshop;

/// <summary>
/// Trace laissée sur le disque pendant une mesure de latence : si le moteur est tué en pleine mesure, aucun code de MAUS ne
/// peut refermer la session du noyau, et Windows la garde jusqu'au redémarrage. Au lancement suivant, ce fichier prouve
/// que la session restée ouverte est bien celle de MAUS, et non celle d'un autre outil, qui ne doit jamais être coupée.
/// Le dossier est réservé à SYSTEM et aux administrateurs.
/// </summary>
internal sealed record LatencySessionMarker(int ProcessId, DateTime ProcessStartUtc, DateTime BootUtc)
{
    private const string FileName = "mesure-latence-en-cours.txt";

    /// <summary>Marge sur l'heure de démarrage du PC, calculée depuis l'horloge : elle bouge un peu d'une lecture à l'autre.</summary>
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Heure de démarrage du PC : une session du noyau ne survit pas à un redémarrage.</summary>
    internal static DateTime CurrentBootUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Le fichier parle d'avant le dernier redémarrage : la session qu'il décrit n'existe plus.</summary>
    internal bool IsFromEarlierBoot(DateTime bootUtc) => (bootUtc - BootUtc).Duration() > BootTolerance;

    /// <summary>La session a été laissée par un moteur disparu, depuis le dernier démarrage du PC.</summary>
    internal bool IsLeftover(DateTime bootUtc, bool ownerRunning) => !ownerRunning && !IsFromEarlierBoot(bootUtc);

    /// <summary>Une session qui écoute autre chose que les interruptions et les DPC n'a pas été ouverte par MAUS.</summary>
    internal static bool LooksLikeOurSession(uint enableFlags, uint logFileMode, uint expectedFlags, uint realTimeMode) =>
        enableFlags == expectedFlags && (logFileMode & realTimeMode) != 0;

    internal static LatencySessionMarker ForThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        return new LatencySessionMarker(self.Id, self.StartTime.ToUniversalTime(), CurrentBootUtc);
    }

    /// <summary>Ce processus tourne encore. Illisible : il est supposé vivant, et rien n'est coupé.</summary>
    internal static bool IsRunning(int processId, DateTime startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (process.StartTime.ToUniversalTime() - startUtc).Duration() < TimeSpan.FromSeconds(1);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>Dossier protégé du fichier, ou <c>null</c> s'il ne peut pas l'être : sans lui, rien n'est écrit ni cru.</summary>
    internal static string? ProtectedFolder()
    {
        try
        {
            new Fixes.WindowsDirectoryProtector().EnsureProtected(CrashLog.ProtectedFolder);
            return CrashLog.ProtectedFolder;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Breadcrumbs.Add("latence : dossier protégé indisponible (" + ex.GetType().Name + ")");
            return null;
        }
    }

    internal void Write(string folder)
    {
        try
        {
            var text = string.Join('\n', ProcessId.ToString(CultureInfo.InvariantCulture), ProcessStartUtc.Ticks.ToString(CultureInfo.InvariantCulture), BootUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(folder, FileName), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans ce fichier, la mesure fonctionne quand même : seul le rattrapage après un moteur tué est perdu.
            Breadcrumbs.Add("latence : trace de la mesure non écrite (" + ex.GetType().Name + ")");
        }
    }

    internal static LatencySessionMarker? Read(string folder)
    {
        try
        {
            return Parse(File.ReadAllText(Path.Combine(folder, FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static LatencySessionMarker? Parse(string text)
    {
        var parts = text.Split('\n');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start <= DateTime.MaxValue.Ticks
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var boot) && boot <= DateTime.MaxValue.Ticks
            ? new LatencySessionMarker(id, new DateTime(start, DateTimeKind.Utc), new DateTime(boot, DateTimeKind.Utc))
            : null;
    }

    internal static void Delete(string folder)
    {
        try
        {
            File.Delete(Path.Combine(folder, FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Breadcrumbs.Add("latence : trace de la mesure non effacée (" + ex.GetType().Name + ")");
        }
    }
}
