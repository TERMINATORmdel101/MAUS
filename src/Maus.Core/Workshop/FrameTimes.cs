using System.Globalization;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Bilan des images d'un jeu pendant le relevé.</summary>
/// <param name="Application">Programme qui a affiché le plus d'images (le jeu).</param>
/// <param name="AverageFps">Images par seconde en moyenne : nombre d'images divisé par le temps total.</param>
/// <param name="SlowestFrameMs">
/// Durée de la plus rapide des images du 1 % le plus lent : 1 % des images (arrondi au-dessus) ont duré au moins autant.
/// </param>
public sealed record FrameSummary(string Application, int Frames, double AverageFps, double SlowestFrameMs)
{
    /// <summary>La même durée exprimée en images par seconde.</summary>
    public double SlowestFps => SlowestFrameMs > 0 ? 1000 / SlowestFrameMs : 0;

    public string Describe() => T("{0} : {1:0} images par seconde en moyenne ; 1 % des images ont duré {2:0.0} ms ou plus (soit {3:0} images par seconde), sur {4} images.",
        Application, AverageFps, SlowestFrameMs, SlowestFps, Frames);
}

/// <summary>
/// Durées d'image relevées par PresentMon (Intel, licence MIT ; colonnes « Application », « ProcessID »,
/// « msBetweenPresents » et « Dropped » de sa sortie CSV en mode <c>--v1_metrics</c>, vérifiées le 05/10/2026 avec la
/// version 2.6.0). Les lignes arrivent d'un autre fil : l'ajout et le bilan sont protégés.
/// </summary>
public sealed class FrameTimeLog
{
    /// <summary>Moins d'images que cela : pas de bilan (une poignée d'images ne dit rien d'une partie).</summary>
    public const int MinimumFrames = 100;

    /// <summary>Le compositeur de Windows et MAUS affichent aussi des images : jamais pris pour le jeu.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "dwm.exe", "MAUS.exe", "<error>", "<unknown>" };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<double>> _frames = new(StringComparer.OrdinalIgnoreCase);
    private int _application = -1;
    private int _frameTime = -1;
    private int _dropped = -1;

    /// <summary>Ajoute une ligne de la sortie CSV (la première est l'en-tête).</summary>
    public void AddCsvLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var cells = line.Split(',');
        lock (_gate)
        {
            if (_frameTime < 0)
            {
                _application = Array.FindIndex(cells, c => c.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase));
                _frameTime = Array.FindIndex(cells, c => c.Trim().Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase));
                _dropped = Array.FindIndex(cells, c => c.Trim().Equals("Dropped", StringComparison.OrdinalIgnoreCase));
                return;
            }

            if (_application < 0 || cells.Length <= Math.Max(_application, _frameTime))
            {
                return;
            }

            // Image jamais affichée : elle ne compte pas dans ce que le joueur a vu.
            if (_dropped >= 0 && _dropped < cells.Length && cells[_dropped].Trim() == "1")
            {
                return;
            }

            if (!double.TryParse(cells[_frameTime], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 10_000)
            {
                return;
            }

            var application = cells[_application].Trim();
            if (!_frames.TryGetValue(application, out var list))
            {
                _frames[application] = list = [];
            }

            list.Add(ms);
        }
    }

    /// <summary>Bilan du programme qui a affiché le plus d'images, ou <c>null</c> s'il n'y en a pas assez.</summary>
    public FrameSummary? Summarize()
    {
        lock (_gate)
        {
            var game = _frames.Where(f => !NotGames.Contains(f.Key)).OrderByDescending(f => f.Value.Count).FirstOrDefault();
            if (game.Value is not { Count: >= MinimumFrames } frames)
            {
                return null;
            }

            // Le 1 % le plus lent : les ⌈n / 100⌉ images les plus longues ; on garde la plus courte d'entre elles.
            var sorted = frames.Order().ToList();
            var slowest = (int)Math.Ceiling(sorted.Count / 100.0);
            return new FrameSummary(game.Key, frames.Count, frames.Count * 1000 / frames.Sum(), sorted[^slowest]);
        }
    }
}
