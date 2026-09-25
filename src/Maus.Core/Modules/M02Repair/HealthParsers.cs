using System.Globalization;
using System.Text.RegularExpressions;

namespace Maus.Core.Modules.M02Repair;

/// <summary>Lecture des données brutes des journaux et des fichiers de vidage, sans accès au système.</summary>
internal static partial class HealthParsers
{
    /// <summary>Codes d'arrêt fréquents : nom officiel et piste à suivre pour un non-spécialiste.</summary>
    private static readonly Dictionary<long, (string Name, string Hint)> KnownBugchecks = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "souvent un pilote ou la mémoire vive"),
        [0x1A] = ("MEMORY_MANAGEMENT", "souvent la mémoire vive : profil XMP/EXPO ou barrette défaillante (Module 10)"),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "souvent un pilote"),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "souvent la mémoire vive (Module 10) ou un pilote"),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "souvent un pilote"),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "un pilote qui gère mal la mise en veille"),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "un pilote défaillant"),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "fichiers système ou disque abîmés : la réparation DISM puis SFC est indiquée"),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "processeur bloqué, souvent un overclocking ou un undervolting instable (Module 15)"),
        [0x116] = ("VIDEO_TDR_FAILURE", "pilote ou carte graphique (Module 9)"),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "pilote ou carte graphique (Module 9)"),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "erreur matérielle : overclocking, tension ou température (Module 15)"),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "souvent le pilote ou le micrologiciel du disque SSD"),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "souvent un pilote ou la mémoire vive"),
    };

    /// <summary>
    /// Code d'arrêt d'un événement Kernel-Power 41 (<c>BugcheckCode</c>, en décimal) ou WER 1001
    /// (<c>param1</c>, en hexadécimal suivi des paramètres entre parenthèses).
    /// </summary>
    public static long? ParseBugcheckCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var token = text.Trim().Split([' ', '('], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }

        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    /// <summary>Vrai si la valeur (décimale ou hexadécimale) est présente et non nulle, par exemple <c>PowerButtonTimestamp</c>.</summary>
    public static bool IsNonZero(string? text) => ParseBugcheckCode(text) is { } value && value != 0;

    /// <summary>Code d'arrêt au format de l'écran bleu, par exemple « 0x0000003B (SYSTEM_SERVICE_EXCEPTION) ».</summary>
    public static string DescribeBugcheck(long code)
    {
        var hex = "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        return KnownBugchecks.TryGetValue(code, out var known) ? $"{hex} ({known.Name})" : hex;
    }

    /// <summary>Piste de diagnostic pour un code d'arrêt connu, sinon <c>null</c>.</summary>
    public static string? BugcheckHint(long code) => KnownBugchecks.TryGetValue(code, out var known) ? known.Hint : null;

    /// <summary>Date d'un minidump d'après son nom Windows (« MMjjaa-nnnn-nn.dmp »), sinon <c>null</c>.</summary>
    public static DateOnly? ParseMinidumpDate(string path)
    {
        var match = MinidumpName().Match(Path.GetFileName(path));
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "MMddyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Nombre réel quel que soit le type CIM (<c>real32</c>, <c>real64</c>, entier ou chaîne).</summary>
    public static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int or long or uint or ulong or short or ushort or byte => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    [GeneratedRegex(@"^(\d{6})-\d+-\d+\.dmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinidumpName();
}
