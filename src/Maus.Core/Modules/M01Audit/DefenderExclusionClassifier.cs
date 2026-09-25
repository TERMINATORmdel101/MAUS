using System.Text.RegularExpressions;
using Maus.Core.Platform;

namespace Maus.Core.Modules.M01Audit;

internal enum ExclusionKind
{
    Path,
    Extension,
    Process,
}

internal sealed record DefenderExclusion(ExclusionKind Kind, string Value)
{
    public override string ToString() => Kind switch
    {
        ExclusionKind.Extension => "extension " + Value,
        ExclusionKind.Process => "processus " + Value,
        _ => Value,
    };

    /// <summary>
    /// Exclusions lues dans une instance <c>MSFT_MpPreference</c>, ou <c>null</c> si Defender les masque :
    /// sans droits administrateur, chaque liste contient « N/A: Must be an administrator to view exclusions ».
    /// </summary>
    public static IReadOnlyList<DefenderExclusion>? ReadAll(CimRow preferences)
    {
        var exclusions = new List<DefenderExclusion>();
        foreach (var (property, kind) in new[] { ("ExclusionPath", ExclusionKind.Path), ("ExclusionExtension", ExclusionKind.Extension), ("ExclusionProcess", ExclusionKind.Process) })
        {
            foreach (var value in preferences.GetStringArray(property))
            {
                if (value.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    exclusions.Add(new DefenderExclusion(kind, value.Trim()));
                }
            }
        }

        return exclusions;
    }
}

/// <summary>
/// Repère les exclusions « trop larges » d'après la liste Microsoft des erreurs courantes :
/// racine d'un disque, dossiers système ou de profil, dossiers temporaires, extensions exécutables, interpréteurs de scripts.
/// </summary>
internal static partial class DefenderExclusionClassifier
{
    private static readonly HashSet<string> BroadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "sys", "scr", "com", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "hta",
        "msi", "lnk", "zip", "rar", "7z", "iso", "jar", "cpl", "docm", "xlsm", "*",
    };

    private static readonly HashSet<string> BroadProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
        "explorer.exe", "svchost.exe", "msiexec.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "node.exe",
        "conhost.exe", "schtasks.exe", "wmic.exe", "bitsadmin.exe", "certutil.exe", "installutil.exe", "msbuild.exe", "*",
    };

    private static readonly HashSet<string> BroadVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "%temp%", "%tmp%", "%userprofile%", "%appdata%", "%localappdata%", "%programdata%", "%windir%", "%systemroot%",
        "%programfiles%", "%programfiles(x86)%", "%systemdrive%", "%public%", "%allusersprofile%",
    };

    public static bool IsBroad(DefenderExclusion exclusion) => exclusion.Kind switch
    {
        ExclusionKind.Extension => BroadExtensions.Contains(exclusion.Value.Trim().TrimStart('*').TrimStart('.')),
        ExclusionKind.Process => BroadProcesses.Contains(Path.GetFileName(exclusion.Value.Trim())),
        _ => IsBroadPath(exclusion.Value),
    };

    private static bool IsBroadPath(string value)
    {
        var path = value.Trim().TrimEnd('*', '\\', '/');
        if (path.Length == 0 || BroadVariables.Contains(path))
        {
            return true;
        }

        return DriveRoot().IsMatch(path) || SystemFolder().IsMatch(path) || ProfileFolder().IsMatch(path) ||
            path.EndsWith("\\Temp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("\\Downloads", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveRoot();

    [GeneratedRegex(@"^[A-Za-z]:\\(Users|Windows|Windows\\System32|Windows\\SysWOW64|Program Files|Program Files \(x86\)|ProgramData)$", RegexOptions.IgnoreCase)]
    private static partial Regex SystemFolder();

    [GeneratedRegex(@"^[A-Za-z]:\\Users\\[^\\]+(\\(AppData|AppData\\Local|AppData\\Roaming|AppData\\LocalLow|Desktop|Documents|Downloads))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFolder();
}
