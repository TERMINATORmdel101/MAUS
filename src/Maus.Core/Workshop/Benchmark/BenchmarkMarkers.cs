using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Benchmark;

/// <summary>
/// Repère de l'historique : ce qui a changé entre deux passes qui se suivent, valeurs à l'appui. Il dit ce qui a changé, jamais
/// pourquoi les points ont varié.
/// </summary>
/// <param name="DriverFrom">Ancienne version du pilote graphique ; <c>null</c> si le pilote n'a pas changé.</param>
/// <param name="DriverTo">Nouvelle version du pilote graphique ; <c>null</c> si le pilote n'a pas changé.</param>
/// <param name="BuildFrom">Ancien build de Windows ; <c>null</c> si le build n'a pas changé.</param>
/// <param name="BuildTo">Nouveau build de Windows ; <c>null</c> si le build n'a pas changé.</param>
public sealed record BenchmarkMarker(string? DriverFrom, string? DriverTo, string? BuildFrom, string? BuildTo)
{
    /// <summary>La phrase à afficher, factuelle.</summary>
    public string Text
    {
        get
        {
            if (DriverTo is not null && BuildTo is not null)
            {
                return T("Pilote graphique ({0} → {1}) et build de Windows ({2} → {3}) changés entre ces deux mesures.", DriverFrom, DriverTo, BuildFrom, BuildTo);
            }

            return DriverTo is not null
                ? T("Pilote graphique changé entre ces deux mesures : {0} → {1}.", DriverFrom, DriverTo)
                : T("Build de Windows changé entre ces deux mesures : {0} → {1}.", BuildFrom, BuildTo);
        }
    }
}

/// <summary>Contexte d'une passe (version du pilote, build de Windows) et repères entre deux passes.</summary>
public static class BenchmarkMarkers
{
    private const long Mask = 0xFFFF;

    /// <summary>
    /// Compare deux passes qui se suivent. Une valeur absente (passe d'avant ces champs, lecture impossible) ne donne aucun
    /// repère : rien n'est supposé. Le pilote ne se compare que sur la même carte graphique : deux cartes ont forcément
    /// deux pilotes, et ce ne serait pas une mise à jour.
    /// </summary>
    /// <returns>Le repère, ou <c>null</c> si rien n'a changé d'après ce qui est connu.</returns>
    public static BenchmarkMarker? Between(BenchmarkReport older, BenchmarkReport newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        var sameGpu = string.Equals(older.Gpu, newer.Gpu, StringComparison.Ordinal);
        var driver = sameGpu ? Change(older.GpuDriver, newer.GpuDriver) : null;
        var build = Change(older.WindowsBuild, newer.WindowsBuild);
        return driver is null && build is null ? null : new BenchmarkMarker(driver?.From, driver?.To, build?.From, build?.To);
    }

    /// <summary>
    /// Version du pilote telle que DXGI la donne (quatre nombres de 16 bits, IDXGIAdapter::CheckInterfaceSupport), écrite comme
    /// dans le Gestionnaire de périphériques ; <c>null</c> si elle n'a pas de sens.
    /// </summary>
    public static string? DriverVersionText(long version) => version <= 0
        ? null
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(version >> 48) & Mask}.{(version >> 32) & Mask}.{(version >> 16) & Mask}.{version & Mask}");

    /// <summary>Build de Windows, avec le numéro de mise à jour s'il est connu ; <c>null</c> si le build n'est pas lisible.</summary>
    public static string? WindowsBuildText(IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var windows = WindowsInfo.Read(registry);
        if (windows.Build <= 0)
        {
            return null;
        }

        return windows.Ubr > 0 ? windows.FullBuild : windows.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string From, string To)? Change(string? older, string? newer)
    {
        var from = older?.Trim();
        var to = newer?.Trim();
        return string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.Ordinal) ? null : (from, to);
    }
}
