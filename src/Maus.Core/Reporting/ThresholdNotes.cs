using Maus.Core.Modules.M01Audit;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Modules.M03Updates;
using Maus.Core.Modules.M08Bios;
using Maus.Core.Modules.M09Gpu;
using Maus.Core.Modules.M10Memory;
using Maus.Core.Modules.M11Health;
using Maus.Core.Modules.M16Workshop;
using Maus.Core.Modules.M18Network;
using Maus.Core.Modules.M19Backup;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>
/// D'où vient le seuil d'un constat (principe 7, décision du 31/10/2026) : la source publiée quand elle existe et a été relue,
/// sinon l'aveu « choisi par MAUS ». Affiché sous « Détails techniques » du constat. Une étoile dans la clé remplace la partie
/// variable de l'identifiant (nom du disque, de la carte...). Les valeurs viennent des constantes des modules ; celles du
/// catalogue des pilotes et de la marge de la carte graphique sont contrôlées par un test.
/// </summary>
public static class ThresholdNotes
{
    private static readonly (string Key, Func<string> Note)[] Notes =
    [
        ("M01.wu-pause", () => Sourced(T("pause de {0} jours au maximum", RiskyChangesAuditModule.MaxPauseDays), T("Microsoft Learn, « Configure Windows Update client policies »"))),
        ("M01.pagefile", () => Chosen(T("moins de {0} Mo au total = très petit", RiskyChangesAuditModule.TinyPageFileMegabytes))),
        ("M02.reliability-index", () => Chosen(T("indice de stabilité de Windows sous {0:0.0} sur 10", WindowsHealthModule.MinimumStabilityIndex))),
        ("M02.bluescreens", () => Chosen(T("{0} écrans bleus ou plus = problème", WindowsHealthModule.BlueScreensProblem))),
        ("M02.disk-errors", () => T("Événements {0} du journal Système : liste choisie par MAUS, sans source publiée.", string.Join(", ", WindowsHealthModule.DiskIds))),
        ("M02.disk-space", () => Chosen(T("moins de {0} Go libres", WindowsHealthModule.MinimumFreeBytes / (1024 * 1024 * 1024)))),
        ("M03.windows-support", () => Chosen(T("fin de support dans moins de {0} jours", WindowsUpdateModule.WarningDaysBeforeEndOfService))),
        ("M03.last-install", () => Chosen(T("aucun correctif depuis {0} jours", WindowsUpdateModule.MaxInstallAgeDays))),
        ("M03.last-search", () => Chosen(T("aucune recherche depuis {0} jours", WindowsUpdateModule.MaxSearchAgeDays))),
        ("M03.failed-updates", () => Chosen(T("{0} échecs ou plus d'une même mise à jour de Windows sans réussite ensuite, encore proposée par Windows (si la liste n'est pas lue : aucune autre mise à jour de Windows installée depuis) ; définitions de Microsoft Defender jamais comptées ; échecs des {1} derniers jours", WindowsUpdateModule.LoopFailureCount, WindowsUpdateModule.RecentFailureDays))),
        ("M03.defender-signatures", () => Chosen(T("définitions de Microsoft Defender de plus de {0} jours", WindowsUpdateModule.MaxSignatureAgeDays))),
        ("M08.bios-age", () => Chosen(T("BIOS de plus de {0} jours", BiosModule.OldBiosDays))),
        ("M08.secure-boot-events", () => Chosen(T("événements des {0} derniers jours", BiosModule.EventWindowDays))),
        ("M09.driver.*", () => Chosen(T("pilote de plus de {0} jours, ou plus de {1} jours derrière le dernier ; catalogue jugé ancien après {2} jours", 183, 90, 45))),
        ("M09.resizable-bar.*", () => Chosen(T("fenêtre mémoire de plus de {0} Mio = Resizable BAR actif", GpuDriverModule.ResizableBarMinMiB))),
        ("M10.xmp", () => Chosen(T("écart toléré de {0} MT/s entre deux vitesses", MemorySpeeds.Tolerance))),
        ("M11.disk-*-reliability", () => Chosen(T("usure à partir de {0} % (pour un SSD NVMe, MAUS se fie à l'usure donnée par le disque lui-même), lecture de plus de {1} s",HardwareHealthModule.WearWarningPercent, HardwareHealthModule.ReadLatencyProblemMs / 1000))),
        ("M11.free-space", () => Sourced(T("moins de {0:0} % d'espace libre", HardwareHealthModule.FreeSpaceWarningRatio * 100), T("Microsoft Learn, commande defrag : il en faut au moins 15 % pour défragmenter complètement un volume"))),
        ("M11.firmware-throttling", () => Chosen(T("événement {0} sur {1} jours", HardwareHealthModule.FirmwareThrottlingEventId, HardwareHealthModule.FirmwareThrottlingDays))),
        ("M16.gpu-temperature.*", () => Chosen(T("{0} °C sous le seuil de ralentissement de la carte, au repos (moins de {1} % d'utilisation)", 15, WorkshopModule.GpuIdlePercent))),
        ("M18.wifi", () => Chosen(T("signal Wi-Fi sous {0} %", NetworkModule.WeakSignalPercent))),
        ("M18.ethernet.*", () => Chosen(T("liaison sous {0} Gb/s ; orange à {1} Mb/s ou moins", NetworkModule.Gigabit / 1_000_000_000, NetworkModule.VerySlowLink / 1_000_000))),
        ("M19.restore-points", () => Chosen(T("point de restauration de moins de {0} jours", BackupModule.RecentRestorePointDays))),
        ("M20.software-updates", () => T("Logiciels « exposés » (navigateurs, PDF, compression, lecteurs, messageries, Java) : liste choisie par MAUS, sans source publiée.")),
    ];

    public static IEnumerable<string> Keys => Notes.Select(n => n.Key);

    public static string? For(string findingId)
    {
        foreach (var (key, note) in Notes)
        {
            if (Matches(key, findingId))
            {
                return note();
            }
        }

        return null;
    }

    private static bool Matches(string key, string id)
    {
        var star = key.IndexOf('*', StringComparison.Ordinal);
        return star < 0
            ? string.Equals(key, id, StringComparison.Ordinal)
            : id.StartsWith(key[..star], StringComparison.Ordinal) && id.EndsWith(key[(star + 1)..], StringComparison.Ordinal);
    }

    private static string Chosen(string threshold) => T("Seuil : {0}. Valeur choisie par MAUS, sans source publiée.", threshold);

    private static string Sourced(string threshold, string source) => T("Seuil : {0}. Source : {1}.", threshold, source);
}
