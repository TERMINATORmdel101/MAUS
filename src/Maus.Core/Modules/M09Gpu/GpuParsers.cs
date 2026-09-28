using System.Globalization;
using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>Une ligne de <c>nvidia-smi --query-gpu=pci.bus_id,driver_version,name</c>.</summary>
internal sealed record NvidiaSmiGpu(int? BusNumber, string DriverVersion, string Name);

/// <summary>
/// Largeur du lien PCIe d'une carte NVIDIA, lue dans <c>nvidia-smi --query-gpu=pci.bus_id,pcie.link.width.current,pcie.link.width.max</c>.
/// D'après <c>nvidia-smi --help-query-gpu</c> : <c>pcie.link.width.max</c> est la largeur maximale « possible avec cette carte et
/// cette configuration du système » (limite du slot, du processeur ou du câblage du portable comprise) ; la largeur actuelle
/// « peut baisser quand la carte n'est pas utilisée ».
/// </summary>
internal sealed record NvidiaPcieLink(int? BusNumber, int? Width, int? SystemMaxWidth);

/// <summary>Taille totale de la fenêtre BAR1 d'une carte NVIDIA, lue dans <c>nvidia-smi -q -d MEMORY</c>.</summary>
internal sealed record NvidiaBar1(int? BusNumber, long TotalMiB);

/// <summary>Lecture des versions de pilotes et des sorties de <c>nvidia-smi</c>.</summary>
internal static partial class GpuParsers
{
    /// <summary>
    /// Numéro commercial NVIDIA : les cinq derniers chiffres des deux derniers champs de la version Windows
    /// (<c>32.0.16.1714</c> donne 617.14, <c>31.0.15.3598</c> donne 535.98).
    /// </summary>
    public static string? NvidiaMarketingVersion(string? windowsVersion)
    {
        var parts = windowsVersion?.Trim().Split('.');
        if (parts is not { Length: 4 } || !parts[2].All(char.IsAsciiDigit) || !parts[3].All(char.IsAsciiDigit) || parts[2].Length == 0 || parts[3].Length is 0 or > 4)
        {
            return null;
        }

        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5)
        {
            return null;
        }

        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    /// <summary>Version comparable (2 à 4 champs numériques), ou <c>null</c>.</summary>
    public static Version? ParseVersion(string? text)
    {
        var match = text is null ? null : VersionPattern().Match(text);
        return match is { Success: true } && Version.TryParse(match.Value, out var version) ? version : null;
    }

    /// <summary>Sortie CSV sans en-tête : « 00000000:01:00.0, 617.14, NVIDIA GeForce RTX 2080 Ti ».</summary>
    public static IReadOnlyList<NvidiaSmiGpu> ParseNvidiaSmiQuery(string output)
    {
        var gpus = new List<NvidiaSmiGpu>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', 3, StringSplitOptions.TrimEntries);
            if (fields.Length == 3 && ParseVersion(fields[1]) is not null)
            {
                gpus.Add(new NvidiaSmiGpu(ParseBusNumber(fields[0]), fields[1], fields[2]));
            }
        }

        return gpus;
    }

    /// <summary>Sortie CSV sans en-tête : « 00000000:01:00.0, 8, 8 » ; « [N/A] » ou « [Not Supported] » donnent <c>null</c>.</summary>
    public static IReadOnlyList<NvidiaPcieLink> ParseNvidiaPcie(string output)
    {
        static int? Width(string field) =>
            int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 32 ? value : null;

        var links = new List<NvidiaPcieLink>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length == 3 && ParseBusNumber(fields[0]) is { } bus)
            {
                links.Add(new NvidiaPcieLink(bus, Width(fields[1]), Width(fields[2])));
            }
        }

        return links;
    }

    /// <summary>Taille totale de la fenêtre BAR1 (Mio) de chaque carte, lue dans <c>nvidia-smi -q -d MEMORY</c>.</summary>
    public static IReadOnlyList<NvidiaBar1> ParseNvidiaBar1(string output)
    {
        var result = new List<NvidiaBar1>();
        int? bus = null;
        var inBar1 = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var gpu = GpuHeaderPattern().Match(line);
            if (gpu.Success)
            {
                bus = ParseBusNumber(gpu.Groups["id"].Value);
                inBar1 = false;
                continue;
            }

            if (line.StartsWith("BAR1", StringComparison.OrdinalIgnoreCase))
            {
                inBar1 = true;
                continue;
            }

            if (!inBar1)
            {
                continue;
            }

            var total = TotalPattern().Match(line);
            if (total.Success)
            {
                var value = long.Parse(total.Groups["value"].Value, CultureInfo.InvariantCulture);
                result.Add(new NvidiaBar1(bus, total.Groups["unit"].Value.StartsWith('G') ? value * 1024 : value));
                inBar1 = false;
            }
            else if (!line.StartsWith("Used", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Free", StringComparison.OrdinalIgnoreCase))
            {
                inBar1 = false;
            }
        }

        return result;
    }

    /// <summary>« 00000000:01:00.0 » donne 1 (bus PCI en hexadécimal).</summary>
    public static int? ParseBusNumber(string? pciBusId)
    {
        var match = pciBusId is null ? null : BusIdPattern().Match(pciBusId);
        return match is { Success: true } ? int.Parse(match.Groups["bus"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Génération PCIe d'après <c>DEVPKEY_PciDevice_*LinkSpeed</c> (1 = 2,5 GT/s, 2 = 5 GT/s, 3 = 8 GT/s…).</summary>
    public static string PcieGeneration(long? speed) => speed switch
    {
        1 => "Gen1 (2,5 GT/s)",
        2 => "Gen2 (5 GT/s)",
        3 => "Gen3 (8 GT/s)",
        4 => "Gen4 (16 GT/s)",
        5 => "Gen5 (32 GT/s)",
        6 => "Gen6 (64 GT/s)",
        null => T("vitesse inconnue"),
        _ => T("code de vitesse {0}", speed),
    };

    /// <summary>Échappe une chaîne pour un littéral WQL entre apostrophes.</summary>
    public static string EscapeWql(string value) => value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal);

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^GPU\s+(?<id>[0-9A-Fa-f]{4,8}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}\.[0-9A-Fa-f])\s*$")]
    private static partial Regex GpuHeaderPattern();

    [GeneratedRegex(@"^Total\s*:\s*(?<value>\d+)\s*(?<unit>[MG]iB)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalPattern();

    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{4,8}:)?(?<bus>[0-9A-Fa-f]{2}):[0-9A-Fa-f]{2}\.[0-9A-Fa-f]$")]
    private static partial Regex BusIdPattern();
}
