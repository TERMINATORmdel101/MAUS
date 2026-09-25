using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Preferences;
using Maus.Core.Reporting;

namespace Maus.Cli;

/// <summary>Choix de l'utilisateur : profil Game Bar, alimentation du portable, constats « voulus ».</summary>
internal static class PreferenceCommands
{
    public static int Print(UserPreferences preferences, TextWriter output)
    {
        output.WriteLine($"Profil Game Bar : {(preferences.GameBarProfile is { } p ? $"profil {p} (choisi par vous)" : "détecté par MAUS")}");
        output.WriteLine($"Alimentation du portable : {Describe(preferences.LaptopPower)}");
        output.WriteLine($"Constats marqués « voulu » : {preferences.Acknowledged.Count}");
        foreach (var mark in preferences.Acknowledged)
        {
            output.WriteLine($"  {mark.FindingId} (valeur : {mark.Current ?? "—"}, le {mark.At.ToLocalTime():dd/MM/yyyy})");
        }

        return 0;
    }

    public static int Set(IPreferencesStore store, string? assignment, TextWriter output)
    {
        var parts = assignment?.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 })
        {
            output.WriteLine("Syntaxe : maus --set gamebar=1|2|3|auto  ou  maus --set alimentation=performance|partout|autonomie|auto");
            return 1;
        }

        var preferences = store.Load();
        UserPreferences? updated = (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()) switch
        {
            ("gamebar", "auto") => preferences with { GameBarProfile = null },
            ("gamebar", "1" or "2" or "3") => preferences with { GameBarProfile = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) },
            ("alimentation", "auto") => preferences with { LaptopPower = LaptopPowerChoice.NotChosen },
            ("alimentation", "performance") => preferences with { LaptopPower = LaptopPowerChoice.Performance },
            ("alimentation", "partout") => preferences with { LaptopPower = LaptopPowerChoice.PerformanceEverywhere },
            ("alimentation", "autonomie") => preferences with { LaptopPower = LaptopPowerChoice.Battery },
            _ => null,
        };

        if (updated is null)
        {
            output.WriteLine($"Choix inconnu : {assignment}");
            return 1;
        }

        return Save(store, updated, output, "Choix enregistré.");
    }

    public static int Acknowledge(IPreferencesStore store, AuditContext context, IReadOnlyList<ModuleResult> results, string? id, bool assumeYes, TextWriter output)
    {
        var finding = results.SelectMany(r => r.Findings).FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        if (finding is null || finding.Status is not (FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem))
        {
            output.WriteLine($"Aucun écart signalé sous l'identifiant « {id} » (voir le rapport d'audit).");
            return 1;
        }

        output.WriteLine($"{finding.Title} : {finding.Current} ({Labels.Of(finding.Status)})");
        output.WriteLine("MAUS ne le signalera plus tant que cette valeur ne change pas.");
        if (finding.Status == FindingStatus.Problem && !assumeYes && !FixCommands.Confirm("C'est un problème de sécurité ou de fiabilité. Le marquer « voulu » quand même ? (o/N) "))
        {
            return 1;
        }

        return Save(store, store.Load().Acknowledge(finding, context.Now), output, "Constat marqué « voulu ».");
    }

    public static int Unacknowledge(IPreferencesStore store, string? id, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            output.WriteLine("Indiquez le constat : maus --unack ID (voir maus --prefs).");
            return 1;
        }

        return Save(store, store.Load().Unacknowledge(id), output, "Marque retirée : le constat sera de nouveau signalé.");
    }

    private static int Save(IPreferencesStore store, UserPreferences preferences, TextWriter output, string message)
    {
        try
        {
            store.Save(preferences);
            output.WriteLine(message);
            return 0;
        }
        catch (Exception ex) when (ex is JournalUnsafeException or UnauthorizedAccessException or IOException)
        {
            output.WriteLine($"Enregistrement impossible (droits administrateur requis) : {ex.Message}");
            return 3;
        }
    }

    private static string Describe(LaptopPowerChoice choice) => choice switch
    {
        LaptopPowerChoice.Performance => "performance sur secteur, Équilibré sur batterie",
        LaptopPowerChoice.PerformanceEverywhere => "performance partout",
        LaptopPowerChoice.Battery => "autonomie",
        _ => "pas encore choisi",
    };
}
