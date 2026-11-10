using System.Runtime.InteropServices;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M14Display;

/// <summary>Écran dont la fréquence peut monter : source GDI, résolution gardée telle quelle, fréquence actuelle et maximale.</summary>
public sealed record RefreshTarget(string SourceName, string Label, int Width, int Height, int CurrentHz, int TargetHz)
{
    /// <summary>HDR actif : à fréquence plus haute, le câble peut compresser les couleurs (4:2:2) ; à vérifier par un nouvel audit.</summary>
    public bool HdrActive { get; init; }
}

/// <summary>Réponse de Windows à un changement de mode d'affichage.</summary>
public enum DisplayChange
{
    Applied,

    /// <summary>Windows ou l'écran refuse ce mode (test préalable négatif).</summary>
    Refused,

    /// <summary>Le changement ne prendra effet qu'après un redémarrage : MAUS ne s'en sert pas pour un essai.</summary>
    NeedsRestart,

    Failed,
}

/// <summary>Change le mode d'un écran (sans droits administrateur, dans la session de l'utilisateur).</summary>
public interface IDisplayModeWriter
{
    /// <summary>Essai sans enregistrement : le mode enregistré reste l'ancien, et revient au prochain changement ou redémarrage.</summary>
    DisplayChange TryMode(string sourceName, int width, int height, int hz);

    /// <summary>Enregistre le mode essayé.</summary>
    DisplayChange Keep(string sourceName, int width, int height, int hz);

    /// <summary>Revient au mode enregistré (celui d'avant l'essai).</summary>
    DisplayChange RestoreSaved(string sourceName);
}

/// <summary>
/// Essai de la fréquence maximale avec retour automatique (fiche du Module 14 : « Écran noir ? Ne touchez à rien : l'ancien
/// réglage revient dans 15 secondes »). La résolution ne change jamais : seule la fréquence monte, au maximum que Windows
/// propose pour la résolution affichée.
/// </summary>
public sealed class RefreshRateTrial(IDisplayModeWriter writer, RefreshTarget target)
{
    /// <summary>Délai sans réponse avant le retour automatique (fiche du Module 14, comme la fenêtre de Windows).</summary>
    public static TimeSpan ConfirmDelay { get; } = TimeSpan.FromSeconds(15);

    public RefreshTarget Target => target;

    public bool Started { get; private set; }

    public bool Finished { get; private set; }

    public DisplayChange Start()
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(target);
        var result = writer.TryMode(target.SourceName, target.Width, target.Height, target.TargetHz);
        Started = result == DisplayChange.Applied;
        return result;
    }

    /// <summary>Enregistre le mode essayé ; si Windows refuse, revient aussitôt au mode enregistré.</summary>
    public DisplayChange Keep() => Finish(() =>
    {
        var kept = writer.Keep(target.SourceName, target.Width, target.Height, target.TargetHz);
        if (kept != DisplayChange.Applied)
        {
            writer.RestoreSaved(target.SourceName);
        }

        return kept;
    });

    public DisplayChange Revert() => Finish(() => writer.RestoreSaved(target.SourceName));

    private DisplayChange Finish(Func<DisplayChange> action)
    {
        if (!Started || Finished)
        {
            throw new InvalidOperationException(T("Aucun essai de fréquence en cours."));
        }

        Finished = true;
        return action();
    }

    public static string Describe(DisplayChange change) => change switch
    {
        DisplayChange.Applied => T("Fait."),
        DisplayChange.Refused => T("Windows refuse cette fréquence pour cet écran (câble, réglage de l'écran ou pilote). Rien n'a changé."),
        DisplayChange.NeedsRestart => T("Windows demande un redémarrage pour ce mode : MAUS ne l'applique pas. Rien n'a changé."),
        _ => T("Windows n'a pas pu changer le mode d'affichage. Rien n'a changé."),
    };
}

/// <summary>Écrans du dernier audit : retrouve l'écran d'un constat <c>M14.refresh.display-N</c> (même ordre que le module).</summary>
/// <summary>
/// Retour à l'ancienne fréquence d'un écran, depuis l'Historique : « Garder » n'est plus définitif (décision du porteur,
/// 0.8.6). Comme pour une correction, un réglage changé entre-temps n'est pas écrasé.
/// </summary>
public static class RefreshRateUndo
{
    /// <param name="Finished">Rien de plus à proposer : l'entrée quitte l'Historique.</param>
    /// <param name="Message">Ce qu'il faut dire à l'utilisateur.</param>
    public sealed record Outcome(bool Finished, string Message);

    public static Outcome Run(Maus.Core.Preferences.KeptRefreshRate kept) => Run(kept, new Win32DisplayConfigReader().ReadActivePaths(), new Win32DisplayModeWriter());

    internal static Outcome Run(Maus.Core.Preferences.KeptRefreshRate kept, IReadOnlyList<DisplayPath>? paths, IDisplayModeWriter writer)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(writer);

