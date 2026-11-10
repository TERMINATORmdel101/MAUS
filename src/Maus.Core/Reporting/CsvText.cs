using System.Globalization;

namespace Maus.Core.Reporting;

/// <summary>Règles communes des fichiers CSV de MAUS.</summary>
public static class CsvText
{
    /// <summary>Séparateur de liste de ce format régional : celui qu'Excel attend, jamais celui de la langue de MAUS.</summary>
    public static string Separator(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.ListSeparator;
    }

    /// <summary>
    /// Une cellule entre guillemets, sur une seule ligne. Un texte lu sur le PC (nom de logiciel, par exemple) qui commence
    /// par = + - @ serait exécuté comme une formule par un tableur : il est neutralisé par une apostrophe.
    /// </summary>
    public static string Cell(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var single = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var safe = single.Length > 0 && single[0] is '=' or '+' or '-' or '@' or '\t' ? "'" + single : single;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
