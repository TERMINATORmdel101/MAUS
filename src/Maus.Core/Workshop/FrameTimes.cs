using System.Globalization;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Bilan des images d'un jeu (toute la mesure, ou ses dernières secondes).</summary>
/// <param name="Application">Programme qui a affiché le plus d'images (le jeu).</param>
/// <param name="AverageFps">Images par seconde en moyenne : nombre d'images divisé par le temps total.</param>
/// <param name="Low1Ms">Plus courte des images du 1 % le plus lent : 1 % des images ont duré au moins autant.</param>
/// <param name="Low01Ms">Même chose pour le 0,1 % le plus lent ; <c>null</c> sous 1 000 images (ce ne serait que la pire image).</param>
/// <param name="GpuBusyShare">
/// Part du temps de chaque image pendant laquelle la carte graphique a travaillé (méthode « GPU Busy » d'Intel PresentMon,
/// colonne msGPUActive) ; <c>null</c> si PresentMon ne la fournit pas.
/// </param>
/// <param name="VSyncShare">Part des images envoyées avec la synchronisation verticale (colonne SyncInterval ≥ 1).</param>
public sealed record FrameSummary(string Application, int Frames, double AverageFps, double Low1Ms, double? Low01Ms, double? GpuBusyShare, double VSyncShare)
{
    public double Low1Fps => Low1Ms > 0 ? 1000 / Low1Ms : 0;

    public double? Low01Fps => Low01Ms is > 0 ? 1000 / Low01Ms : null;

    public string Describe() => Low01Fps is { } low01
        ? T("{0} : {1:0} images par seconde en moyenne ; 1 % le plus lent : {2:0} images par seconde ; 0,1 % le plus lent : {3:0} ({4} images).", Application, AverageFps, Low1Fps, low01, Frames)
        : T("{0} : {1:0} images par seconde en moyenne ; 1 % le plus lent : {2:0} images par seconde ({3} images).", Application, AverageFps, Low1Fps, Frames);
}

/// <summary>
/// Durées d'image relevées par PresentMon (Intel, licence MIT ; colonnes « Application », « TimeInSeconds »,
/// « msBetweenPresents », « Dropped », « SyncInterval » et « msGPUActive » de sa sortie CSV en mode <c>--v1_metrics</c>,
/// vérifiées le 05/10/2026 avec la version 2.6.0). Les lignes arrivent d'un autre fil : l'ajout et le bilan sont protégés.
/// </summary>
/// <param name="keep">Durée gardée en mémoire (compteur en direct) ; <c>null</c> = tout garder (relevé de partie).</param>
public sealed class FrameTimeLog(TimeSpan? keep = null)
{
    /// <summary>Moins d'images que cela : pas de bilan (une poignée d'images ne dit rien d'une partie).</summary>
    public const int MinimumFrames = 100;

    /// <summary>Le 0,1 % le plus lent n'a de sens qu'à partir de 1 000 images (en dessous, ce serait la seule pire image).</summary>
    public const int MinimumFramesFor01 = 1000;

    /// <summary>Le compositeur de Windows et MAUS affichent aussi des images : jamais pris pour le jeu.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "dwm.exe", "MAUS.exe", "<error>", "<unknown>" };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Frame>> _frames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int>? _columns;
    private double _latest;

    private readonly record struct Frame(double Time, double Ms, double? GpuMs, bool Synced);

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
            if (_columns is null)
            {
                _columns = cells.Select((c, i) => (Name: c.Trim(), i)).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
                return;
            }

            string? Cell(string name) => _columns.TryGetValue(name, out var i) && i < cells.Length ? cells[i].Trim() : null;
            double? Number(string name) => double.TryParse(Cell(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            // Image jamais affichée : elle ne compte pas dans ce que le joueur a vu.
            if (Cell("Application") is not { Length: > 0 } application || Cell("Dropped") == "1"
                || Number("msBetweenPresents") is not { } ms || ms <= 0 || ms > 10_000)
            {
                return;
            }

            var time = Number("TimeInSeconds") ?? _latest;
            _latest = Math.Max(_latest, time);
            if (!_frames.TryGetValue(application, out var list))
            {
                _frames[application] = list = [];
            }

            var gpu = Number("msGPUActive") is { } g && g >= 0 ? Math.Min(g, ms) : (double?)null;
            list.Add(new Frame(time, ms, gpu, Number("SyncInterval") >= 1));
            if (keep is { } window && list.Count > 4096 && list[0].Time < _latest - window.TotalSeconds)
            {
                list.RemoveAll(f => f.Time < _latest - window.TotalSeconds);
            }
        }
    }

