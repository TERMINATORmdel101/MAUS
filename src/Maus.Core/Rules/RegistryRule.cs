using Maus.Core.Fixes;
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

    /// <summary>Correction réversible proposée en cas d'écart (V0.2) ; <c>null</c> si la règle se contente de signaler.</summary>
    public RuleFix? Fix { get; init; }

    public RegistryHive ParsedHive => Hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        _ => throw new FormatException($"Ruche inconnue dans la règle {Id} : {Hive}"),
    };
}

/// <summary>
/// Correction déclarée dans le catalogue. Par défaut : écrire <see cref="RegistryRule.Value"/> en DWORD pour
/// <see cref="RuleExpectation.EqualTo"/>, supprimer la valeur pour <see cref="RuleExpectation.Absent"/>.
/// </summary>
public sealed record RuleFix
{
    /// <summary>Libellé de l'action ; à défaut, le conseil de la règle.</summary>
    public string? Title { get; init; }

    /// <summary>Valeur à écrire si elle diffère de la valeur attendue (obligatoire pour <c>OneOf</c> et <c>NotEqualTo</c>).</summary>
    public string? Value { get; init; }

    /// <summary>Type de la valeur écrite.</summary>
    public RegistryValueKind Type { get; init; } = RegistryValueKind.DWord;

    /// <summary>Supprimer la valeur au lieu de l'écrire (retour au défaut de Windows).</summary>
    public bool Delete { get; init; }

    public ChangeEffect Effect { get; init; } = ChangeEffect.Immediate;

    public bool Advanced { get; init; }

    public bool Recommended { get; init; } = true;

    public string? Gain { get; init; }

    public string? Risk { get; init; }

    public string? Warning { get; init; }

    /// <summary>Écriture à faire, ou <c>null</c> si la règle ne permet pas d'en déduire une.</summary>
    public SettingWrite? ToWrite(RegistryRule rule)
    {
        var key = SettingKey.Registry(rule.Hive, rule.Path, rule.Name);
        if (Delete || (Value is null && rule.Expect == RuleExpectation.Absent))
        {
            return new SettingWrite(key, null);
        }

        var text = Value ?? (rule.Expect == RuleExpectation.EqualTo ? rule.Value : null);
        if (text is null)
        {
            return null;
        }

        return new SettingWrite(key, Type switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord => new SettingValue(Type, text.Trim()),
            _ => new SettingValue(Type, text),
        });
    }
}
