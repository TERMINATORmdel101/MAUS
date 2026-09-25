using System.Globalization;
using System.Text.RegularExpressions;

namespace Maus.Core.Modules.M05Power;

/// <summary>Famille d'un mode de gestion : un mode d'origine Windows ou une copie de celui-ci.</summary>
internal enum SchemeKind
{
    Other,
    Balanced,
    HighPerformance,
    PowerSaver,
    UltimatePerformance,
}

/// <summary>Mode de gestion listé par <c>powercfg /list</c>.</summary>
internal sealed record ListedScheme(Guid Guid, string Name, bool IsActive);

/// <summary>GUID documentés des modes de gestion et des modes d'alimentation (overlays) de Windows 11.</summary>
internal static partial class PowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Mode d'alimentation « Équilibré » : aucune surcouche.</summary>
    public static readonly Guid OverlayBalanced = Guid.Empty;
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid OverlayBetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>
    /// Famille d'un mode : GUID d'origine, sinon identifiant de ressource de son nom dans le registre
    /// (une copie par <c>powercfg /duplicatescheme</c> garde « @%SystemRoot%\system32\powrprof.dll,-19,Ultimate Performance »),
    /// sinon nom affiché.
    /// </summary>
    public static SchemeKind Classify(Guid scheme, string? registryFriendlyName, string? displayName)
    {
        if (scheme == Balanced)
        {
            return SchemeKind.Balanced;
        }

        if (scheme == HighPerformance)
        {
            return SchemeKind.HighPerformance;
        }

        if (scheme == PowerSaver)
        {
            return SchemeKind.PowerSaver;
        }

        if (scheme == UltimatePerformance)
        {
            return SchemeKind.UltimatePerformance;
        }

        var match = registryFriendlyName is null ? null : ResourcePattern().Match(registryFriendlyName);
        if (match is { Success: true } && int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resource))
        {
            var kind = resource switch
            {
                15 => SchemeKind.Balanced,
                13 => SchemeKind.HighPerformance,
                11 => SchemeKind.PowerSaver,
                19 => SchemeKind.UltimatePerformance,
                _ => SchemeKind.Other,
            };
            if (kind != SchemeKind.Other)
            {
                return kind;
            }
        }

        return ClassifyByName(displayName ?? registryFriendlyName);
    }

    /// <summary>Libellé français d'une famille de modes, tel qu'affiché par Windows.</summary>
    public static string Label(SchemeKind kind) => kind switch
    {
        SchemeKind.Balanced => "Utilisation normale",
        SchemeKind.HighPerformance => "Haute performance",
        SchemeKind.PowerSaver => "Économie d'énergie",
        SchemeKind.UltimatePerformance => "Performances optimales",
        _ => "Mode personnalisé",
    };

    /// <summary>Libellé d'un mode d'alimentation (surcouche), tel qu'affiché dans Paramètres > Système > Alimentation.</summary>
    public static string OverlayLabel(Guid overlay)
    {
        if (overlay == OverlayBalanced)
        {
            return "Équilibré";
        }

        if (overlay == OverlayBestPerformance)
        {
            return "Meilleures performances";
        }

        if (overlay == OverlayBetterPerformance)
        {
            return "Performances accrues";
        }

        return overlay == OverlayBestEfficiency ? "Meilleure efficacité énergétique" : $"mode inconnu ({overlay})";
    }

    /// <summary>
    /// Analyse la sortie de <c>powercfg /list</c>, quelle que soit la langue : chaque ligne utile contient un GUID,
    /// le nom entre parenthèses, puis « * » pour le mode actif.
    /// </summary>
    public static IReadOnlyList<ListedScheme> ParseList(string output)
    {
        var schemes = new List<ListedScheme>();
        foreach (var line in output.Split('\n'))
        {
            var match = ListLinePattern().Match(line.TrimEnd('\r', ' ', '\t'));
            if (match.Success && Guid.TryParse(match.Groups["guid"].Value, out var guid))
            {
                schemes.Add(new ListedScheme(guid, match.Groups["name"].Value.Trim(), match.Groups["active"].Success));
            }
        }

        return schemes;
    }

    private static SchemeKind ClassifyByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SchemeKind.Other;
        }

        if (name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase) || name.Contains("Performances optimales", StringComparison.OrdinalIgnoreCase))
        {
            return SchemeKind.UltimatePerformance;
        }

        return name.Contains("High performance", StringComparison.OrdinalIgnoreCase) || name.Contains("Haute performance", StringComparison.OrdinalIgnoreCase)
            ? SchemeKind.HighPerformance
            : SchemeKind.Other;
    }

    [GeneratedRegex(@"powrprof\.dll\s*,\s*-(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePattern();

    [GeneratedRegex(@"(?<guid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((?<name>.*)\)\s*(?<active>\*)?$")]
    private static partial Regex ListLinePattern();
}
