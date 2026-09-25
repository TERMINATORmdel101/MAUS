using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>
/// Réparation officielle des fichiers de Windows, dans une fenêtre de commande visible : <c>DISM /RestoreHealth</c>
/// (répare la copie de référence à partir de Windows Update) puis <c>SFC /scannow</c> (remplace les fichiers système abîmés),
/// dans l'ordre conseillé par Microsoft. Lancée seulement par l'utilisateur, jamais par l'audit.
/// </summary>
public static class RepairConsole
{
    public const string DismCommand = "DISM.exe /Online /Cleanup-Image /RestoreHealth";
    public const string SfcCommand = "sfc.exe /scannow";

    public static string Intro => T("Réparation des fichiers de Windows par les outils de Microsoft : DISM puis SFC. Ne fermez pas cette fenêtre : cela prend souvent 10 à 30 minutes.");

    public static string Done => T("Terminé. Lisez les messages ci-dessus, redémarrez le PC puis relancez l'audit de MAUS. Vous pouvez fermer cette fenêtre.");

    /// <summary>Arguments de <c>cmd.exe</c> : <c>/k</c> garde la fenêtre ouverte pour lire le résultat.</summary>
    public static string Arguments()
    {
        var line = new StringBuilder("/k title MAUS");
        line.Append(" & echo ").Append(Escape(Intro));
        line.Append(" & echo. & ").Append(DismCommand);
        line.Append(" & echo. & ").Append(SfcCommand);
        line.Append(" & echo. & echo ").Append(Escape(Done));
        return line.ToString();
    }

    /// <summary>Texte affiché par <c>echo</c> : caractères spéciaux de cmd neutralisés, variables et guillemets retirés.</summary>
    internal static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '^' or '&' or '|' or '<' or '>' or '(' or ')':
                    escaped.Append('^').Append(c);
                    break;
                case '%' or '!':
                    break;
                case '"':
                    escaped.Append('\'');
                    break;
                case '\r' or '\n':
                    escaped.Append(' ');
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }
}
