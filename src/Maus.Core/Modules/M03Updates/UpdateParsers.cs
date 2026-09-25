using System.Globalization;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M03Updates;

/// <summary>Nature d'une mise à jour en attente, pour les décomptes du module.</summary>
internal enum PendingUpdateKind
{
    Security,
    Other,
    Optional,

    /// <summary>Définitions Defender : téléchargées seules plusieurs fois par jour, leur fraîcheur est jugée à part.</summary>
    Definitions,
    Driver,
}

/// <summary>Lecture des données brutes de Windows Update, sans accès au système.</summary>
internal static class UpdateParsers
{
    /// <summary>Classifications WUA (GUID de catégorie), voir la fiche du Module 3.</summary>
    public const string SecurityUpdatesCategory = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    public const string CriticalUpdatesCategory = "e6cf1350-c01b-414d-a61f-263d14d133b4";
    public const string DefinitionUpdatesCategory = "e0789628-ce08-4437-be74-2495b842f43b";
    public const string DriversCategory = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";

    /// <summary>Bit « analyse en temps réel active » de <c>AntiVirusProduct.productState</c> (Centre de sécurité).</summary>
    private const long ProductEnabledFlag = 0x1000;

    private static readonly string[] QfeDateFormats = ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd"];

    /// <summary>
    /// Pilotes exclus (type ou catégorie Drivers, double contrôle de la fiche), puis facultatives (<c>BrowseOnly</c>),
    /// puis définitions Defender, puis sécurité (classification Security Updates ou gravité MSRC renseignée), sinon « autre ».
    /// </summary>
    public static PendingUpdateKind Classify(PendingUpdate update)
    {
        if (update.IsDriver || HasCategory(update, DriversCategory))
        {
            return PendingUpdateKind.Driver;
        }

        if (update.BrowseOnly)
        {
            return PendingUpdateKind.Optional;
        }

        if (HasCategory(update, DefinitionUpdatesCategory))
        {
            return PendingUpdateKind.Definitions;
        }

        return HasCategory(update, SecurityUpdatesCategory) || !string.IsNullOrWhiteSpace(update.MsrcSeverity)
            ? PendingUpdateKind.Security
            : PendingUpdateKind.Other;
    }

    public static bool HasCategory(PendingUpdate update, string categoryId) =>
        update.CategoryIds.Any(c => c.Trim('{', '}').Equals(categoryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Date d'installation de <c>Win32_QuickFixEngineering.InstalledOn</c> : « M/j/aaaa » sur Windows 11,
    /// parfois « aaaammjj » ou un FILETIME hexadécimal sur les systèmes plus anciens.
    /// </summary>
    public static DateOnly? ParseQfeDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, QfeDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return DateOnly.FromDateTime(date);
        }

        if (trimmed.Length == 16
            && long.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fileTime)
            && fileTime > 0)
        {
            try
            {
                return DateOnly.FromDateTime(DateTime.FromFileTimeUtc(fileTime));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Date ISO 8601 des réglages de pause (<c>PauseUpdatesExpiryTime</c>…), supposée en temps universel.</summary>
    public static DateTimeOffset? ParseIsoDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>Vrai si le produit antivirus déclaré au Centre de sécurité a sa protection en temps réel active.</summary>
    public static bool IsAntivirusEnabled(long productState) => (productState & ProductEnabledFlag) != 0;

    /// <summary>
    /// Microsoft Defender se déclare sous « Windows Defender » ou « Microsoft Defender Antivirus » ;
    /// « Bitdefender » contient aussi « defender », d'où la comparaison sur le nom complet.
    /// </summary>
    public static bool IsMicrosoftDefender(string? displayName) =>
        displayName is not null
        && (displayName.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase));

    /// <summary>Explication en clair d'un code d'erreur de l'agent Windows Update.</summary>
    public static string DescribeSearchError(int hresult)
    {
        var hint = unchecked((uint)hresult) switch
        {
            0x80070422 => T("le service Windows Update est désactivé"),
            0x80070005 => T("accès refusé"),
            0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 or 0x8024401C or 0x80244022 or 0x80240438 =>
                T("le serveur de mises à jour est injoignable (connexion Internet, proxy ou pare-feu)"),
            0x8024500C => T("l'accès à Windows Update est bloqué par une stratégie"),
            0x8024001E => T("la recherche a été interrompue par l'arrêt du service"),
            _ => null,
        };
        var code = "0x" + unchecked((uint)hresult).ToString("X8", CultureInfo.InvariantCulture);
        return hint is null ? T("code d'erreur {0}", code) : $"{hint} (code {code})";
    }
}
