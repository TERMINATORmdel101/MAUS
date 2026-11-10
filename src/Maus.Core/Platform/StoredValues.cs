using System.Globalization;
using System.Text;

namespace Maus.Core.Platform;

/// <summary>
/// Ce qui est relu d'un fichier que l'utilisateur ou un programme peut modifier : une valeur impossible n'est pas montrée comme
/// mesurée. Le fichier, lui, n'en est pas réécrit pour autant.
/// </summary>
public static class StoredValues
{
    /// <summary>Une horloge un peu en retard, ou un fuseau mal réglé, ne doit rien faire disparaître.</summary>
    private static readonly TimeSpan ClockMargin = TimeSpan.FromDays(2);

    /// <summary>Date que MAUS a pu écrire, quelle que soit l'horloge du jour : MAUS n'existait pas avant 2000.</summary>
    public static bool IsPossibleDate(DateTimeOffset at) => at.Year is >= 2000 and < 2200;

    /// <summary>Date à montrer : possible, et pas encore à venir. Une ligne datée de 2090 cacherait toutes les vraies.</summary>
    public static bool IsPastDate(DateTimeOffset at) => IsPossibleDate(at) && at <= DateTimeOffset.Now.Add(ClockMargin);

    public static bool IsWithin(double value, double lowest, double highest) => double.IsFinite(value) && value >= lowest && value <= highest;

    /// <summary>La valeur si elle est possible, sinon <c>null</c> : « non lu », jamais un chiffre faux.</summary>
    public static double? Within(double? value, double lowest, double highest) => value is { } read && IsWithin(read, lowest, highest) ? read : null;

    /// <summary>Texte qui s'affiche tel quel : pas trop long, sans caractère de contrôle ni d'inversion du sens d'écriture.</summary>
    public static bool IsPrintable(string text, int longest)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= longest && !text.Any(IsHidden);
    }

    /// <summary>Le texte sans ce qui ne s'affiche pas et raccourci ; un retour à la ligne devient une espace. Un texte déjà propre reste le même.</summary>
    public static string Text(string? text, int longest)
    {
        if (text is null)
        {
            return string.Empty;
        }

        if (IsPrintable(text, longest))
        {
            return text;
        }

        var clean = new StringBuilder(Math.Min(text.Length, longest));
        foreach (var c in text)
        {
            if (clean.Length >= longest)
            {
                break;
            }

            if (!IsHidden(c))
            {
                clean.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                clean.Append(' ');
            }
        }

        // Un caractère coupé en deux ne s'écrirait plus dans un fichier JSON.
        if (clean.Length > 0 && char.IsHighSurrogate(clean[^1]))
        {
            clean.Length--;
        }

        return clean.ToString().Trim();
    }

    private static bool IsHidden(char c) => char.GetUnicodeCategory(c)
        is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
