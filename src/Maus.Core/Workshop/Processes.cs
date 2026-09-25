using System.Text.RegularExpressions;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Relevé brut d'un processus : compteurs cumulés, convertis en taux par <see cref="ProcessMonitor"/>.</summary>
public sealed record RawProcess(
    int Pid,
    string Name,
    string? Path,
    TimeSpan? CpuTime,
    long WorkingSetBytes,
    long PrivateBytes,
    ulong? IoBytes,
    int SessionId,
    DateTime? StartTime);

/// <summary>Un processus à un instant donné, tel que l'affiche l'atelier.</summary>
public sealed record ProcessSample(
    int Pid,
    string Name,
    string? Path,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double DiskBytesPerSecond,
    double? GpuPercent,
    int SessionId,
    DateTime? StartTime);

public interface IProcessSource
{
    IReadOnlyList<RawProcess> Read();

    /// <summary>Utilisation du GPU par processus (compteurs « GPU Engine »), en %.</summary>
    IReadOnlyDictionary<int, double> GpuPercentByPid();
}

/// <summary>Calcule l'utilisation processeur et disque de chaque processus entre deux relevés.</summary>
public sealed class ProcessMonitor(IProcessSource source, int logicalProcessors, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private Dictionary<(int Pid, DateTime? Start), RawProcess> _previous = [];
    private DateTimeOffset? _previousAt;

    public IReadOnlyList<ProcessSample> Sample()
    {
        var now = _clock();
        var current = source.Read();
        var gpu = source.GpuPercentByPid();
        var elapsed = _previousAt is { } before ? (now - before).TotalSeconds : 0;

        var samples = current.Select(process =>
        {
            double cpu = 0;
            double disk = 0;
            if (elapsed > 0 && _previous.TryGetValue((process.Pid, process.StartTime), out var old))
            {
                if (process.CpuTime is { } cpuNow && old.CpuTime is { } cpuBefore)
                {
                    cpu = Math.Clamp((cpuNow - cpuBefore).TotalSeconds / (elapsed * Math.Max(1, logicalProcessors)) * 100, 0, 100);
                }

                if (process.IoBytes is { } ioNow && old.IoBytes is { } ioBefore && ioNow >= ioBefore)
                {
                    disk = (ioNow - ioBefore) / elapsed;
                }
            }

            return new ProcessSample(process.Pid, process.Name, process.Path, cpu, process.WorkingSetBytes, process.PrivateBytes, disk,
                gpu.TryGetValue(process.Pid, out var g) ? Math.Min(100, g) : null, process.SessionId, process.StartTime);
        }).ToList();

        _previous = current.GroupBy(p => (p.Pid, p.StartTime)).ToDictionary(g => g.Key, g => g.First());
        _previousAt = now;
        return samples;
    }
}

/// <summary>Degré de confiance d'un processus d'après son emplacement (la signature numérique se vérifie à la demande).</summary>
public enum ProcessTrust
{
    Windows,
    Installed,
    UserFolder,
    Unusual,
    Unknown,
}

/// <summary>Emplacements de référence (injectés pour les tests).</summary>
public sealed record SystemFolders(string Windows, string ProgramFiles, string ProgramFilesX86, string ProgramData)
{
    public static SystemFolders Current { get; } = new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
}

public static partial class ProcessRules
{
    /// <summary>Processus vitaux : les arrêter provoque un écran bleu ou coupe la session. MAUS refuse toujours.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "Secure System",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
        "svchost.exe", "fontdrvhost.exe", "dwm.exe", "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
    };

    public static bool IsCritical(string name) => Critical.Contains(name) || Critical.Contains(name + ".exe");

    public static ProcessTrust TrustOf(ProcessSample process, SystemFolders folders)
    {
        if (process.Path is not { Length: > 0 } path)
        {
            return IsCritical(process.Name) ? ProcessTrust.Windows : ProcessTrust.Unknown;
        }

        if (Under(path, folders.Windows))
        {
            return ProcessTrust.Windows;
        }

        if (Under(path, folders.ProgramFiles) || Under(path, folders.ProgramFilesX86) || Under(path, folders.ProgramData + @"\Microsoft\Windows Defender"))
        {
            return ProcessTrust.Installed;
        }

        if (UnusualFolder().IsMatch(path))
        {
            return ProcessTrust.Unusual;
        }

        return path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ? ProcessTrust.UserFolder : ProcessTrust.Unknown;
    }

    public static string Describe(ProcessTrust trust) => trust switch
    {
        ProcessTrust.Windows => T("Composant de Windows"),
        ProcessTrust.Installed => T("Programme installé"),
        ProcessTrust.UserFolder => T("Programme installé pour votre compte"),
        ProcessTrust.Unusual => T("Emplacement inhabituel : à vérifier"),
        _ => T("Emplacement inconnu"),
    };

    /// <summary>Peut-on proposer d'arrêter ce processus ? Sinon, la raison, en clair.</summary>
    public static (bool Allowed, string? Reason) CanTerminate(ProcessSample process, int ownPid)
    {
        if (process.Pid == ownPid)
        {
            return (false, T("C'est MAUS lui-même."));
        }

        if (process.Pid is 0 or 4 || IsCritical(process.Name))
        {
            return (false, T("Processus vital de Windows : l'arrêter provoquerait un écran bleu ou fermerait votre session."));
        }

        return (true, null);
    }

    private static bool Under(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\\(Temp|Downloads|Téléchargements|Descargas)\\|\\AppData\\Local\\Temp\\|\\Users\\Public\\", RegexOptions.IgnoreCase)]
    private static partial Regex UnusualFolder();
}

/// <summary>Moteurs de recherche proposés ; le choix revient à l'utilisateur.</summary>
public enum SearchEngine
{
    DuckDuckGo,
    Qwant,
    Ecosia,
    Google,
    Bing,
}

/// <summary>Recherche web lancée uniquement par un clic de l'utilisateur : MAUS n'envoie rien de lui-même.</summary>
public static class WebSearch
{
    public static Uri Build(SearchEngine engine, string query)
    {
        var q = Uri.EscapeDataString(query);
        return new Uri(engine switch
        {
            SearchEngine.Qwant => $"https://www.qwant.com/?q={q}",
            SearchEngine.Ecosia => $"https://www.ecosia.org/search?q={q}",
            SearchEngine.Google => $"https://www.google.com/search?q={q}",
            SearchEngine.Bing => $"https://www.bing.com/search?q={q}",
            _ => $"https://duckduckgo.com/?q={q}",
        });
    }

    /// <summary>Requête pour un processus : son nom exact, entre guillemets, et le mot « processus » dans la langue active.</summary>
    public static string ForProcess(string name) => $"\"{name}\" {T("processus Windows")}";

    /// <summary>Requête pour un composant : sa référence exacte et « fiche technique ».</summary>
    public static string ForComponent(string reference) => $"\"{reference}\" {T("fiche technique")}";
}
