using System.ComponentModel;
using System.Runtime.InteropServices;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Diagnostics;

/// <summary>
/// Une erreur en phrase humaine, suivie de « que faire » (0.8.6). Les exceptions de MAUS portent déjà un texte lisible : il
/// est gardé tel quel. Les erreurs de Windows et de .NET (accès refusé, fichier introuvable, disque plein, délai dépassé,
/// autorisation annulée…) sont traduites ; en mode technicien, le type et le message d'origine suivent entre crochets.
/// </summary>
public static class ErrorText
{
    /// <summary>Mode technicien : le message d'origine est ajouté entre crochets.</summary>
    public static bool Verbose { get; set; }

    public static string For(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var human = Explain(exception);
        return Verbose && !IsMaus(exception)
            ? human + " [" + exception.GetType().Name + " : " + FirstLine(exception.Message) + "]"
            : human;
    }

    /// <summary>Exception de MAUS lui-même (moteur, journal, préférences, PawnIO…) : son message est écrit pour l'utilisateur.</summary>
    private static bool IsMaus(Exception exception) =>
        (exception.GetType().Namespace ?? string.Empty).StartsWith("Maus.", StringComparison.Ordinal);

    private static string Explain(Exception exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: 1 } single => Explain(single.InnerExceptions[0]),
        _ when IsMaus(exception) => exception.Message,
        OperationCanceledException => T("Opération interrompue : rien n'a été fait."),
        Win32Exception { NativeErrorCode: 1223 } => T("Vous avez annulé la demande d'autorisation de Windows : rien n'a été fait."),
        Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException =>
            T("Windows a refusé l'accès. Que faire : cliquez sur « Toutes les fonctionnalités » si ce n'est pas fait ; sinon, un autre programme (antivirus, outil de protection) bloque peut-être ce fichier ou ce réglage."),
        Win32Exception { NativeErrorCode: 2 or 3 } or FileNotFoundException or DirectoryNotFoundException =>
            T("Un fichier ou un dossier attendu est introuvable. Que faire : si MAUS vient d'être déplacé ou mis à jour, fermez-le et rouvrez-le ; sinon, réinstallez-le."),
        Win32Exception { NativeErrorCode: 1460 } or TimeoutException =>
            T("Windows n'a pas répondu à temps. Que faire : réessayez dans un instant ; si cela continue, redémarrez le PC."),
        Win32Exception win32 => T("Windows a renvoyé une erreur (code {0}). Que faire : réessayez ; si cela continue, redémarrez le PC.", win32.NativeErrorCode),
        PathTooLongException => T("Le chemin du fichier est trop long pour Windows. Que faire : choisissez un dossier moins profond."),
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } =>
            T("Le fichier est utilisé par un autre programme. Que faire : fermez ce programme (antivirus, éditeur, explorateur) puis réessayez."),
        IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } =>
            T("Le disque est plein. Que faire : libérez de l'espace (Atelier > Stockage) puis réessayez."),
        IOException => T("Lecture ou écriture impossible sur le disque. Que faire : réessayez ; si cela continue, vérifiez l'état du disque (Atelier > Mon PC, carte Disque)."),
        COMException com => T("Un composant de Windows (WMI) a refusé la demande (code {0}). Que faire : réessayez ; si cela continue, « Réparer les fichiers de Windows » peut remettre ce composant en état.", "0x" + com.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or System.Net.WebException =>
            T("Pas de connexion à Internet, ou serveur injoignable. Que faire : vérifiez la connexion puis réessayez."),
        OutOfMemoryException => T("Plus assez de mémoire. Que faire : fermez des programmes puis réessayez."),
        NotSupportedException or PlatformNotSupportedException =>
            T("Cette opération n'est pas possible sur ce PC (version de Windows ou matériel). Rien n'a été fait."),
        InvalidOperationException => T("Cette opération n'est pas possible dans l'état actuel. Que faire : relancez l'audit, puis réessayez."),
        _ => T("Erreur imprévue ({0}). Que faire : réessayez ; si cela continue, « Demander de l'aide » prépare un résumé à partager.", exception.GetType().Name),
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 160 ? line : string.Concat(line.AsSpan(0, 159), "…");
    }
}