    /// <summary>Bilan du programme qui a affiché le plus d'images, ou <c>null</c> s'il n'y en a pas assez.</summary>
    /// <param name="last">Seulement les dernières secondes (compteur en direct) ; <c>null</c> = toute la mesure.</param>
    public FrameSummary? Summarize(TimeSpan? last = null)
    {
        lock (_gate)
        {
            var since = last is { } window ? _latest - window.TotalSeconds : double.MinValue;
            var game = _frames
                .Where(f => !NotGames.Contains(f.Key))
                .Select(f => (Application: f.Key, Frames: f.Value.Where(x => x.Time >= since).ToList()))
                .OrderByDescending(f => f.Frames.Count)
                .FirstOrDefault();
            if (game.Frames is not { Count: >= MinimumFrames } frames)
            {
                return null;
            }

            // Le 1 % (ou 0,1 %) le plus lent : les ⌈n / 100⌉ images les plus longues ; on garde la plus courte d'entre elles.
            var sorted = frames.Select(f => f.Ms).Order().ToList();
            double Slowest(double share) => sorted[^(int)Math.Ceiling(sorted.Count * share)];
            var total = frames.Sum(f => f.Ms);
            var withGpu = frames.Where(f => f.GpuMs is not null).ToList();
            return new FrameSummary(
                game.Application,
                frames.Count,
                frames.Count * 1000 / total,
                Slowest(0.01),
                frames.Count >= MinimumFramesFor01 ? Slowest(0.001) : null,
                withGpu.Count == frames.Count && total > 0 ? withGpu.Sum(f => f.GpuMs!.Value) / total : null,
                frames.Count(f => f.Synced) / (double)frames.Count);
        }
    }
}

/// <summary>
/// Explication en clair d'un bilan d'images, pour aider à choisir une amélioration. Repères publiés :
/// <list type="bullet">
/// <item>méthode « GPU Busy » d'Intel (PresentMon) : si la carte graphique travaille presque toute la durée de chaque image,
/// c'est elle qui limite ; si l'image dure nettement plus longtemps, c'est le processeur, la mémoire ou une limite d'images ;</item>
/// <item>NVIDIA (Reflex) : plus d'images par seconde réduisent la latence, ce qui compte surtout dans les jeux compétitifs.</item>
/// </list>
/// Les bornes « presque toute » (95 %) et « nettement moins » (85 %) sont celles que MAUS emploie déjà pour la charge de la
/// carte graphique dans le bilan du relevé : ce sont des repères de MAUS, pas des normes publiées, et l'écran le dit.
/// </summary>
public static class FrameAdvice
{
    public const double GpuBound = 0.95;
    public const double NotGpuBound = 0.85;

    /// <param name="refreshHz">Fréquence de l'écran principal, si connue.</param>
    public static IReadOnlyList<string> Explain(FrameSummary frames, int? refreshHz)
    {
        var lines = new List<string>();
        if (refreshHz is > 0 and var hz)
        {
            lines.Add(frames.AverageFps >= hz
                ? T("Écran à {0} Hz : la moyenne atteint sa fréquence, chaque rafraîchissement reçoit en moyenne une image neuve. Au-delà, l'écran ne montre pas d'images complètes en plus : le gain est surtout la réactivité (latence), utile dans les jeux compétitifs (CS2, Valorant…), beaucoup moins dans un jeu d'aventure (Red Dead Redemption 2, Cyberpunk 2077…).", hz)
                : T("Écran à {0} Hz : la moyenne reste sous sa fréquence : l'écran pourrait afficher plus d'images que le PC n'en fournit.", hz));
        }

        if (frames.VSyncShare >= 0.5)
        {
            lines.Add(T("Synchronisation verticale (V-Sync) active sur {0:0} % des images : le nombre d'images par seconde est plafonné volontairement, ce n'est pas un manque de puissance.", frames.VSyncShare * 100));
        }

        if (frames.GpuBusyShare is { } busy)
        {
            var detail = T("La carte graphique a travaillé {0:0} % de la durée de chaque image (méthode « GPU Busy » d'Intel).", busy * 100);
            lines.Add(busy >= GpuBound
                ? detail + " " + T("C'est elle qui limite : pour plus d'images, baissez la résolution ou les réglages graphiques, activez DLSS / FSR / XeSS, ou, pour une amélioration matérielle, c'est la carte graphique qui compte.")
                : busy <= NotGpuBound
                    ? detail + " " + T("Elle attend souvent : c'est plutôt le processeur, la mémoire ou une limite d'images (V-Sync, limiteur du jeu) qui freine. Baisser les graphismes aidera peu ; les réglages qui chargent le processeur (distance de vue, foule, physique) davantage.")
                    : detail + " " + T("Entre les deux : ni la carte graphique ni le reste ne limitent nettement."));
        }

        return lines;
    }
}
