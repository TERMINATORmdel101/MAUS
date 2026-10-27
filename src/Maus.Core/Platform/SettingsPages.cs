namespace Maus.Core.Platform;

/// <summary>Adresses des pages des Paramètres de Windows que MAUS accepte d'ouvrir.</summary>
public static class SettingsPages
{
    private const string Prefix = "ms-settings:";

    private static readonly System.Buffers.SearchValues<char> PageCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    /// <summary>
    /// « ms-settings:nom-de-page » seulement : lettres minuscules, chiffres et tirets, comme toutes les pages de la liste de
    /// Microsoft Learn (« Launch Windows Settings »). Espaces, guillemets et autres caractères sont refusés : ils pourraient
    /// ajouter des arguments à explorer.exe, qui ouvre la page.
    /// </summary>
    public static bool IsValid(string? page) =>
        page is { Length: <= 100 } && page.StartsWith(Prefix, StringComparison.Ordinal)
        && page.AsSpan(Prefix.Length).IndexOfAnyExcept(PageCharacters) < 0;
}
