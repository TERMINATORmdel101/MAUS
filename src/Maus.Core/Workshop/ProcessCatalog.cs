using Maus.Core.Rules;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Un processus connu : ce qu'il fait, en français (traduit à l'affichage).</summary>
public sealed record KnownProcess
{
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Famille affichée (« Windows », « Sécurité », « Jeux »…).</summary>
    public required string Category { get; init; }

    /// <summary>« Qu'est-ce que c'est ? » en une ou deux phrases.</summary>
    public required string What { get; init; }
}

/// <summary>Catalogue MAUS des processus courants (<c>Catalog/processes.json</c>), rédigé par MAUS.</summary>
public sealed class ProcessCatalog
{
    private static readonly Lazy<ProcessCatalog> Embedded = new(() => new ProcessCatalog(EmbeddedCatalog.Load<List<KnownProcess>>("processes.json")));

    private readonly Dictionary<string, KnownProcess> _byName;

    public ProcessCatalog(IEnumerable<KnownProcess> entries)
    {
        Entries = entries.ToList();
        _byName = Entries.SelectMany(e => e.Names.Select(n => (Name: n, Entry: e)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.OrdinalIgnoreCase);
    }

    public static ProcessCatalog Default => Embedded.Value;

    public IReadOnlyList<KnownProcess> Entries { get; }

    public KnownProcess? Find(string processName) =>
        _byName.TryGetValue(processName, out var entry) ? entry
        : _byName.TryGetValue(processName + ".exe", out entry) ? entry
        : null;

    /// <summary>Explication traduite, ou un texte générique pour un processus inconnu du catalogue.</summary>
    public (string Category, string What) Explain(string processName) => Find(processName) is { } known
        ? (T(known.Category), T(known.What))
        : (T("Non répertorié"), T("Ce programme ne figure pas dans le catalogue de MAUS. Regardez son éditeur et son emplacement, ou lancez une recherche web sur son nom exact."));
}
