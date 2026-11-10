using System.Globalization;

namespace Maus.Core.Reporting;

/// <summary>Recherche dans les constats, sans tenir compte des majuscules ni des accents : « securite » trouve « Sécurité ».</summary>
public static class FindingSearch
{
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Le titre, l'explication ou l'identifiant contient le texte cherché ; un texte vide laisse tout passer.</summary>
    public static bool Matches(Finding finding, string? search)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var wanted = search?.Trim();
        return string.IsNullOrEmpty(wanted) || Contains(finding.Title, wanted) || Contains(finding.Explanation, wanted) || Contains(finding.Id, wanted);
    }

    private static bool Contains(string text, string wanted) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, wanted, Loose) >= 0;
}
