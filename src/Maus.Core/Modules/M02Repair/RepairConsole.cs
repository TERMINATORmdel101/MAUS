using System.Text;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>
/// Réparation officielle des fichiers de Windows, lancée directement par le moteur, sans cmd.exe : <c>DISM /RestoreHealth</c>
/// (répare la copie de référence à partir de Windows Update) puis <c>SFC /scannow</c> (remplace les fichiers système abîmés),
/// dans l'ordre conseillé par Microsoft. Lancée seulement par l'utilisateur, jamais par l'audit.
/// </summary>
public static class RepairConsole
{
    private static readonly string[] DismArguments = ["/Online", "/Cleanup-Image", "/RestoreHealth"];
    private static readonly string[] SfcArguments = ["/scannow"];

    public static string Intro => T("Réparation des fichiers de Windows par les outils de Microsoft : DISM puis SFC. Ne fermez pas cette fenêtre : cela prend souvent 10 à 30 minutes.");

    public static string Done => T("Terminé. Lisez les messages ci-dessus, redémarrez le PC puis relancez l'audit de MAUS. Vous pouvez fermer cette fenêtre.");

    /// <param name="run">Lance un outil de System32 : nom du fichier, arguments, encodage de sa sortie ; rend son code de sortie.</param>
    /// <param name="line">Reçoit le texte à afficher.</param>
    /// <returns>Le résultat à afficher. SFC est lancé même si DISM échoue, comme avant.</returns>
    public static async Task<string> RunAsync(Func<string, IReadOnlyList<string>, Encoding, Task<int>> run, Action<string> line)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(line);

        line(Intro);
        var dism = await run("Dism.exe", DismArguments, ReadOnlyCommandRunner.ToolOutputEncoding).ConfigureAwait(false);

        // SFC écrit en UTF-16 quand sa sortie ne va pas à une console, contrairement aux autres outils.
        await run("sfc.exe", SfcArguments, Encoding.Unicode).ConfigureAwait(false);
        return dism == 0 ? Done : T("DISM a fini avec le code {0} : lisez son message ci-dessus.", dism) + " " + Done;
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
