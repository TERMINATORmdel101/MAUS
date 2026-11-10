using System.Globalization;
using System.Text.RegularExpressions;
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

/// <summary>D'où vient une ligne de l'historique : un pilote ou une application du Store n'est pas un échec de Windows.</summary>
internal enum UpdateFamily
{
    /// <summary>Windows et les autres produits de Microsoft : tout ce qui n'est reconnu ni comme pilote, ni comme application du Store.</summary>
    Windows,
    Driver,
    Store,
}

/// <summary>Une mise à jour qui a échoué : combien de fois, dernier code noté par Windows, et si elle a réussi ensuite.</summary>
internal sealed record FailedUpdate(string UpdateId, string Title, UpdateFamily Family, int Failures, DateTime LastFailureUtc, int LastCode, DateTime? SucceededAfterUtc);

/// <summary>Ce que Windows note d'une retenue : <c>Held</c> vaut <c>null</c> s'il n'en dit rien de documenté.</summary>
internal sealed record SafeguardHold(bool? Held, string? TargetVersion, IReadOnlyList<string> Ids, IReadOnlyList<string> Reasons);

/// <summary>Une sous-clé de <c>TargetVersionUpgradeExperienceIndicators</c>, une par version visée.</summary>
internal sealed record SafeguardIndicators(string KeyName, string? Status, IReadOnlyList<string> Ids, IReadOnlyList<string> Reasons);

/// <summary>Lecture des données brutes de Windows Update, sans accès au système.</summary>
internal static partial class UpdateParsers
{
    /// <summary>Classifications WUA (GUID de catégorie), voir la fiche du Module 3.</summary>
    public const string SecurityUpdatesCategory = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    public const string DefinitionUpdatesCategory = "e0789628-ce08-4437-be74-2495b842f43b";
    public const string DriversCategory = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";

    /// <summary><c>UpdateOperation</c> : installation.</summary>
    private const int InstallOperation = 1;

    /// <summary><c>OperationResultCode</c> : réussie (2) et échec (4). « Réussie avec des erreurs » (3) et « abandonnée » (5) ne comptent pour aucun des deux.</summary>
    private const int ResultSucceeded = 2;
    private const int ResultFailed = 4;

    /// <summary>
    /// Traces des pilotes et du Store dans l'historique, relevées sur le PC du porteur le 2/11/2026 : Microsoft ne les documente pas et
    /// l'historique ne donne pas le type de la mise à jour. Le service seul ne suffit pas : celui des pilotes (8b24b027…) y livre aussi les
    /// correctifs de Windows. Ce qui n'est pas reconnu reste du côté de Windows.
    /// </summary>
    private const string DriverClient = "Device Driver Retrieval Client";
    private const string StoreService = "855e8a7c-ecb4-4ca3-b045-1dfa50104289";
    private const string StoreClientPrefix = "Acquisition;";

    /// <summary>Valeurs de <c>GStatus</c> (Microsoft Learn, « Safeguard holds for Windows ») : 0 = retenue en place, 2 = pas de retenue.</summary>
    private const string HoldInEffect = "0";
    private const string NoHold = "2";

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

    /// <summary>Application du Store, puis numéro KB (Windows), puis pilote (voir les constantes), sinon Windows.</summary>
    public static UpdateFamily ClassifyHistory(UpdateHistoryEntry entry)
    {
        if (string.Equals(entry.ServiceId?.Trim('{', '}'), StoreService, StringComparison.OrdinalIgnoreCase)
            || entry.ClientApplicationId?.StartsWith(StoreClientPrefix, StringComparison.OrdinalIgnoreCase) == true
            || StoreTitle().IsMatch(entry.Title))
        {
            return UpdateFamily.Store;
        }

        if (KbNumber().IsMatch(entry.Title))
        {
            return UpdateFamily.Windows;
        }

        return string.Equals(entry.ClientApplicationId, DriverClient, StringComparison.OrdinalIgnoreCase) || IsDriverTitle(entry.Title)
            ? UpdateFamily.Driver
            : UpdateFamily.Windows;
    }

