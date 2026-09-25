using System.Globalization;

namespace Maus.Core.Modules.M13Security;

internal enum MitigationState
{
    /// <summary>Aucune valeur, ou 0 : réglage Windows par défaut, atténuations actives.</summary>
    Default,

    /// <summary>Seuls des bits d'activation supplémentaires sont posés (par exemple 8, 72 ou 8264 selon les guides Microsoft).</summary>
    Strengthened,

    /// <summary>Bit 0 (Spectre variante 2) ou bit 1 (Meltdown) posé et couvert par le masque : atténuations coupées.</summary>
    Disabled,

    /// <summary>Bit de désactivation posé sans le masque correspondant : réglage incomplet, probablement sans effet.</summary>
    DisabledWithoutMask,
}

/// <summary>
/// Lecture de <c>FeatureSettingsOverride</c> et <c>FeatureSettingsOverrideMask</c> (Memory Management).
/// Microsoft documente « 3 / 3 » pour couper Spectre variante 2 et Meltdown ; les autres valeurs publiées ajoutent des protections.
/// </summary>
internal static class CpuMitigationOverrides
{
    public const long SpectreV2Bit = 0x1;
    public const long MeltdownBit = 0x2;

    public static MitigationState Evaluate(long? overrideValue, long? mask)
    {
        if (overrideValue is null or 0)
        {
            return MitigationState.Default;
        }

        var disabled = overrideValue.Value & (SpectreV2Bit | MeltdownBit);
        if (disabled == 0)
        {
            return MitigationState.Strengthened;
        }

        return ((mask ?? 0) & disabled) != 0 ? MitigationState.Disabled : MitigationState.DisabledWithoutMask;
    }

    /// <summary>Protections coupées par les bits de désactivation, en clair.</summary>
    public static string DescribeDisabled(long overrideValue)
    {
        var parts = new List<string>();
        if ((overrideValue & SpectreV2Bit) != 0)
        {
            parts.Add("Spectre variante 2");
        }

        if ((overrideValue & MeltdownBit) != 0)
        {
            parts.Add("Meltdown");
        }

        return string.Join(" et ", parts);
    }

    /// <summary>Valeur numérique d'une valeur de registre (DWORD, QWORD ou texte décimal ou « 0x… »), <c>null</c> si absente ou illisible.</summary>
    public static long? ToNumber(object? value) => value switch
    {
        null => null,
        int i => unchecked((uint)i),
        long l => l,
        string s when s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(s.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) => hex,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}
