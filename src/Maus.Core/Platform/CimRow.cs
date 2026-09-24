using System.Globalization;

namespace Maus.Core.Platform;

/// <summary>Une instance CIM/WMI sous forme de propriétés nommées (insensibles à la casse).</summary>
public sealed class CimRow
{
    private readonly Dictionary<string, object?> _values;

    public CimRow(IEnumerable<KeyValuePair<string, object?>> values)
    {
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Names => _values.Keys;

    public object? this[string name] => _values.GetValueOrDefault(name);

    public string? GetString(string name) => this[name] switch
    {
        null => null,
        string s => s,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    /// <summary>Entier quel que soit le type CIM d'origine (uint8 à uint64, sint*, chaîne numérique).</summary>
    public long? GetInt64(string name) => ToInt64(this[name]);

    public bool? GetBool(string name) => this[name] switch
    {
        bool b => b,
        null => null,
        var other => ToInt64(other) is { } n ? n != 0 : null,
    };

    public DateTime? GetDateTime(string name) => this[name] as DateTime?;

    /// <summary>Tableau d'entiers (par exemple <c>ChassisTypes</c> ou <c>SecurityServicesRunning</c>), vide si absent.</summary>
    public IReadOnlyList<long> GetInt64Array(string name) => this[name] switch
    {
        Array array => array.Cast<object?>().Select(ToInt64).OfType<long>().ToArray(),
        null => [],
        var single => ToInt64(single) is { } n ? [n] : [],
    };

    public IReadOnlyList<string> GetStringArray(string name) => this[name] switch
    {
        string[] strings => strings,
        Array array => array.Cast<object?>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).OfType<string>().ToArray(),
        string single => [single],
        _ => [],
    };

    /// <summary>Objet CIM imbriqué.</summary>
    public CimRow? GetRow(string name) => this[name] as CimRow;

    /// <summary>Tableau d'objets CIM imbriqués, vide si absent.</summary>
    public IReadOnlyList<CimRow> GetRows(string name) => this[name] as CimRow[] ?? [];

    private static long? ToInt64(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        uint u => u,
        ulong ul => ul <= long.MaxValue ? (long)ul : null,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
