using System.Globalization;
using System.Management;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Description d'un contrôle du catalogue et fabrique de ses constats, pour éviter de répéter identifiant, titre et catégorie.</summary>
internal sealed record Check(string Id, string Title, string Category, Severity Severity, bool Fixable = false)
{
    public Finding Compliant(string current, string expected, string explanation) => new()
    {
        Id = Id,
        Title = Title,
        Category = Category,
        Status = FindingStatus.Ok,
        Severity = Severity,
        Current = current,
        Expected = expected,
        Explanation = explanation,
    };

    /// <summary>Écart constaté ; <paramref name="severity"/> remplace la gravité du catalogue quand l'écart est atténué.</summary>
    public Finding Deviation(string current, string expected, string explanation, string advice, Severity? severity = null)
    {
        var effective = severity ?? Severity;
        return new Finding
        {
            Id = Id,
            Title = Title,
            Category = Category,
            Status = FindingStatusExtensions.ForDeviation(effective),
            Severity = effective,
            Current = current,
            Expected = expected,
            Explanation = explanation,
            Advice = advice,
            Fixable = Fixable,
        };
    }

    /// <summary>Simple information, sans jugement (antivirus tiers, PC géré…).</summary>
    public Finding Neutral(string current, string? expected, string explanation, string? advice = null) => new()
    {
        Id = Id,
        Title = Title,
        Category = Category,
        Status = FindingStatus.Info,
        Severity = Severity.Info,
        Current = current,
        Expected = expected,
        Explanation = explanation,
        Advice = advice,
    };

    public Finding Unknown(string reason) => Finding.Unknown(Id, Title, reason, Category);

    public Finding AdminRequired() => Finding.AdminRequired(Id, Title, Category);
}

/// <summary>Résultat d'une requête CIM : lignes lues, ou raison de l'échec.</summary>
internal sealed record CimQueryResult(IReadOnlyList<CimRow>? Rows, bool AccessDenied)
{
    public CimRow? First => Rows is { Count: > 0 } rows ? rows[0] : null;

    public static CimQueryResult Run(ICimReader cim, string wql, string scope)
    {
        try
        {
            return new CimQueryResult(cim.Query(wql, scope), AccessDenied: false);
        }
        catch (MausAccessDeniedException)
        {
            return new CimQueryResult(null, AccessDenied: true);
        }
        catch (DataSourceUnavailableException)
        {
            return new CimQueryResult(null, AccessDenied: false);
        }
        catch (ManagementException)
        {
            // Requête refusée par le fournisseur (propriété inconnue d'une plateforme ancienne, service arrêté…).
            return new CimQueryResult(null, AccessDenied: false);
        }
    }
}

/// <summary>Type de démarrage d'un service, lu dans sa clé de registre (valeur <c>Start</c>).</summary>
internal sealed record ServiceStart(string Name, int? Start, bool Exists, bool Denied)
{
    /// <summary>Valeur 4 : service désactivé.</summary>
    public bool IsDisabled => Start == 4;

    /// <summary>Service désactivé ou absent : les deux cas empêchent Windows de s'en servir.</summary>
    public bool IsBroken => !Denied && (IsDisabled || !Exists);

    public string Describe()
    {
        if (Denied)
        {
            return $"{Name} : illisible";
        }

        return Exists ? $"{Name} : {Label(Start)}" : $"{Name} : absent";
    }

    public static string Label(int? start) => start switch
    {
        0 => T("démarrage du noyau"),
        1 => T("démarrage système"),
        2 => T("automatique"),
        3 => T("manuel"),
        4 => T("désactivé"),
        null => T("type non renseigné"),
        _ => "type " + start.Value.ToString(CultureInfo.InvariantCulture),
    };

    public static ServiceStart Read(IRegistryReader registry, string name)
    {
        var path = @"SYSTEM\CurrentControlSet\Services\" + name;
        try
        {
            return registry.KeyExists(RegistryHive.LocalMachine, path)
                ? new ServiceStart(name, registry.GetDword(RegistryHive.LocalMachine, path, "Start"), Exists: true, Denied: false)
                : new ServiceStart(name, null, Exists: false, Denied: false);
        }
        catch (MausAccessDeniedException)
        {
            return new ServiceStart(name, null, Exists: true, Denied: true);
        }
    }
}
