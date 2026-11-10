using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>
/// Un seul gabarit pour toutes les confirmations (0.8.6) : ce qui va changer, ce qu'il faut savoir, comment revenir en
/// arrière, le rappel légal, puis la question. La boîte elle-même répond Non par défaut.
/// </summary>
public static class ConfirmationText
{
    public static string Build(string what, string? risk = null, string? undo = null, string? reminder = null)
    {
        ArgumentNullException.ThrowIfNull(what);

        var parts = new List<string> { what.TrimEnd() };
        if (!string.IsNullOrWhiteSpace(risk))
        {
            parts.Add(T("À savoir : {0}", risk));
        }

        if (!string.IsNullOrWhiteSpace(undo))
        {
            parts.Add(T("Retour en arrière : {0}", undo));
        }

        if (!string.IsNullOrWhiteSpace(reminder))
        {
            parts.Add(reminder);
        }

        parts.Add(T("Continuer ?"));
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
