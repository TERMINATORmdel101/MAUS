using System.Globalization;
using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M11Health;

/// <summary>Disque physique lu dans <c>MSFT_PhysicalDisk</c>.</summary>
internal sealed record PhysicalDiskInfo(
    string DeviceId,
    string Name,
    long MediaType,
    long BusType,
    long? HealthStatus,
    long? SizeBytes,
    long? SpindleSpeed)
{
    /// <summary>SSD : <c>MediaType</c> 4 (SSD) ou 5 (SCM), bus NVMe, ou vitesse de rotation nulle (« non rotatif »).</summary>
    public bool IsSsd => MediaType is 4 or 5 || BusType == 17 || (MediaType == 0 && SpindleSpeed == 0);

    /// <summary>Disque dur : <c>MediaType</c> 3, ou type non précisé avec une vitesse de rotation connue.</summary>
    public bool IsHdd => MediaType == 3 || (MediaType == 0 && BusType != 17 && SpindleSpeed is > 0 and < uint.MaxValue);

    public string MediaLabel => (IsHdd, IsSsd, BusType) switch
    {
        (true, _, _) => T("disque dur (HDD)"),
        (_, true, 17) => "SSD NVMe",
        (_, true, 11) => "SSD SATA",
        (_, true, _) => "SSD",
        _ => T("type de disque inconnu"),
    };

    public string BusLabel => BusType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "carte SD",
        13 => "MMC",
        16 => T("Espaces de stockage"),
        17 => "NVMe",
        19 => "UFS",
        _ => T("bus inconnu"),
    };
}

/// <summary>Analyse des sorties texte et des valeurs WMI utilisées par le module 11.</summary>
internal static partial class HealthParsers
{
    /// <summary>
    /// Lit <c>fsutil behavior query DisableDeleteNotify</c>, en français comme en anglais :
    /// « NTFS DisableDeleteNotify = 0 (…) ». Renvoie <c>null</c> si la ligne NTFS est introuvable.
    /// Les anciennes versions n'affichent qu'une ligne « DisableDeleteNotify = 0 », comptée comme NTFS.
    /// </summary>
    public static int? ParseNtfsDisableDeleteNotify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        int? unprefixed = null;
        foreach (var line in output.Split('\n'))
        {
            var match = DisableDeleteNotifyLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var fileSystem = match.Groups["fs"].Value;
            if (fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (fileSystem.Length == 0)
            {
                unprefixed ??= value;
            }
        }

        return unprefixed;
    }

    /// <summary>
    /// Nombre de canaux mémoire d'après <c>DeviceLocator</c> ou <c>BankLabel</c> (« ChannelA-DIMM1 », « DIMM_B2 », « A1 »…).
    /// Sans indice exploitable : une barrette = un canal, plafonné à deux (cas des PC grand public), et l'estimation est signalée.
    /// </summary>
    public static (int Channels, bool Estimated) CountMemoryChannels(IReadOnlyList<(string? Locator, string? Bank)> modules)
    {
        if (modules.Count == 0)
        {
            return (0, true);
        }

        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locator, bank) in modules)
        {
            var letter = ChannelLetter(locator) ?? ChannelLetter(bank);
            if (letter is null)
            {
                return (Math.Min(modules.Count, 2), true);
            }

            letters.Add(letter);
        }

        return (Math.Min(letters.Count, modules.Count), false);
    }

    /// <summary>Lettre de lecteur d'une instance <c>MSFT_Partition</c> (char16 en WMI).</summary>
    public static char? DriveLetterOf(object? value)
    {
        var letter = value switch
        {
            char c => c,
            string { Length: > 0 } s => s[0],
            ushort u => (char)u,
            int i => (char)i,
            _ => '\0',
        };
        return char.IsAsciiLetter(letter) ? char.ToUpperInvariant(letter) : null;
    }

    /// <summary>Taille lisible en Go (1 Go = 1024³ octets, comme l'Explorateur).</summary>
    public static string FormatGigabytes(long bytes) =>
        (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.GetCultureInfo("fr-FR")) + " Go";

    /// <summary>Type de mémoire selon SMBIOS (<c>Win32_PhysicalMemory.SMBIOSMemoryType</c>).</summary>
    public static string? MemoryTypeLabel(long? smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    private static string? ChannelLetter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ChannelPattern().Match(text);
        if (match.Success)
        {
            return match.Groups["letter"].Value;
        }

        match = DimmSlotPattern().Match(text);
        return match.Success ? match.Groups["letter"].Value : null;
    }

    [GeneratedRegex(@"^\s*(?<fs>NTFS|ReFS)?\s*DisableDeleteNotify\s*=\s*(?<value>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisableDeleteNotifyLine();

    [GeneratedRegex(@"Channel\s*(?<letter>[A-H])(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"(?:^|[_\s-])(?:DIMM[_\s-]?)?(?<letter>[A-H])[0-3](?:$|[_\s-])", RegexOptions.IgnoreCase)]
    private static partial Regex DimmSlotPattern();
}
