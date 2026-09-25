using System.Globalization;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory;

/// <summary>Lecture des puces SPD de toutes les barrettes.</summary>
public interface ISpdSource
{
    IReadOnlyList<SpdImage> ReadAll();
}

/// <summary>Fiche mémoire complète : barrettes (SPD), contrôleur (timings réels) et horloges.</summary>
public sealed record MemoryDetailReport(
    IReadOnlyList<SpdModule> Modules,
    LiveMemoryTimings? Live,
    MemoryClocks? Clocks,
    IReadOnlyList<string> Notes);

public static class MemoryDetails
{
    /// <summary>
    /// Lit tout ce qui est lisible ; chaque partie qui échoue devient une note, jamais une erreur bloquante.
    /// <paramref name="smn"/> et <paramref name="pm"/> sont <c>null</c> hors processeur AMD Ryzen.
    /// </summary>
    public static MemoryDetailReport Read(ISpdSource spd, ISmnReader? smn, IPmTableReader? pm, bool? ddr5Hint)
    {
        var notes = new List<string>();
        var modules = new List<SpdModule>();
        try
        {
            foreach (var image in spd.ReadAll())
            {
                if (SpdDecoder.Decode(image) is { } module)
                {
                    modules.Add(module);
                    if (!module.ChecksumOk)
                    {
                        notes.Add(T("Barrette {0} : somme de contrôle SPD incorrecte, valeurs à prendre avec prudence (lecture perturbée ou SPD modifiée).", module.Slot + 1));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add(T("Puces SPD illisibles : {0}", ex.Message));
        }

        if (modules.Count == 0)
        {
            notes.Add(T("Aucune puce SPD lue : certaines cartes mères bloquent l'accès au bus SMBus, ou le pilote PawnIO n'est pas installé."));
        }

        var ddr5 = modules.Count > 0 ? modules[0].MemoryType == "DDR5" : ddr5Hint ?? false;
        LiveMemoryTimings? live = null;
        MemoryClocks? clocks = null;
        if (smn is null)
        {
            notes.Add(T("Timings réels : lisibles seulement sur processeur AMD Ryzen. Sur Intel, le contrôleur mémoire n'est pas accessible par PawnIO ; MAUS affiche les timings des profils SPD."));
        }
        else
        {
            try
            {
                live = ZenMemoryController.ReadTimings(smn, ddr5);
                if (live is null)
                {
                    notes.Add(T("Timings réels illisibles : processeur non reconnu, ou pilote PawnIO absent."));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add(T("Timings réels illisibles : {0}", ex.Message));
            }
        }

        if (pm is not null)
        {
            try
            {
                clocks = ZenMemoryController.ReadClocks(pm);
                if (clocks is null)
                {
                    notes.Add(T("FCLK et UCLK : version de la table d'énergie du processeur inconnue de MAUS."));
                }
                else if (clocks.GenericLayout)
                {
                    notes.Add(T("FCLK et UCLK : table d'énergie lue avec la disposition générique de sa famille (version 0x{0:X6}), valeurs à vérifier.", clocks.TableVersion));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add(T("FCLK et UCLK illisibles : {0}", ex.Message));
            }
        }

        return new MemoryDetailReport(modules, live, clocks, notes);
    }

    /// <summary>Ce que mesure un timing, en une phrase ; <c>null</c> pour les réglages très techniques.</summary>
    public static string? Describe(string key) => key switch
    {
        "tCL" => T("Latence CAS : cycles entre la demande de lecture et l'arrivée des données. Le plus connu des timings."),
        "tRCD" or "tRCDRD" => T("Délai entre l'ouverture d'une ligne et la première lecture dans cette ligne."),
        "tRCDWR" => T("Délai entre l'ouverture d'une ligne et la première écriture dans cette ligne."),
        "tRP" => T("Temps pour refermer une ligne avant d'en ouvrir une autre dans la même banque."),
        "tRAS" => T("Durée minimale pendant laquelle une ligne reste ouverte."),
        "tRC" => T("Cycle complet d'une banque : ouverture, accès et fermeture (environ tRAS + tRP)."),
        "tRRDS" => T("Délai entre deux ouvertures de lignes dans des groupes de banques différents."),
        "tRRDL" => T("Délai entre deux ouvertures de lignes dans le même groupe de banques."),
        "tFAW" => T("Fenêtre dans laquelle quatre ouvertures de lignes au plus sont permises."),
        "tCCDL" => T("Délai entre deux lectures ou écritures dans le même groupe de banques."),
        "tCCDL_WR" or "tCCDL_WR2" => T("Délai entre deux écritures dans le même groupe de banques."),
        "tWTRS" => T("Délai entre une écriture et une lecture dans des groupes de banques différents."),
        "tWTRL" => T("Délai entre une écriture et une lecture dans le même groupe de banques."),
        "tWR" => T("Temps de récupération après une écriture, avant de refermer la ligne."),
        "tRTP" => T("Délai entre une lecture et la fermeture de la ligne."),
        "tCWL" => T("Latence d'écriture : l'équivalent de la latence CAS pour les écritures."),
        "tRFC" or "tRFC2" or "tRFC4" or "tRFCsb" => T("Durée d'un rafraîchissement : la mémoire est indisponible pendant ce temps. Dépend de la densité des puces."),
        "tREFI" => T("Intervalle entre deux rafraîchissements : plus il est long, moins la mémoire est interrompue (mais plus elle chauffe)."),
        "tRDRDSCL" or "tWRWRSCL" => T("Délai entre deux lectures (ou deux écritures) successives dans le même groupe de banques."),
        "tRDRDSC" or "tRDRDSD" or "tRDRDDD" or "tWRWRSC" or "tWRWRSD" or "tWRWRDD" => T("Délais entre lectures (ou écritures) successives : même rang, rang différent ou barrette différente."),
        "tRDWR" => T("Délai pour passer d'une lecture à une écriture."),
        "tWRRD" => T("Délai pour passer d'une écriture à une lecture sur un autre rang ou une autre barrette."),
        "tCKE" or "tXP" => T("Délais de mise en veille et de réveil de la mémoire."),
        _ => null,
    };

    /// <summary>Rapport texte complet, à copier dans un forum ou à comparer avant/après un réglage.</summary>
    public static string ToText(MemoryDetailReport report)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.AppendLine(value);
        string Timings(IEnumerable<MemoryTiming> timings) => string.Join("  ", timings.Select(t =>
            t.Nanoseconds is { } ns
                ? string.Create(CultureInfo.InvariantCulture, $"{t.Key} {t.Clocks} ({ns:0.#} ns)")
                : string.Create(CultureInfo.InvariantCulture, $"{t.Key} {t.Clocks}")));

        Line(T("Fiche mémoire (MAUS)"));
        if (report.Clocks is { } clocks)
        {
            Line(T("Horloges : FCLK {0} · UCLK {1} · MCLK {2} ({3})", Mhz(clocks.FclkMhz), Mhz(clocks.UclkMhz), Mhz(clocks.MclkMhz), clocks.UclkMode ?? "?"));
            Line(T("Tensions : SoC {0} · VDDP {1} · VDDG IOD {2} · VDDG CCD {3}", Volts(clocks.SocVolts), Volts(clocks.VddpVolts), Volts(clocks.VddgIodVolts), Volts(clocks.VddgCcdVolts)));
        }

        if (report.Live is { } live)
        {
            Line();
            Line(T("Timings réels ({0}, {1} MT/s, canaux {2}) :", live.Ddr5 ? "DDR5" : "DDR4", live.SpeedMts, string.Join(", ", live.Channels.Select(c => (char)('A' + c)))));
            Line(T("Réglages : GDM {0} · commande {1} · Power Down {2} · BGS {3} · BGS Alt {4} · rafraîchissement {5}",
                OnOff(live.Settings.GearDownMode), live.Settings.Command2T ? "2T" : "1T", OnOff(live.Settings.PowerDown),
                OnOff(live.Settings.BankGroupSwap), OnOff(live.Settings.BankGroupSwapAlt), live.Settings.RefreshMode));
            foreach (var group in Enum.GetValues<TimingGroup>())
            {
                Line(GroupName(group) + " : " + Timings(live.Timings.Where(t => t.Group == group)));
            }
        }

        foreach (var module in report.Modules)
        {
            Line();
            Line(T("Barrette {0} : {1} {2}, {3} {4}, {5}", module.Slot + 1, module.ModuleManufacturer ?? "?", module.PartNumber,
                Gb(module.CapacityBytes), module.MemoryType, module.ModuleType));
            Line(T("Puces : {0} ({1}), {2}, {3} groupes de {4} banques", module.DramManufacturer ?? "?", module.Organization, module.Ecc ? "ECC" : T("sans ECC"), module.BankGroups, module.BanksPerGroup));
            foreach (var profile in module.Profiles)
            {
                Line(T("  {0} : {1} MT/s, {2}{3}", ProfileName(profile), profile.SpeedMts, profile.Summary, profile.Vdd is { } v ? string.Create(CultureInfo.InvariantCulture, $", {v:0.00} V") : string.Empty));
                Line("    " + Timings(profile.Timings));
            }
        }

        foreach (var note in report.Notes)
        {
            Line();
            Line("• " + note);
        }

        return text.ToString();
    }

    public static string GroupName(TimingGroup group) => group switch
    {
        TimingGroup.Primary => T("Primaires"),
        TimingGroup.Secondary => T("Secondaires"),
        _ => T("Tertiaires"),
    };

    public static string ProfileName(SpdProfile profile) => profile.Kind switch
    {
        ProfileKind.Jedec => T("JEDEC (standard)"),
        ProfileKind.Xmp => T("XMP {0} n° {1}", profile.Version ?? string.Empty, profile.Number) + (profile.Name is { } name ? $" « {name} »" : string.Empty),
        _ => T("EXPO n° {0}", profile.Number),
    };

    private static string OnOff(bool value) => value ? T("activé") : T("désactivé");

    private static string Mhz(double? value) => value is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + " MHz" : "?";

    private static string Volts(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) + " V" : "?";

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " Go";
}
