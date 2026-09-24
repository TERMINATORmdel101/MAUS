using Microsoft.Win32;

namespace Maus.Core.Rules;

public enum RuleExpectation
{
    /// <summary>La valeur doit être absente (cas typique : une stratégie posée par un script).</summary>
    Absent,

    /// <summary>La valeur doit valoir <see cref="RegistryRule.Value"/>.</summary>
    EqualTo,

    /// <summary>La valeur ne doit pas valoir <see cref="RegistryRule.Value"/> ; absente, elle est conforme.</summary>
    NotEqualTo,

    /// <summary>La valeur doit figurer dans <see cref="RegistryRule.Values"/>.</summary>
    OneOf,
}

/// <summary>Contrôle de registre déclaratif, chargé depuis un catalogue JSON versionné.</summary>
public sealed record RegistryRule
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Category { get; init; }

    /// <summary>« HKLM » ou « HKCU ».</summary>
    public required string Hive { get; init; }

    public required string Path { get; init; }

    public required string Name { get; init; }

    public required RuleExpectation Expect { get; init; }

    public string? Value { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Pour <see cref="RuleExpectation.EqualTo"/> et <see cref="RuleExpectation.OneOf"/> : une valeur absente vaut-elle le défaut attendu ?</summary>
    public bool AbsentIsOk { get; init; }

    public Severity Severity { get; init; } = Severity.Medium;

    public required string Explanation { get; init; }

    public string? Advice { get; init; }

    /// <summary>Texte affiché pour la valeur attendue ; à défaut, il est déduit de la règle.</summary>
    public string? ExpectedLabel { get; init; }

    public RegistryHive ParsedHive => Hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        _ => throw new FormatException($"Ruche inconnue dans la règle {Id} : {Hive}"),
    };
}
