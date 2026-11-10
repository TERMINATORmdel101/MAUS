using System.Globalization;

namespace Maus.Core.Reporting;

/// <summary>
/// Trace d'une alerte de l'audit de la semaine restée sans réponse : la petite fenêtre se ferme seule, et Windows arrête la
/// tâche au bout de 30 minutes. Écrite avant d'afficher l'alerte et retirée dès que l'utilisateur répond ; ce qui reste est
/// dit une fois à la prochaine ouverture. Simple texte dans le profil : aucune décision n'en dépend, et un fichier abîmé ne dit rien.
/// </summary>
public sealed class MissedAlertNote(string path)
{
    public static MissedAlertNote CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "settings", "alerte-sans-reponse.txt"));

    public void Write(DateTimeOffset at, int problems)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"{at:O};{problems}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Breadcrumbs.Add("alerte de la semaine : note non écrite (" + ex.GetType().Name + ")");
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Breadcrumbs.Add("alerte de la semaine : note non retirée (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Lit la note et la retire : elle n'est dite qu'une fois ; <c>null</c> s'il n'y en a pas ou si elle est illisible.</summary>
    public (DateTimeOffset At, int Problems)? Take()
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Breadcrumbs.Add("alerte de la semaine : note illisible (" + ex.GetType().Name + ")");
            return null;
        }

        Clear();
        var parts = text.Split(';');
        return parts.Length == 2
            && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var problems)
            && problems > 0
            ? (at, problems)
            : null;
    }
}
