using System.Globalization;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Valeur d'un réglage avec son type exact, sous une forme texte stable (journal JSON, comparaisons).
/// Une valeur absente est représentée par <c>null</c>.
/// </summary>
public sealed record SettingValue(RegistryValueKind Kind, string Data)
{
    private const char MultiStringSeparator = '\0';

    public static SettingValue Dword(int value) => new(RegistryValueKind.DWord, value.ToString(CultureInfo.InvariantCulture));

    public static SettingValue Text(string value) => new(RegistryValueKind.String, value);

    public static SettingValue Bool(bool value) => Dword(value ? 1 : 0);

    public static SettingValue Binary(byte[] value) => new(RegistryValueKind.Binary, Convert.ToHexString(value));

    /// <summary>Convertit une valeur brute lue dans le registre.</summary>
    public static SettingValue? FromRegistry(object? raw, RegistryValueKind? kind) => raw switch
    {
        null => null,
        int i => new(kind ?? RegistryValueKind.DWord, i.ToString(CultureInfo.InvariantCulture)),
        long l => new(kind ?? RegistryValueKind.QWord, l.ToString(CultureInfo.InvariantCulture)),
        string s => new(kind is RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String, s),
        string[] values => new(RegistryValueKind.MultiString, string.Join(MultiStringSeparator, values)),
        byte[] bytes => new(RegistryValueKind.Binary, Convert.ToHexString(bytes)),
        var other => new(kind ?? RegistryValueKind.String, Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>Objet attendu par <c>RegistryKey.SetValue</c> pour ce type.</summary>
    public object ToRegistryObject() => Kind switch
    {
        RegistryValueKind.DWord => unchecked((int)ParseInteger()),
        RegistryValueKind.QWord => ParseInteger(),
        RegistryValueKind.MultiString => Data.Length == 0 ? Array.Empty<string>() : Data.Split(MultiStringSeparator),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromHexString(Data),
        _ => Data,
    };

    /// <summary>Valeur booléenne (paramètres système) : tout entier non nul vaut « activé ».</summary>
    public bool AsBool() => ParseInteger() != 0;

    /// <summary>Même valeur : les entiers se comparent numériquement, le texte exactement (hors espaces de bord).</summary>
    public static bool AreEquivalent(SettingValue? left, SettingValue? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (IsInteger(left.Kind) && IsInteger(right.Kind))
        {
            return left.ParseInteger() == right.ParseInteger();
        }

        return left.Kind == right.Kind && string.Equals(left.Data.Trim(), right.Data.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Texte affiché à l'utilisateur.</summary>
    public static string Display(SettingValue? value) => value is null
        ? T("absente")
        : value.Kind == RegistryValueKind.MultiString ? value.Data.Replace(MultiStringSeparator, '|') : value.Data;

    private static bool IsInteger(RegistryValueKind kind) => kind is RegistryValueKind.DWord or RegistryValueKind.QWord;

    private long ParseInteger()
    {
        // Les DWORD sont parfois journalisés en non signé (valeurs au-delà de 2^31).
        if (long.TryParse(Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new FormatException(T("Valeur entière attendue : « {0} »", Data));
    }

    public override string ToString() => Display(this);
}
