using System.Text;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>Le code de sortie d'un audit : c'est lui qu'un script regarde (la liste complète est dans <see cref="CliHelp"/>).</summary>
internal static class AuditExit
{
    /// <summary>2 : un problème rouge. 4 : un module n'a pas pu être vérifié, le résultat est partiel (un module non choisi n'est pas une panne).</summary>
    public static int Of(IReadOnlyList<ModuleResult> results) =>
        results.Any(r => r.WorstStatus == FindingStatus.Problem) ? 2 : results.Any(r => r.Error is not null && !r.NotChosen) ? 4 : 0;
}

/// <summary>Le fichier du rapport HTML : contrôlé avant l'audit, qui peut durer plusieurs minutes, et avant toute correction.</summary>
internal static class HtmlReportFile
{
    /// <summary>La raison pour laquelle ce fichier ne pourra pas être écrit, ou <c>null</c>. Un fichier existant n'est ni vidé ni touché ; un essai de création ne laisse rien.</summary>
    public static string? Problem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return T("indiquez un nom de fichier.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);

            // Un périphérique (NUL) n'a ni dossier ni fichier à créer et supprimer : l'écriture dira si elle échoue.
            if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return null;
            }

            if (Directory.Exists(fullPath))
            {
                return T("ce chemin est un dossier, indiquez un nom de fichier.");
            }

            if (Path.GetDirectoryName(fullPath) is { } folder && !Directory.Exists(folder))
            {
                return T("le dossier n'existe pas : {0}", folder);
            }

            using var trial = File.Exists(fullPath)
                ? new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                : new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return null;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            return Explain(ex);
        }
    }

    /// <summary>Écrit le rapport ; un échec est dit sur la sortie d'erreur et rend <c>false</c> : l'appelant en fait le code 3.</summary>
    public static bool Write(string path, HtmlReportInput input, TextWriter output, TextWriter errors)
    {
        try
        {
            File.WriteAllText(path, HtmlReport.Build(input with { Privacy = PrivacyFilter.ForCurrentUser() }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine(T("Rapport HTML enregistré : {0}", Path.GetFullPath(path)));
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            errors.WriteLine(T("Rapport HTML non enregistré : {0}", Explain(ex)));
            return false;
        }
    }

    private static bool IsWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>Nos phrases : celles de .NET changent de langue selon le cas, et disent « accès refusé » pour un dossier.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => T("accès refusé : le fichier est en lecture seule, ou ce dossier demande d'autres droits."),
        DirectoryNotFoundException => T("le dossier n'existe pas."),
        ArgumentException or NotSupportedException => T("ce nom de fichier n'est pas valide."),
        _ => T("écriture impossible : nom de fichier incorrect, fichier ouvert dans un autre programme, ou disque plein."),
    };
}

/// <summary>
/// Progression sur la sortie d'erreur, écrite tout de suite : avec Progress&lt;T&gt;, la dernière ligne pouvait sortir au milieu du rapport.
/// Un module peut durer plus d'une minute au premier lancement : après un long silence, une ligne dit lesquels restent ouverts, une seule fois.
/// </summary>
internal sealed class ConsoleProgress : IProgress<ModuleResult>, IDisposable
{
    /// <summary>Au-delà du temps normal des modules les plus lents.</summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _open;
    private readonly TextWriter _output;
    private readonly TimeSpan _patience;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _said;

    /// <param name="modules">Modules que l'audit va lancer.</param>
    public ConsoleProgress(IEnumerable<string> modules, TextWriter output, TimeSpan patience)
    {
        _open = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
        _output = output;
        _patience = patience;
        _timer = new Timer(_ => SayWhatIsStillOpen(), null, patience, Timeout.InfiniteTimeSpan);
    }

    public void Report(ModuleResult value)
    {
        lock (_gate)
        {
            _open.Remove(value.ModuleId);
            if (value.NotChosen)
            {
                return;
            }

            _output.WriteLine(T("  … {0} terminé ({1:0.0} s)", value.ModuleId, value.Duration.TotalSeconds));
            if (!_said && _open.Count > 0)
            {
                _timer.Change(_patience, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void SayWhatIsStillOpen()
    {
        lock (_gate)
        {
            if (_said || _open.Count == 0)
            {
                return;
            }

            _said = true;
            _output.WriteLine(T("  … encore en cours : {0}. Le premier lancement peut être long, MAUS n'est pas bloqué.", string.Join(", ", _open.Order(StringComparer.Ordinal))));
        }
    }

    public void Dispose() => _timer.Dispose();
}
