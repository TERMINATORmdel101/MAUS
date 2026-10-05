using System.Text.RegularExpressions;

namespace Maus.Core.Reporting;

/// <summary>
/// Adresses web écrites dans les textes des constats (« Page officielle : https://… ») : l'interface les remplace par le
/// seul nom du site, plus lisible, et propose un bouton qui ouvre l'adresse complète.
/// </summary>
public static partial class LinkText
{
    /// <summary>Première adresse http(s) du texte, ou <c>null</c>.</summary>
    public static Uri? FirstLink(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = Url().Match(text);
        return match.Success && Uri.TryCreate(Trim(match.Value), UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Le texte, chaque adresse remplacée par le nom du site (« www.msi.com »).</summary>
    public static string? Shorten(string? text) => text is null
        ? null
        : Url().Replace(text, match =>
        {
            var url = Trim(match.Value);
            var rest = match.Value[url.Length..];
            return (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url) + rest;
        });

    /// <summary>La ponctuation qui termine une phrase ne fait pas partie de l'adresse.</summary>
    private static string Trim(string url) => url.TrimEnd('.', ',', ';', ':', ')', '!', '?');

    [GeneratedRegex(@"https?://[^\s«»""<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
