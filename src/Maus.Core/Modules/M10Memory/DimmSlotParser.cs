using System.Text.RegularExpressions;

namespace Maus.Core.Modules.M10Memory;

/// <summary>
/// Canal mémoire d'un emplacement, lu dans <c>DeviceLocator</c> et <c>BankLabel</c>, dont le format varie selon le fabricant :
/// « ChannelA-DIMM1 », « DIMM 0 » + « P0 CHANNEL A », « DIMM_A1 », « DDR4_B2 », « Controller1-ChannelA-DIMM0 », « A1 »…
/// </summary>
internal static partial class DimmSlotParser
{
    /// <summary>Clé du canal (contrôleur et lettre, par exemple « 0A » ou « 1A »), ou <c>null</c> si le format n'est pas reconnu.</summary>
    public static string? ChannelOf(string? deviceLocator, string? bankLabel)
    {
        var locator = (deviceLocator ?? string.Empty).Trim().ToUpperInvariant();
        var bank = (bankLabel ?? string.Empty).Trim().ToUpperInvariant();
        var controller = ControllerPattern().Match($"{locator} {bank}") is { Success: true } c ? c.Groups[1].Value : "0";

        foreach (var text in new[] { locator, bank })
        {
            if (ChannelPattern().Match(text) is { Success: true } channel)
            {
                return controller + channel.Groups[1].Value;
            }

            if (SlotPattern().Match(text) is { Success: true } slot)
            {
                return controller + slot.Groups[1].Value;
            }
        }

        return ShortSlotPattern().Match(locator) is { Success: true } shortSlot ? controller + shortSlot.Groups[1].Value : null;
    }

    [GeneratedRegex(@"CONTROLLER\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerPattern();

    /// <summary>« CHANNELA », « CHANNEL A », « CHANNEL 1 ».</summary>
    [GeneratedRegex(@"CHANNEL\s*([A-H]|\d)(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    /// <summary>« DIMM_A1 », « DIMM A2 », « DIMMB1 », « DDR4_A1 », « CHA_DIMM0 » exclu.</summary>
    [GeneratedRegex(@"(?:DIMM|DDR\d)[\s_-]?([A-H])\d", RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();

    /// <summary>Emplacement nommé seulement « A1 » ou « B2 ».</summary>
    [GeneratedRegex(@"^([A-H])\d$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSlotPattern();
}
