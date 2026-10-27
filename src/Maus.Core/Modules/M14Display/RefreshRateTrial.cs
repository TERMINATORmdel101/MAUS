using System.Runtime.InteropServices;
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
public static class RefreshRateTargets
{
    private const string Prefix = "M14.refresh.display-";
    private const double Tolerance = 1.0;

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
    private const uint EnumCurrentSettings = unchecked((uint)-1);
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

        return Map(Change(sourceName, &mode, UpdateRegistry));
    }

    public DisplayChange RestoreSaved(string sourceName) => Map(Change(sourceName, null, 0));

    /// <summary>Part du mode actuel (orientation, position, profondeur gardées) et ne change que taille et fréquence.</summary>
    private static bool TryReadCurrent(string sourceName, int width, int height, int hz, out DevMode mode)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        fixed (char* name = sourceName)
        fixed (DevMode* pointer = &mode)
        {
            if (EnumDisplaySettingsExW(name, EnumCurrentSettings, pointer, 0) == 0)
            {
                return false;
            }
        }

        mode.PelsWidth = (uint)width;
        mode.PelsHeight = (uint)height;
        mode.DisplayFrequency = (uint)hz;
        mode.Fields = PelsWidth | PelsHeight | DisplayFrequency;
        return true;
    }

    private static int Change(string sourceName, DevMode* mode, uint flags)
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

    /// <summary><c>DEVMODEW</c> (220 octets) : taille à 68, champs utilisés à 72, mode d'affichage à 172-184.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)]
        public ushort Size;

        [FieldOffset(72)]
        public uint Fields;

        [FieldOffset(172)]
        public uint PelsWidth;

        [FieldOffset(176)]
        public uint PelsHeight;

        [FieldOffset(184)]
        public uint DisplayFrequency;
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplaySettingsExW(char* deviceName, uint modeNum, DevMode* devMode, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int ChangeDisplaySettingsExW(char* deviceName, DevMode* devMode, nint hwnd, uint flags, nint parameter);
}