        var path = paths?.FirstOrDefault(p => string.Equals(p.SourceName, kept.SourceName, StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            return new(false, T("Cet écran n'est pas branché en ce moment : rien n'a changé."));
        }

        if (path.Width != kept.Width || path.Height != kept.Height || path.RefreshHz is not { } current || Math.Abs(current - kept.ToHz) > 1)
        {
            return new(true, T("{0} n'est plus à {1} Hz : le réglage a changé depuis, MAUS le laisse tel quel.", kept.Label, kept.ToHz));
        }

        // Note du fichier de l'utilisateur : la fréquence n'est écrite que si Windows la propose pour cet écran et cette taille.
        var listed = path.Modes
            .Where(m => IsSameSize(m, kept) && Math.Abs(m.RefreshHz - kept.FromHz) <= RefreshRateTargets.Tolerance)
            .OrderBy(m => Math.Abs(m.RefreshHz - kept.FromHz))
            .FirstOrDefault();
        if (listed is null)
        {
            return new(false, RefreshRateTrial.Describe(DisplayChange.Refused));
        }

        var change = writer.Keep(kept.SourceName, kept.Width, kept.Height, listed.RefreshHz);
        return change == DisplayChange.Applied
            ? new(true, T("{0} est revenu à {1} Hz.", kept.Label, kept.FromHz))
            : new(false, RefreshRateTrial.Describe(change));
    }

    /// <summary>Un écran tourné peut lister ses modes dans l'autre sens (voir <c>DisplayModule</c>).</summary>
    private static bool IsSameSize(DisplayMode mode, Maus.Core.Preferences.KeptRefreshRate kept) =>
        (mode.Width == kept.Width && mode.Height == kept.Height) || (mode.Width == kept.Height && mode.Height == kept.Width);
}

public static class RefreshRateTargets
{
    private const string Prefix = "M14.refresh.display-";
    /// <summary>Tolérance entre la fréquence exacte (59,94 Hz) et la valeur entière des modes (60 Hz).</summary>
    internal const double Tolerance = 1.0;

    public static bool IsRefreshFinding(string findingId) =>
        findingId is not null && findingId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Relit la configuration d'affichage (lecture seule) ; <c>null</c> si l'écran est déjà au maximum ou introuvable.</summary>
    public static RefreshTarget? Find(string findingId) => Find(new Win32DisplayConfigReader().ReadActivePaths(), findingId);

    internal static RefreshTarget? Find(IReadOnlyList<DisplayPath>? paths, string findingId)
    {
        if (paths is null || !IsRefreshFinding(findingId)
            || !int.TryParse(findingId.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || index < 1 || index > paths.Count)
        {
            return null;
        }

        var path = paths[index - 1];
        if (path.Width <= 0 || path.Height <= 0 || path.RefreshHz is not { } current || string.IsNullOrEmpty(path.SourceName)
            || DisplayParsers.MaxRefreshAt(path.Modes, path.Width, path.Height) is not { } max || current >= max - Tolerance)
        {
            return null;
        }

        return new RefreshTarget(path.SourceName, DisplayModule.Label(path, index), path.Width, path.Height, (int)Math.Round(current), max)
        {
            HdrActive = path.Color is { HdrActive: true },
        };
    }
}

/// <summary>
/// <c>ChangeDisplaySettingsExW</c> (Microsoft Learn) : test du mode (<c>CDS_TEST</c>), puis changement dynamique (drapeaux 0,
/// rien d'enregistré), enregistrement par <c>CDS_UPDATEREGISTRY</c>, et retour au mode enregistré avec un mode nul et des
/// drapeaux 0 (« the easiest way to return to the default mode after a dynamic mode change »).
/// </summary>
public sealed unsafe partial class Win32DisplayModeWriter : IDisplayModeWriter
{
    private const uint UpdateRegistry = 0x1;
    private const uint Test = 0x2;
    private const uint PelsWidth = 0x80000;
    private const uint PelsHeight = 0x100000;
    private const uint DisplayFrequency = 0x400000;
    private const int Successful = 0;
    private const int Restart = 1;
    private const int BadMode = -2;

    public DisplayChange TryMode(string sourceName, int width, int height, int hz)
    {
        if (!TryReadCurrent(sourceName, width, height, hz, out var mode))
        {
            return DisplayChange.Failed;
        }

        var test = Change(sourceName, &mode, Test);
        return test == Successful ? Map(Change(sourceName, &mode, 0)) : Map(test);
    }

    public DisplayChange Keep(string sourceName, int width, int height, int hz)
    {
        if (!TryReadCurrent(sourceName, width, height, hz, out var mode))
        {
            return DisplayChange.Failed;
        }

        var test = Change(sourceName, &mode, Test);
        return test == Successful ? Map(Change(sourceName, &mode, UpdateRegistry)) : Map(test);
    }

    public DisplayChange RestoreSaved(string sourceName) => Map(Change(sourceName, null, 0));

    /// <summary>Part du mode actuel (orientation, position, profondeur gardées) et ne change que taille et fréquence.</summary>
    private static bool TryReadCurrent(string sourceName, int width, int height, int hz, out DevModeW mode)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        mode = new DevModeW { Size = DevModeW.NativeSize };
        if (DisplayRefresh.EnumDisplaySettingsExW(sourceName, DisplayRefresh.EnumCurrentSettings, ref mode, 0) == 0)
        {
            return false;
        }

        mode.PelsWidth = (uint)width;
        mode.PelsHeight = (uint)height;
        mode.DisplayFrequency = (uint)hz;
        mode.Fields = PelsWidth | PelsHeight | DisplayFrequency;
        return true;
    }

    private static int Change(string sourceName, DevModeW* mode, uint flags)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        try
        {
            fixed (char* name = sourceName)
            {
                return ChangeDisplaySettingsExW(name, mode, 0, flags, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private static DisplayChange Map(int code) => code switch
    {
        Successful => DisplayChange.Applied,
        Restart => DisplayChange.NeedsRestart,
        BadMode => DisplayChange.Refused,
        _ => DisplayChange.Failed,
    };

    [LibraryImport("user32.dll")]
    private static partial int ChangeDisplaySettingsExW(char* deviceName, DevModeW* devMode, nint hwnd, uint flags, nint parameter);
}
