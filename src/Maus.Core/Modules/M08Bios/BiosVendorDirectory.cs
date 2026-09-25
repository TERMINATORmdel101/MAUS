using System.Text.RegularExpressions;
using Maus.Core.Rules;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M08Bios;

/// <summary>Fabricant connu : page officielle et outils de mise à jour du BIOS (catalogue <c>m08-bios-vendors.json</c>).</summary>
internal sealed record BiosVendor
{
    public required string Name { get; init; }

    /// <summary>Mots cherchés (mot entier, sans tenir compte de la casse) dans le fabricant WMI.</summary>
    public IReadOnlyList<string> Match { get; init; } = [];

    /// <summary>Fabricant de PC de marque uniquement (Dell, HP, Lenovo, Acer).</summary>
    public bool OemOnly { get; init; }

    public required string SupportUrl { get; init; }

    /// <summary>Recherche sur le site du fabricant ; <c>{model}</c> y est remplacé par le modèle.</summary>
    public string? SearchUrl { get; init; }

    /// <summary>Outil intégré au BIOS d'une carte mère vendue seule (EZ Flash, M-Flash, Q-Flash…).</summary>
    public string? BoardTool { get; init; }

    /// <summary>Solution de secours sans processeur ni BIOS fonctionnel (USB BIOS FlashBack…).</summary>
    public string? RescueTool { get; init; }

    /// <summary>Application du fabricant pour un PC de marque (Dell Command Update, Lenovo Vantage…).</summary>
    public string? OemTool { get; init; }
}

internal sealed record BiosVendorCatalog
{
    public IReadOnlyList<BiosVendor> Vendors { get; init; } = [];
}

/// <summary>Ce que MAUS affiche pour guider la mise à jour : fabricant, modèle, page officielle et outil.</summary>
internal sealed record BiosTarget(
    string DisplayName,
    string? Model,
    bool IsBrandedPc,
    BiosVendor? Vendor,
    string? Url,
    string Tool);

internal static partial class BiosVendorDirectory
{
    private static readonly Lazy<IReadOnlyList<BiosVendor>> Catalog =
        new(() => EmbeddedCatalog.Load<BiosVendorCatalog>("m08-bios-vendors.json").Vendors);

    /// <summary>Valeurs de remplissage laissées par les fabricants à la place d'un vrai nom.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "System manufacturer",
        "System Product Name",
        "System Version",
        "To Be Filled By O.E.M.",
        "Default string",
        "O.E.M.",
        "OEM",
        "Not Applicable",
        "Not Specified",
        "None",
        "Base Board",
        "Type1ProductConfigId",
        "Unknown",
    };

    public static IReadOnlyList<BiosVendor> Vendors => Catalog.Value;

    public static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || Placeholders.Contains(value.Trim());

    public static BiosVendor? Find(string? manufacturer, IReadOnlyList<BiosVendor> vendors)
    {
        if (IsPlaceholder(manufacturer))
        {
            return null;
        }

        return vendors.FirstOrDefault(vendor => vendor.Match.Any(word =>
            Regex.IsMatch(manufacturer!, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
    }

    /// <summary>
    /// Un PC de marque (fabricant de PC reconnu, ou portable) est identifié par <c>Win32_ComputerSystem</c> ;
    /// un PC monté est identifié par sa carte mère (<c>Win32_BaseBoard</c>).
    /// </summary>
    public static BiosTarget Resolve(
        string? systemManufacturer,
        string? systemModel,
        bool isLaptop,
        string? boardManufacturer,
        string? boardProduct,
        IReadOnlyList<BiosVendor>? vendors = null)
    {
        vendors ??= Vendors;
        var systemVendor = Find(systemManufacturer, vendors);
        var hasSystemIdentity = !IsPlaceholder(systemManufacturer) && !IsPlaceholder(systemModel);
        var branded = hasSystemIdentity && (systemVendor?.OemOnly == true || isLaptop);

        if (branded)
        {
            var model = systemModel!.Trim();
            var name = systemVendor?.Name ?? systemManufacturer!.Trim();
            var tool = systemVendor?.OemTool ?? T("l'application de mise à jour fournie par le fabricant du PC");
            return new BiosTarget($"{name} {model}", model, true, systemVendor, BuildUrl(systemVendor, model), tool);
        }

        var boardVendor = Find(boardManufacturer, vendors);
        var boardModel = IsPlaceholder(boardProduct) ? null : CleanModel(boardProduct!);
        var boardName = boardVendor?.Name ?? (IsPlaceholder(boardManufacturer) ? T("Carte mère") : boardManufacturer!.Trim());
        var display = boardProduct is not null && !IsPlaceholder(boardProduct) ? $"{boardName} {boardProduct.Trim()}" : boardName;
        var boardTool = boardVendor?.BoardTool is { } flash
            ? T("{0} depuis le BIOS, avec une clé USB formatée en FAT32", flash)
            : T("l'outil de mise à jour intégré au BIOS, avec une clé USB formatée en FAT32");
        if (boardVendor?.RescueTool is { } rescue)
        {
            boardTool += T(" ; en secours : {0}", T(rescue));
        }

        return new BiosTarget(display, boardModel, false, boardVendor, BuildUrl(boardVendor, boardModel), boardTool);
    }

    /// <summary>Page de recherche avec le modèle si le fabricant en propose une, sinon page support générale.</summary>
    public static string? BuildUrl(BiosVendor? vendor, string? model)
    {
        if (vendor is null)
        {
            return null;
        }

        return vendor.SearchUrl is { } search && !string.IsNullOrWhiteSpace(model)
            ? search.Replace("{model}", Uri.EscapeDataString(model.Trim()), StringComparison.Ordinal)
            : vendor.SupportUrl;
    }

    /// <summary>Retire le code interne entre parenthèses : « MPG Z390 GAMING PRO CARBON (MS-7B17) » devient « MPG Z390 GAMING PRO CARBON ».</summary>
    public static string CleanModel(string product)
    {
        var cleaned = ParenthesizedCode().Replace(product, string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : product.Trim();
    }

    [GeneratedRegex(@"\s*\([^)]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedCode();
}