    /// <summary>Un titre trop long pour être lu à temps (<see cref="DriverTitle"/> a un délai maximal) n'est pas reconnu comme pilote.</summary>
    internal static bool IsDriverTitle(string title)
    {
        try
        {
            return DriverTitle().IsMatch(title);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Installations en échec depuis <paramref name="sinceUtc"/>, par mise à jour : d'abord celles qui n'ont pas réussi ensuite, puis les plus récentes.
    /// La réussite est cherchée dans tout l'historique lu. Une désinstallation ne compte pas.
    /// </summary>
    public static List<FailedUpdate> SummarizeFailures(IEnumerable<UpdateHistoryEntry> history, DateTime sinceUtc) =>
        [.. history
            .Where(e => e.Operation == InstallOperation)
            .GroupBy(e => e.UpdateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize([.. group], sinceUtc))
            .OfType<FailedUpdate>()
            .OrderBy(f => f.SucceededAfterUtc is null ? 0 : 1)
            .ThenByDescending(f => f.LastFailureUtc)];

    private static FailedUpdate? Summarize(List<UpdateHistoryEntry> attempts, DateTime sinceUtc)
    {
        var failures = attempts.Where(e => e.ResultCode == ResultFailed && e.DateUtc >= sinceUtc).ToList();
        if (failures.Count == 0)
        {
            return null;
        }

        var last = failures.MaxBy(e => e.DateUtc)!;
        var success = attempts.Where(e => e.ResultCode == ResultSucceeded && e.DateUtc > last.DateUtc).Select(e => (DateTime?)e.DateUtc).Min();
        return new FailedUpdate(last.UpdateId, last.Title, ClassifyHistory(last), failures.Count, last.DateUtc, last.Code, success);
    }

    /// <summary>
    /// Vrai si Windows propose encore cette mise à jour dans la liste lue par le même audit : même identifiant, sinon même numéro KB.
    /// </summary>
    public static bool IsStillPending(FailedUpdate failure, IReadOnlyList<PendingUpdate> pending)
    {
        if (pending.Any(p => string.Equals(p.UpdateId, failure.UpdateId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var kb = KbNumber().Match(failure.Title);
        return kb.Success && pending.Any(p => KbNumber().Match(p.Title) is { Success: true } other
            && string.Equals(other.Value, kb.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Vrai si une autre mise à jour de Windows (hors définitions de Microsoft Defender, qui arrivent plusieurs fois par jour) s'est installée
    /// après le dernier échec de celle-ci.
    /// </summary>
    public static bool OtherWindowsSuccessAfter(IEnumerable<UpdateHistoryEntry> history, FailedUpdate failure) =>
        history.Any(e => e.Operation == InstallOperation
            && e.ResultCode == ResultSucceeded
            && e.DateUtc > failure.LastFailureUtc
            && !string.Equals(e.UpdateId, failure.UpdateId, StringComparison.OrdinalIgnoreCase)
            && ClassifyHistory(e) == UpdateFamily.Windows
            && !Maus.Core.Follow.CrashTimelineReader.IsDefenderDefinitions(e.Title));

    /// <summary>
    /// Retenue selon Microsoft Learn (« Safeguard holds for Windows ») : <c>GStatus</c> de la clé GWX fait foi ; sans valeur documentée,
    /// celui des sous-clés par version. Une ancienne sous-clé ne contredit donc pas GWX. Numéro et motif ne viennent que des versions retenues.
    /// </summary>
    public static SafeguardHold ReadHold(string? gwxStatus, IReadOnlyList<SafeguardIndicators> indicators)
    {
        var status = gwxStatus?.Trim();
        var heldKeys = indicators.Where(i => i.Status?.Trim() == HoldInEffect).ToList();
        bool? held = status switch
        {
            HoldInEffect => true,
            NoHold => false,
            _ when heldKeys.Count > 0 => true,
            _ when indicators.Any(i => i.Status?.Trim() == NoHold) => false,
            _ => null,
        };
        if (held != true)
        {
            return new SafeguardHold(held, null, [], []);
        }

        var version = heldKeys.Select(i => TargetVersion().Match(i.KeyName)).FirstOrDefault(m => m.Success)?.Value;
        return new SafeguardHold(
            true,
            version,
            [.. heldKeys.SelectMany(i => i.Ids).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. heldKeys.SelectMany(i => i.Reasons).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Texte ou liste de textes du registre, sans le « None » que Windows écrit quand il n'y a rien à dire.</summary>
    public static IReadOnlyList<string> RegistryTexts(object? value)
    {
        IEnumerable<string> texts = value switch
        {
            string text => [text],
            string[] list => list,
            int or long => [Convert.ToString(value, CultureInfo.InvariantCulture)!],
            _ => [],
        };
        return [.. texts.Select(t => t.Trim()).Where(t => t.Length > 0 && !t.Equals("None", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Code d'erreur tel que Windows l'affiche, sans explication : un même code peut avoir plusieurs causes.</summary>
    public static string FormatCode(int code) => "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture);

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
    public static bool IsAntivirusEnabled(long productState) => ((productState >> 12) & 0xF) == 1; // 1 = actif, 2 = en veille, 3 = expiré

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

    /// <summary>« 2026-10 Correctif de sécurité (KB5127648) ».</summary>
    [GeneratedRegex(@"\bKB\d{5,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KbNumber();

    /// <summary>« 9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2 » : identifiant du Store, puis nom du paquet.</summary>
    [GeneratedRegex(@"^[0-9A-Z]{12}-\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex StoreTitle();

    /// <summary>« Intel - System - 10.1.16.7 », « NVIDIA Display Driver Update (32.0.16.1088) ».</summary>
    [GeneratedRegex(@"^.+ - .+ - .*\d$|\bDriver Update\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex DriverTitle();

    /// <summary>« GE24H2 » : la version visée ; le préfixe n'est pas documenté et n'est pas affiché.</summary>
    [GeneratedRegex(@"\d\d[Hh]\d$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetVersion();
}
