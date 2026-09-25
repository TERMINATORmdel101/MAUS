using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>Accès au registre en lecture seule.</summary>
public interface IRegistryReader
{
    /// <summary>Valeur brute, ou <c>null</c> si la clé ou la valeur est absente. Les variables d'environnement ne sont pas développées.</summary>
    /// <exception cref="MausAccessDeniedException">La clé existe mais sa lecture est refusée.</exception>
    object? GetValue(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64);

    bool KeyExists(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64);

    /// <summary>Noms des valeurs de la clé, ou liste vide si la clé est absente.</summary>
    IReadOnlyList<string> GetValueNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64);

    /// <summary>Noms des sous-clés, ou liste vide si la clé est absente.</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64);

    /// <summary>
    /// Type exact de la valeur, ou <c>null</c> si elle est absente. Nécessaire pour restaurer une valeur à l'identique
    /// (REG_SZ et REG_EXPAND_SZ se lisent tous deux comme du texte). Par défaut, le type est déduit de la valeur lue.
    /// </summary>
    RegistryValueKind? GetValueKind(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        GetValue(hive, path, name, view) switch
        {
            null => null,
            int => RegistryValueKind.DWord,
            long => RegistryValueKind.QWord,
            string => RegistryValueKind.String,
            string[] => RegistryValueKind.MultiString,
            byte[] => RegistryValueKind.Binary,
            _ => RegistryValueKind.Unknown,
        };
}

public static class RegistryReaderExtensions
{
    public static int? GetDword(this IRegistryReader registry, RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        registry.GetValue(hive, path, name, view) switch
        {
            int value => value,
            long value => unchecked((int)value),
            _ => null,
        };

    public static string? GetString(this IRegistryReader registry, RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        registry.GetValue(hive, path, name, view) switch
        {
            string value => value,
            string[] values => string.Join('|', values),
            null => null,
            var other => other.ToString(),
        };

    public static byte[]? GetBinary(this IRegistryReader registry, RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        registry.GetValue(hive, path, name, view) as byte[];

    /// <summary>Représentation texte stable d'une valeur de registre, pour comparer et afficher.</summary>
    public static string? Normalize(object? value) => value switch
    {
        null => null,
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => s.Trim(),
        string[] values => string.Join('|', values),
        byte[] bytes => Convert.ToHexString(bytes),
        var other => other.ToString(),
    };
}
