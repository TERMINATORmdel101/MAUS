using Maus.Core.Rules;

namespace Maus.Core.Modules.M12Startup;

/// <summary>Recommandation d'une famille d'applications au démarrage (fiche technique, Module 12).</summary>
internal enum StartupAdvice
{
    /// <summary>Désactiver sans problème (pré-coché en V0.2).</summary>
    Disable,

    /// <summary>Désactiver selon l'usage.</summary>
    DependsOnUse,

    /// <summary>Désactiver dans les réglages du navigateur.</summary>
    BrowserSettings,

    /// <summary>Garder si le service est utilisé (cloud) ; rien n'est fait par défaut.</summary>
    KeepIfUsed,

    /// <summary>Garder (pilotes et système).</summary>
    Keep,

    /// <summary>Toujours garder, jamais proposé à la désactivation (sécurité).</summary>
    AlwaysKeep,
}

internal sealed class StartupFamily
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Nom de la famille, au pluriel (catégorie affichée).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Nom d'un élément de la famille, au singulier (titre du constat).</summary>
    public string Item { get; init; } = string.Empty;

    public StartupAdvice Advice { get; init; }

    /// <summary>Ce que l'utilisateur perd en désactivant l'entrée.</summary>
    public string Loses { get; init; } = string.Empty;

    public string Recommendation { get; init; } = string.Empty;
}

internal sealed class StartupApp
{
    public string Name { get; init; } = string.Empty;

    public string Family { get; init; } = string.Empty;

    /// <summary>Morceaux du nom de l'entrée (valeur Run, raccourci), sans tenir compte de la casse.</summary>
    public List<string> Names { get; init; } = [];

    /// <summary>Noms exacts du fichier lancé.</summary>
    public List<string> Executables { get; init; } = [];

    /// <summary>Débuts de noms de paquets Store.</summary>
    public List<string> Packages { get; init; } = [];
}

/// <summary>Catalogue embarqué <c>m12-startup-catalog.json</c> : familles et applications connues.</summary>
internal sealed class StartupCatalog
{
    public List<StartupFamily> Families { get; init; } = [];

    public List<StartupApp> Apps { get; init; } = [];

    public static StartupCatalog LoadEmbedded() => EmbeddedCatalog.Load<StartupCatalog>("m12-startup-catalog.json");

    /// <summary>Première application du catalogue qui correspond à l'entrée, avec sa famille.</summary>
    public (StartupApp App, StartupFamily Family)? Match(string entryName, string? executableFileName, string? packageName)
    {
        foreach (var app in Apps)
        {
            var matches =
                app.Names.Any(n => entryName.Contains(n, StringComparison.OrdinalIgnoreCase))
                || (executableFileName is not null && app.Executables.Any(e => e.Equals(executableFileName, StringComparison.OrdinalIgnoreCase)))
                || (packageName is not null && app.Packages.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            if (matches && Families.FirstOrDefault(f => f.Id.Equals(app.Family, StringComparison.OrdinalIgnoreCase)) is { } family)
            {
                return (app, family);
            }
        }

        return null;
    }
}
