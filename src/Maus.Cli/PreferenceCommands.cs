using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>Choix de l'utilisateur : profil Game Bar, alimentation du portable, constats « voulus ».</summary>
internal static class PreferenceCommands
{
    public static int Print(UserPreferences preferences, TextWriter output)
    {
        output.WriteLine(T("Profil Game Bar : {0}", (preferences.GameBarProfile is { } p ? T("profil {0} (choisi par vous)", p) : T("détecté par MAUS"))));
        output.WriteLine(T("Alimentation du portable : {0}", Describe(preferences.LaptopPower)));
        output.WriteLine(T("Audit de la semaine, vous prévenir : {0}", Describe(preferences.WeeklyNotification)));
        output.WriteLine(T("Modules décochés pour l'audit : {0}", preferences.SkippedModules.Count == 0 ? T("aucun") : string.Join(", ", preferences.SkippedModules)));
        output.WriteLine(T("Constats marqués « voulu » : {0}", preferences.Acknowledged.Count));
        foreach (var mark in preferences.Acknowledged)
        {
            output.WriteLine(T("  {0} (valeur : {1}, le {2:d})", mark.FindingId, mark.Current ?? "—", mark.At.ToLocalTime()));
        }

        return 0;
    }

    public static int Set(IPreferencesStore store, string? assignment, TextWriter output)
    {
        var parts = assignment?.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 })
        {
            output.WriteLine(T("Syntaxe : maus --set gamebar=1|2|3|auto  ou  maus --set alimentation=performance|partout|autonomie|auto"));
            return 1;
        }

        var preferences = store.Load();

        // L'aide écrit ces mots dans la langue de l'utilisateur : français, anglais et espagnol sont acceptés quelle que soit la langue affichée.
        UserPreferences? updated = (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()) switch
        {
            ("gamebar", "auto") => preferences with { GameBarProfile = null },
            ("gamebar", "1" or "2" or "3") => preferences with { GameBarProfile = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) },
            ("alimentation" or "power" or "energia" or "energía", "auto") => preferences with { LaptopPower = LaptopPowerChoice.NotChosen },
            ("alimentation" or "power" or "energia" or "energía", "performance" or "rendimiento") => preferences with { LaptopPower = LaptopPowerChoice.Performance },
            ("alimentation" or "power" or "energia" or "energía", "partout" or "everywhere" or "siempre") => preferences with { LaptopPower = LaptopPowerChoice.PerformanceEverywhere },
            ("alimentation" or "power" or "energia" or "energía", "autonomie" or "battery" or "bateria" or "batería") => preferences with { LaptopPower = LaptopPowerChoice.Battery },
            _ => null,
        };

        if (updated is null)
        {
            output.WriteLine(T("Choix inconnu : {0}", assignment));
            return 1;
        }

        return Save(store, updated, output, T("Choix enregistré."));
    }

    public static int Acknowledge(IPreferencesStore store, AuditContext context, IReadOnlyList<ModuleResult> results, string? id, bool assumeYes, TextWriter output)
    {
        var finding = results.SelectMany(r => r.Findings).FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        if (finding is null || finding.Status is not (FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem))
        {
            output.WriteLine(T("Aucun écart signalé sous l'identifiant « {0} » (voir le rapport d'audit).", id));
            return 1;
        }

        output.WriteLine($"{finding.Title} : {finding.Current} ({Labels.Of(finding.Status)})");
        output.WriteLine(T("MAUS ne le signalera plus tant que cette valeur ne change pas."));
        // Comme la fenêtre et le moteur : toujours une question, sauf --yes.
        var question = finding.Status == FindingStatus.Problem
            ? T("C'est un problème de sécurité ou de fiabilité. Le marquer « voulu » quand même ? (o/N) ")
            : T("Le marquer « voulu » ? (o/N) ");
        if (!assumeYes && !FixCommands.Confirm(question))
        {
            return 1;
        }

        return Save(store, store.Load().Acknowledge(finding, context.Now), output, T("Constat marqué « voulu »."));
    }

    public static int Unacknowledge(IPreferencesStore store, string? id, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            output.WriteLine(T("Indiquez le constat : maus --unack ID (voir maus --prefs)."));
            return 1;
        }

        var preferences = store.Load();
        if (!preferences.Acknowledged.Any(a => a.FindingId.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine(T("Aucune marque pour {0} : rien à retirer.", id));
            return 1;
        }

        return Save(store, preferences.Unacknowledge(id), output, T("Marque retirée : le constat sera de nouveau signalé."));
    }

    private static int Save(IPreferencesStore store, UserPreferences preferences, TextWriter output, string message)
    {
        try
        {
            store.Save(preferences);
            output.WriteLine(message);
            if (store.Notice is { } notice)
            {
                output.WriteLine(notice);
            }

            return 0;
        }
        catch (Exception ex) when (ex is JournalUnsafeException or UnauthorizedAccessException or IOException)
        {
            output.WriteLine(T("Enregistrement impossible (droits administrateur requis) : {0}", ex.Message));
            return 3;
        }
    }

    private static string Describe(WeeklyNotification level) => level switch
    {
        WeeklyNotification.Never => T("jamais"),
        WeeklyNotification.Always => T("à chaque audit"),
        _ => T("seulement pour un problème rouge"),
    };

    private static string Describe(LaptopPowerChoice choice) => choice switch
    {
        LaptopPowerChoice.Performance => T("performance sur secteur, Équilibré sur batterie"),
        LaptopPowerChoice.PerformanceEverywhere => T("performance partout"),
        LaptopPowerChoice.Battery => T("autonomie"),
        _ => T("pas encore choisi"),
    };
}
