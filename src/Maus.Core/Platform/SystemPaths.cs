using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>
/// Emplacements de Windows lus à une source qu'un compte sans droits ne peut pas changer : le noyau pour le dossier de Windows
/// (<c>GetSystemWindowsDirectory</c>) et la valeur brute de <c>HKLM</c> pour le reste, sans développer aucune variable de
/// l'environnement. Tout l'espace réservé de MAUS part de <see cref="ProgramData"/>, et de lui seul.
/// </summary>
public static partial class SystemPaths
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string SessionEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Dossier de Windows, commun à toutes les sessions (<c>C:\Windows</c>).</summary>
    public static string WindowsFolder
    {
        get
        {
            var buffer = new char[260];
            var length = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Length);
            return length is > 0 and < 260
                ? new string(buffer, 0, (int)length).TrimEnd('\\')
                : Path.GetDirectoryName(Environment.SystemDirectory) ?? @"C:\Windows";
        }
    }

    /// <summary>Lecteur de Windows, sous la forme de la variable <c>SystemDrive</c> (<c>C:</c>).</summary>
    public static string SystemDrive => WindowsFolder[..2];

    /// <summary>Racine de <c>%ProgramData%</c>.</summary>
    public static string ProgramData => ProgramDataFrom(ReadRawMachineValue(ProfileListKey, "ProgramData"), WindowsFolder);

    /// <summary>Dossier réservé de MAUS : <c>%ProgramData%\MAUS</c>.</summary>
    public static string MausData => Path.Combine(ProgramData, "MAUS");

    /// <summary>
    /// Variables qu'un processus élevé ne doit pas tenir de l'environnement du compte, avec leur valeur système. Leur remise
    /// est ce que fait aussi le lanceur natif (<c>Host/MausHost.cpp</c>) avant le démarrage de .NET.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SystemVariables()
    {
        var windows = WindowsFolder;
        var programData = ProgramData;
        var temp = TempFrom(ReadRawMachineValue(SessionEnvironmentKey, "TEMP"), windows);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemDrive"] = windows[..2],
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["ProgramData"] = programData,
            ["ALLUSERSPROFILE"] = programData,
            ["TEMP"] = temp,
            ["TMP"] = temp,
        };
    }

    /// <summary>Processus élevé : remet ces variables à leur valeur système, pour lui et pour ce qu'il lance.</summary>
    public static void ResetEnvironment()
    {
        foreach (var (name, value) in SystemVariables())
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// Vrai si le chemin est strictement sous <paramref name="root"/>. Un chemin mal formé n'y est pas.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + @"\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Valeur brute de <c>ProfileList\ProgramData</c> rendue en chemin, avec le lecteur de Windows pour seule substitution.
    /// Valeur absente, autre variable, chemin non local : <c>&lt;lecteur&gt;\ProgramData</c>, l'emplacement de Windows.
    /// </summary>
    internal static string ProgramDataFrom(string? raw, string windows) =>
        Resolved(raw, windows) ?? windows[..2] + @"\ProgramData";

    /// <summary>Comme <see cref="ProgramDataFrom"/>, pour le dossier temporaire de la machine (<c>%SystemRoot%\TEMP</c>).</summary>
    internal static string TempFrom(string? raw, string windows) =>
        Resolved(raw, windows) ?? windows + @"\Temp";

    private static string? Resolved(string? raw, string windows)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim()
            .Replace("%SystemDrive%", windows[..2], StringComparison.OrdinalIgnoreCase)
            .Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase);
        return IsLocalAbsolute(path) ? Path.TrimEndingDirectorySeparator(path) : null;
    }

    /// <summary><c>X:\…</c>, sans variable restante, ni « .. », ni caractère interdit.</summary>
    private static bool IsLocalAbsolute(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && !path.Contains('%') && !path.Contains("..", StringComparison.Ordinal)
        && path[2..].IndexOfAny(Path.GetInvalidPathChars()) < 0 && !path[2..].Contains(':');

    private static string? ReadRawMachineValue(string key, string name)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var opened = machine.OpenSubKey(key);
            return opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetSystemWindowsDirectoryW([Out] char[] buffer, uint size);
}
