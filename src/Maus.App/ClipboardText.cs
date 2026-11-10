using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App;

/// <summary>
/// Copie vers le presse-papiers : un autre programme peut le tenir ouvert un instant, ce qui ne doit jamais faire planter
/// MAUS. Le nom d'utilisateur, le nom du PC et les adresses e-mail sont masqués, sauf pour un texte déjà relu par l'utilisateur
/// ou qui n'en contient pas par construction.
/// </summary>
public static class ClipboardText
{
    public static string Busy => T("Le presse-papiers est occupé : réessayez.");

    /// <returns>Faux si le presse-papiers est occupé.</returns>
    public static bool TryCopy(string text, bool mask = true)
    {
        try
        {
            Clipboard.SetText(mask ? PrivacyFilter.ForCurrentUser().Mask(text) : text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <returns>Faux si le presse-papiers est occupé.</returns>
    public static bool TryCopyImage(BitmapSource image)
    {
        try
        {
            Clipboard.SetImage(image);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
