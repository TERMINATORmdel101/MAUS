using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Modules.M05Power;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Ce que le réglage du démarrage rapide fait aujourd'hui sur ce PC.</summary>
public enum FastStartupSetting
{
    Unknown,
    Active,
    Inactive,
}

/// <summary>
/// Le dernier vrai redémarrage, dit à côté de « Pourquoi mon PC est lent ? » : un fait daté, sans seuil, sans durée (elle compterait
/// la veille) et sans lien affirmé avec la lenteur.
/// </summary>
/// <param name="RestartedAt">Dernier démarrage complet de Windows.</param>
/// <param name="FastStartedAt">Dernier allumage, si Windows l'a noté comme un démarrage rapide.</param>
public sealed record LastRestart(DateTime? RestartedAt, DateTime? FastStartedAt, FastStartupSetting Setting)
{
    internal const string StartProvider = "Microsoft-Windows-Kernel-General";
    internal const int StartEventId = 12;
    internal const string BootProvider = "Microsoft-Windows-Kernel-Boot";
    internal const int BootEventId = 27;
    internal const string BootQuery = "SELECT LastBootUpTime FROM Win32_OperatingSystem";

    public string Text
    {
        get
        {
            var parts = new List<string>
            {
                RestartedAt is { } restarted
                    ? T("Dernier vrai redémarrage de Windows (démarrage complet) : le {0}.", restarted.ToString("g", Culture))
                    : T("La date du dernier vrai redémarrage de Windows n'a pas pu être lue."),
            };

            if (FastStartedAt is { } fast)
            {
                parts.Add(T("Le dernier allumage, le {0}, est noté par Windows comme un démarrage rapide : ce n'était pas un démarrage complet.", fast.ToString("g", Culture)));
            }

            parts.Add(Setting switch
            {
                FastStartupSetting.Active => T("Le démarrage rapide est activé sur ce PC : éteindre puis rallumer ne remet pas tout Windows à zéro, « Redémarrer » si."),
                FastStartupSetting.Inactive => T("Le démarrage rapide n'agit pas sur ce PC : éteindre puis rallumer fait aussi un démarrage complet."),
                _ => T("L'état du démarrage rapide n'a pas pu être lu : s'il est activé, éteindre puis rallumer ne remet pas tout Windows à zéro, « Redémarrer » si."),
            });
            parts.Add(T("Rien ne prouve que cela joue sur la lenteur. Pour essayer, sans gain promis : choisissez « Redémarrer », puis voyez si la lenteur continue."));
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Kernel-General 12 : « le système d'exploitation a démarré », sur Microsoft Learn. Sur le PC du porteur, il manque au seul allumage
    /// noté Kernel-Boot 27 avec BootType 1 (3/10/2026), que Kernel-Power 42 précède avec TargetState 6 et EffectiveState 5 : arrêt demandé,
    /// veille prolongée faite, la définition du démarrage rapide selon Microsoft. La valeur de BootType n'est pas publiée par Microsoft :
    /// seule la valeur 1 est utilisée, et son absence ne dit rien. Journal trop court : la date de WMI, définie comme le dernier redémarrage.
    /// </summary>
    public static LastRestart Read(IEventLogReader logs, ICimReader cim, IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(cim);
        ArgumentNullException.ThrowIfNull(registry);

        var restarted = Latest(logs, StartProvider, StartEventId)?.TimeCreated ?? BootTime(cim);
        var boot = Latest(logs, BootProvider, BootEventId);
        var fast = boot is not null && IsFastStartup(boot.Data.GetValueOrDefault("BootType")) && (restarted is null || boot.TimeCreated > restarted)
            ? boot.TimeCreated
            : (DateTime?)null;
        return new LastRestart(restarted, fast, ReadSetting(registry));
    }

    /// <summary>Le lecteur rend les événements du plus récent au plus ancien.</summary>
    private static EventRecordInfo? Latest(IEventLogReader logs, string provider, int id) =>
        SystemLogQuery.Run(logs, provider, [id], DateTime.MinValue).Events is [var latest, ..] ? latest : null;

    private static DateTime? BootTime(ICimReader cim)
    {
        try
        {
            return cim.Query(BootQuery) is [var row, ..] ? row.GetDateTime("LastBootUpTime") : null;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or ManagementException or COMException)
        {
            return null;
        }
    }

    private static bool IsFastStartup(string? bootType)
    {
        var text = bootType?.Trim();
        if (text is null)
        {
            return false;
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex == 1
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number == 1;
    }

    /// <summary>Même lecture que le constat M05.fast-startup : sans veille prolongée, le réglage n'a pas d'effet. Valeur absente : non lu.</summary>
    private static FastStartupSetting ReadSetting(IRegistryReader registry)
    {
        try
        {
            var hiberboot = registry.GetDword(RegistryHive.LocalMachine, PowerModule.SessionPowerKey, "HiberbootEnabled");
            var hibernate = registry.GetDword(RegistryHive.LocalMachine, PowerModule.PowerKey, "HibernateEnabled");
            return hiberboot switch
            {
                null => FastStartupSetting.Unknown,
                0 => FastStartupSetting.Inactive,
                _ => hibernate == 0 ? FastStartupSetting.Inactive : FastStartupSetting.Active,
            };
        }
        catch (MausAccessDeniedException)
        {
            return FastStartupSetting.Unknown;
        }
    }
}
