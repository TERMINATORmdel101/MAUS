namespace Maus.Core.Hardware;

/// <summary>Textes que le BIOS et les pilotes donnent sur le matériel, et valeurs de remplissage à ne pas prendre pour un vrai nom.</summary>
internal static class HardwareText
{
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
        "Undefined",
    };

    public static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || Placeholders.Contains(value.Trim());

    /// <summary>Le texte sans ses espaces ni ses caractères nuls ; <c>null</c> s'il est vide ou de remplissage.</summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim().Trim('\0').Trim();
        return IsPlaceholder(trimmed) ? null : trimmed;
    }
}
