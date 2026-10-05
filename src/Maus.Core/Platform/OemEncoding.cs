using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Maus.Core.Platform;

/// <summary>
/// Page de code OEM du système : celle dans laquelle les outils classiques en ligne de commande (powercfg, bcdedit, fsutil,
/// schtasks…) écrivent quand leur sortie est redirigée vers MAUS (850 sur un Windows français, 866 sur un Windows russe).
/// Elle dépend de la langue du système pour les programmes non Unicode, jamais de la langue choisie dans MAUS :
/// l'application remplace la culture du fil par celle de sa langue, qui n'a pas la même page (anglais : 437).
/// Source : fonction <c>GetOEMCP</c> de kernel32 (Microsoft Learn, « GetOEMCP function (winnls.h) »).
/// </summary>
public static partial class OemEncoding
{
    private static readonly bool ProviderRegistered = RegisterProvider();

    private static readonly Lazy<int> SystemCodePage = new(ReadSystemCodePage);

    /// <summary>Numéro de la page de code OEM du système.</summary>
    public static int CodePage => SystemCodePage.Value;

    /// <summary>Encodage de la page de code OEM du système, pour lire la sortie redirigée des outils classiques.</summary>
    public static Encoding Current => Get(CodePage);

    /// <summary>
    /// Encodage d'une page de code (les pages OEM sont fournies par <see cref="CodePagesEncodingProvider"/>). Une page
    /// inconnue donne celle de la culture invariante (437), qui garde au moins les caractères ASCII.
    /// </summary>
    public static Encoding Get(int codePage)
    {
        _ = ProviderRegistered;
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(CultureInfo.InvariantCulture.TextInfo.OEMCodePage);
        }
    }

    private static bool RegisterProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    private static int ReadSystemCodePage()
    {
        try
        {
            var page = GetOEMCP();
            if (page is > 0 and <= int.MaxValue)
            {
                return (int)page;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Hors de Windows (compilation et tests sous Linux) : pas de page OEM du système.
        }

        return CultureInfo.InvariantCulture.TextInfo.OEMCodePage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();
}
