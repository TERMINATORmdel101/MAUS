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
    public static IReadOnlyList<SlownessCause> Analyze(IReadOnlyCollection<SensorSnapshot> samples, IReadOnlyList<ProcessSample> processes, ProcessCatalog catalog)
    {
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
        if (cpu is >= 70)
        {
            var who = topCpu is { CpuPercent: >= 20 } ? T(" Le plus gourmand : {0} ({1:0} %) — {2}", topCpu.Name, topCpu.CpuPercent, catalog.Explain(topCpu.Name).What) : string.Empty;
            causes.Add(new(T("Le processeur est très occupé ({0:0} % en moyenne)", cpu), T("Un ou plusieurs programmes utilisent le processeur en continu.") + who, "M12", cpu.Value));
        }

        var topMemory = processes.OrderByDescending(p => p.PrivateBytes).FirstOrDefault();
        if (memory is >= 85)
        {
            var who = topMemory is null ? string.Empty : T(" Le plus gourmand : {0} ({1:0.0} Go).", topMemory.Name, topMemory.PrivateBytes / 1073741824.0);
            causes.Add(new(T("La mémoire vive est presque pleine ({0:0} %)", memory), T("Windows doit écrire sur le disque ce qui ne tient plus en mémoire, ce qui ralentit tout.") + who, "M10", memory.Value));
        }

        if (disk is >= 80)
        {
            var who = processes.OrderByDescending(p => p.DiskBytesPerSecond).FirstOrDefault() is { DiskBytesPerSecond: > 1_000_000 } top
                ? T(" Le plus actif : {0} ({1:0.0} Mo/s).", top.Name, top.DiskBytesPerSecond / 1_048_576)
                : string.Empty;
            causes.Add(new(T("Le disque est saturé ({0:0} % d'activité)", disk), T("Un disque occupé en permanence fait attendre tous les programmes ; c'est très sensible sur un disque dur.") + who, "M11", disk.Value));
        }

        if (samples.Max(s => s.ThermalZoneC) is >= 90)
        {
            causes.Add(new(T("Le PC chauffe trop"), T("En surchauffe, le processeur baisse sa fréquence pour se protéger : tout ralentit."), "M11", 90));
        }

        if (gpu >= 90)
        {
            causes.Add(new(T("La carte graphique est à pleine charge ({0:0} %)", gpu), T("Normal pendant un jeu ; sinon, un programme utilise la carte graphique en arrière-plan."), "M09", gpu));
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
