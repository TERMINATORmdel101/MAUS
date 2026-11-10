using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Une cause probable de lenteur, avec le module de MAUS qui aide à la corriger.</summary>
public sealed record SlownessCause(string Title, string Explanation, string? ModuleId, double Weight);

/// <summary>
/// « Pourquoi mon PC est lent ? » : à partir d'environ une minute de mesures, les causes principales, classées.
/// Chaque cause renvoie au module qui la corrige.
/// </summary>
public static class SlownessDiagnosis
{
    private const int CpuBusyPercent = 70;
    private const int MemoryFullPercent = 85;
    private const int DiskBusyPercent = 80;
    private const int GpuBusyPercent = 90;

    /// <summary>Ces seuils n'ont pas de source publiée : la phrase est affichée avec le résultat (principe 7).</summary>
    public static string ThresholdNote => T("Seuils de ce diagnostic : processeur {0} %, mémoire {1} %, disque {2} %, carte graphique {3} %. Ils sont choisis par MAUS, sans source publiée.",
        CpuBusyPercent, MemoryFullPercent, DiskBusyPercent, GpuBusyPercent);

    /// <param name="samples">Mesures de la minute écoulée.</param>
    /// <param name="processes">Processus au moment de l'analyse.</param>
    /// <param name="catalog">Explication des processus connus.</param>
    /// <param name="cpuMaxC">Température maximale du processeur selon son fabricant (catalogue des seuils), si connue.</param>
    public static IReadOnlyList<SlownessCause> Analyze(IReadOnlyCollection<SensorSnapshot> samples, IReadOnlyList<ProcessSample> processes, ProcessCatalog catalog, int? cpuMaxC = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(samples);

        var causes = new List<SlownessCause>();
        if (samples.Count == 0)
        {
            return causes;
        }

        var cpu = Average(samples, s => s.CpuPercent);
        var memory = Average(samples, s => s.MemoryPercent);
        var disk = Average(samples, s => s.DiskActivePercent);
        var gpu = samples.SelectMany(s => s.Gpus).Select(g => g.UtilizationPercent).OfType<double>().DefaultIfEmpty(0).Average();

        var topCpu = processes.Where(p => p.Pid != 0).OrderByDescending(p => p.CpuPercent).FirstOrDefault();
        if (cpu is >= CpuBusyPercent)
        {
            var who = topCpu is { CpuPercent: >= 20 } ? T(" Le plus gourmand : {0} ({1:0} %) — {2}", topCpu.Name, topCpu.CpuPercent, catalog.Explain(topCpu.Name).What) : string.Empty;
            causes.Add(new(T("Le processeur est très occupé ({0:0} % en moyenne)", cpu), T("Un ou plusieurs programmes utilisent le processeur en continu.") + who, "M12", cpu.Value));
        }

        var topMemory = processes.OrderByDescending(p => p.PrivateBytes).FirstOrDefault();
        if (memory is >= MemoryFullPercent)
        {
            var who = topMemory is null ? string.Empty : T(" Le plus gourmand : {0} ({1:0.0} Go).", topMemory.Name, topMemory.PrivateBytes / 1073741824.0);
            causes.Add(new(T("La mémoire vive est presque pleine ({0:0} %)", memory), T("Windows doit écrire sur le disque ce qui ne tient plus en mémoire, ce qui ralentit tout.") + who, "M10", memory.Value));
        }

        if (disk is >= DiskBusyPercent)
        {
            var who = processes.OrderByDescending(p => p.IoBytesPerSecond).FirstOrDefault() is { IoBytesPerSecond: > 1_000_000 } top
                ? T(" Le plus gros lecteur-écrivain (disque, réseau, cache) : {0} ({1:0.0} Mo/s).", top.Name, top.IoBytesPerSecond / 1_048_576)
                : string.Empty;
            causes.Add(new(T("Le disque est saturé ({0:0} % d'activité)", disk), T("Un disque occupé en permanence fait attendre tous les programmes ; c'est très sensible sur un disque dur.") + who, "M11", disk.Value));
        }

        // La limite du processeur ou de son fabricant, jamais la zone ACPI : approximative, et aucun seuil publié ne vaut pour toutes
        // les cartes mères (voir SensorAlarms). Limite inconnue : pas de cause inventée.
        var hottest = samples.Where(s => s.CpuTemperatureC is not null).MaxBy(s => s.CpuTemperatureC);
        if (hottest is { CpuTemperatureC: { } hot } && ThermalRules.CpuLimit(hottest.CpuTjMaxC, cpuMaxC) is { } limit && hot >= limit)
        {
            causes.Add(new(T("Le processeur atteint sa limite de température ({0:0} °C sur {1} °C)", hot, limit),
                T("À sa limite, le processeur baisse sa fréquence pour se protéger : tout ralentit. Certains modèles récents y montent volontairement en pleine charge ; au repos, vérifiez le refroidissement."), "M11", 90));
        }

        if (gpu >= GpuBusyPercent)
        {
            causes.Add(new(T("La carte graphique est à pleine charge ({0:0} %)", gpu), T("Normal pendant un jeu ; sinon, un programme utilise la carte graphique en arrière-plan."), "M09", gpu));
        }

        if (causes.Count == 0 && (cpu is null || disk is null))
        {
            causes.Add(new(T("Processeur ou disque illisibles"),
                T("Pendant cette minute, MAUS n'a pas pu lire le processeur ou le disque : il ne peut pas dire s'ils étaient surchargés."),
                "M12", 0));
        }

        if (causes.Count == 0)
        {
            causes.Add(new(T("Aucune surcharge pendant la mesure"),
                T("Pendant cette minute, le processeur, la mémoire et le disque étaient disponibles. Si la lenteur arrive à d'autres moments (démarrage, jeux), relancez le diagnostic à ce moment-là, et regardez les applications au démarrage (Module 12)."),
                "M12", 0));
        }

        return causes.OrderByDescending(c => c.Weight).Take(3).ToList();
    }

    private static double? Average(IReadOnlyCollection<SensorSnapshot> samples, Func<SensorSnapshot, double?> selector)
    {
        var values = samples.Select(selector).OfType<double>().ToList();
        return values.Count == 0 ? null : values.Average();
    }
}
