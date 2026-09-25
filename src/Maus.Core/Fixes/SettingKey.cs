using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>Nature d'un réglage modifiable.</summary>
public enum SettingKind
{
    /// <summary>Valeur de registre (HKLM ou HKCU).</summary>
    Registry,

    /// <summary>Booléen de <c>SystemParametersInfo</c> (effets visuels de la session).</summary>
    SystemParameter,

    /// <summary>Animation de réduction et d'agrandissement (<c>SPI_GETANIMATION</c> / <c>SPI_SETANIMATION</c>).</summary>
    MinimizeAnimation,

    /// <summary>Mode de gestion de l'alimentation actif (GUID).</summary>
    ActivePowerScheme,
}

/// <summary>Désigne un réglage précis que MAUS sait lire, écrire et restaurer. Sérialisé tel quel dans le journal.</summary>
public sealed record SettingKey
{
    public required SettingKind Kind { get; init; }

    /// <summary>« HKLM » ou « HKCU » (registre seulement).</summary>
    public string? Hive { get; init; }

    public string? Path { get; init; }

    public string? Name { get; init; }

    /// <summary>Code SPI_GET* (paramètre système seulement).</summary>
    public uint SpiGet { get; init; }

    /// <summary>Code SPI_SET* correspondant.</summary>
    public uint SpiSet { get; init; }

    /// <summary>La valeur SPI passe par <c>uiParam</c> au lieu de <c>pvParam</c>.</summary>
    public bool SpiUseUiParam { get; init; }

    [JsonIgnore]
    public RegistryHive RegistryHive => Hive?.ToUpperInvariant() switch
    {
        "HKLM" => RegistryHive.LocalMachine,
        "HKCU" => RegistryHive.CurrentUser,
        _ => throw new FormatException($"Ruche inconnue : {Hive}"),
    };

    /// <summary>Réglage propre à l'utilisateur de la session (HKCU ou paramètre système) : il faut écrire dans le bon profil.</summary>
    [JsonIgnore]
    public bool IsUserScoped => Kind is SettingKind.SystemParameter or SettingKind.MinimizeAnimation
        || Kind == SettingKind.Registry && string.Equals(Hive, "HKCU", StringComparison.OrdinalIgnoreCase);

    public static SettingKey Registry(string hive, string path, string name) =>
        new() { Kind = SettingKind.Registry, Hive = hive.ToUpperInvariant(), Path = path.Trim('\\'), Name = name };

    public static SettingKey Spi(uint getAction, uint setAction, bool useUiParam = false) =>
        new() { Kind = SettingKind.SystemParameter, SpiGet = getAction, SpiSet = setAction, SpiUseUiParam = useUiParam };

    public static SettingKey MinimizeAnimation { get; } = new() { Kind = SettingKind.MinimizeAnimation };

    public static SettingKey ActivePowerScheme { get; } = new() { Kind = SettingKind.ActivePowerScheme };

    /// <summary>Emplacement lisible, affiché dans l'aperçu et le journal.</summary>
    public string Describe() => Kind switch
    {
        SettingKind.Registry => $@"{Hive}\{Path}\{Name}",
        SettingKind.SystemParameter => "SystemParametersInfo 0x" + SpiSet.ToString("X4", CultureInfo.InvariantCulture),
        SettingKind.ActivePowerScheme => T("Mode de gestion de l'alimentation actif"),
        _ => "SystemParametersInfo SPI_SETANIMATION",
    };

    /// <summary>Identité du réglage, pour repérer deux écritures sur la même valeur.</summary>
    [JsonIgnore]
    public string Identity => Describe().ToUpperInvariant();

    public override string ToString() => Describe();
}
