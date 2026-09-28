using System.Diagnostics;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Programme complet de validation du Curve Optimizer (ou d'un undervolt) : phase cœur par cœur puis phase de transitoires,
/// sur plusieurs heures, avec une barre d'avancement, les erreurs au fil de l'eau et un bilan par cœur.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private ICommand? _startCurveProgram;
    private TestOption<CpuStressMode>? _coreMode;
    private TestOption<TimeSpan>? _curveProgram;
    private double _curveProgress;
    private string _curveStatus = string.Empty;

    /// <summary>Charge des tests cœur par cœur et du programme complet (mêmes choix que le test du processeur).</summary>
    public TestOption<CpuStressMode> CoreMode
    {
        get => _coreMode ?? CpuModes[0];
        set
        {
            if (SetProperty(ref _coreMode, value))
            {
                OnPropertyChanged(nameof(CoreModeDetail));
            }
        }
    }

    public string CoreModeDetail => T("Instructions : {0}", CpuStress.Instructions(CoreMode.Value));

    /// <summary>Durées du programme complet, partagées moitié-moitié entre les deux phases.</summary>
    public IReadOnlyList<TestOption<TimeSpan>> CurvePrograms { get; } =
    [
        new(T("1 heure : 30 min cœur par cœur, puis 30 min de transitoires"), TimeSpan.FromHours(1)),
        new(T("4 heures : 2 h cœur par cœur, puis 2 h de transitoires (recommandé)"), TimeSpan.FromHours(4)),
    ];

    public TestOption<TimeSpan> CurveProgram
    {
        get => _curveProgram ?? CurvePrograms[1];
        set => SetProperty(ref _curveProgram, value);
    }

    public double CurveProgress
    {
        get => _curveProgress;
        private set => SetProperty(ref _curveProgress, value);
    }

    public string CurveStatus
    {
        get => _curveStatus;
        private set => SetProperty(ref _curveStatus, value);
    }

    public ICommand StartCurveProgramCommand => _startCurveProgram ??= new AsyncCommand(RunCurveProgramAsync);

    private async Task RunCurveProgramAsync()
    {
        if (Busy)
        {
            return;
        }

        // Occupé dès le clic : lecture des cœurs, des programmes actifs et confirmation prennent du temps.
        SetPreparing(true);
        try
        {
            await CurveProgramAsync();
        }
        finally
        {
            SetPreparing(false);
        }
    }

    private async Task CurveProgramAsync()
    {
        IReadOnlyList<CpuCore> cores;
        try
        {
            cores = await Task.Run(_topology.Cores);
        }
        catch (Exception ex)
        {
            CurveStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
            return;
        }

        if (cores.Count == 0)
        {
            CurveStatus = T("Windows n'a pas décrit les cœurs du processeur : le test cœur par cœur est impossible sur ce PC.");
            return;
        }

        var total = CurveProgram.Value;
        CurveStatus = T("Recherche des programmes qui utilisent le processeur…");
        var busy = await BusyProgramsAsync();
        CurveStatus = string.Empty;
        var warning = T("Pendant {0}, le PC sera INUTILISABLE : tous les cœurs vont être poussés tour à tour, puis tous ensemble par à-coups. Enregistrez votre travail et fermez vos programmes avant de continuer.", FormatDuration(total))
            + Environment.NewLine + Environment.NewLine
            + T("Phase 1 ({0}) : un cœur à la fois, pic de charge d'une seconde, chute au repos, réveil vérifié, cœur suivant. Phase 2 ({0}) : tous les fils en charge 5 à 10 secondes, arrêt simultané, 2 secondes de repos, et on recommence.", FormatDuration(total / 2))
            + Environment.NewLine + Environment.NewLine
            + T("MAUS ne ferme et ne suspend aucun autre programme (cela pourrait provoquer de faux plantages) : c'est à vous de les fermer. Pendant le programme, MAUS passe en priorité haute : pendant les rafales de la phase 2, les autres programmes seront presque à l'arrêt, c'est voulu. Jamais de priorité « temps réel » : elle peut empêcher Windows de vider ses caches disque et bloquer la souris. La mise en veille est suspendue jusqu'à la fin.")
            + Environment.NewLine + Environment.NewLine
            + T("Si vos ventilateurs sont pilotés par un logiciel plutôt que par le BIOS, ce logiciel peut réagir en retard pendant les rafales : pour un test long, préférez une courbe de ventilation réglée dans le BIOS.")
            + (busy.Count > 0 ? Environment.NewLine + Environment.NewLine + T("Programmes qui utilisent le processeur en ce moment : {0}.", string.Join(", ", busy)) : string.Empty)
            + HeavyWarning(CoreMode)
            + TemperatureCaveat()
            + Environment.NewLine + Environment.NewLine
            + T("Si le PC gèle ou redémarre, MAUS dira au prochain lancement dans quelle phase et sur quel cœur. Le programme s'arrête à tout moment avec « Arrêter le test ».")
            + Environment.NewLine + Environment.NewLine + T("Lancer le programme complet ?");
        if (!_confirm(T("Programme complet Curve Optimizer"), warning))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        var process = Process.GetCurrentProcess();
        var previousPriority = process.PriorityClass;
        CoreChips.Clear();
        CurveProgress = 0;
        var hybrid = cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
        var topClass = cores.Max(c => c.EfficiencyClass);
        string Name(int index)
        {
            var core = cores.First(c => c.Index == index);
            return hybrid ? T("Cœur {0} ({1})", index, core.EfficiencyClass == topClass ? "P" : "E") : T("Cœur {0}", index);
        }

        try
        {
            try
            {
                // Priorité haute : les programmes ordinaires cèdent la place ; « temps réel » est exclu (voir l'avertissement).
                process.PriorityClass = ProcessPriorityClass.High;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Priorité refusée : le programme tourne quand même, à la priorité normale.
            }

            CurveStatus = T("Programme en cours…");
            var progress = new Progress<CurveProgramProgress>(p =>
            {
                CurveProgress = p.Percent;
                var remaining = FormatDuration(p.Remaining);
                CurveStatus = p.Phase == 1
                    ? T("Phase 1/2 · cœur par cœur · {0} ({1}/{2}) · {3:0} % · reste {4} · {5} cœur(s) en erreur", Name(p.Core ?? 0), p.Position + 1, p.CoreCount, p.Percent, remaining, p.FailedCores)
                    : T("Phase 2/2 · transitoires (tous les cœurs) · rafale {0} · {1:0} % · reste {2} · {3} cœur(s) en erreur en phase 1 · {4} erreur(s) de calcul en phase 2", p.Bursts + 1, p.Percent, remaining, p.FailedCores, p.TransientErrors);
            });
            var logs = new Maus.Core.Platform.WindowsEventLogReader();
            var options = new CurveProgramOptions(total / 2, total / 2, CoreMode.Value);
            var result = await CurveOptimizerProgram.RunAsync(_topology, _checkpoint, options, progress, () => Live.DangerAlarm,
                since => WheaEvents.CountSince(logs, since), cancellation.Token);
            CurveProgress = 100;

            foreach (var verdict in result.Cores)
            {
                CoreChips.Add(new CoreChipViewModel(
                    verdict.Froze ? T("{0} · figé", Name(verdict.Core))
                        : verdict.Errors > 0 ? T("{0} · {1} erreur(s)", Name(verdict.Core), verdict.Errors)
                        : verdict.Rounds > 0 ? T("{0} · stable", Name(verdict.Core)) : Name(verdict.Core),
                    verdict.Froze || verdict.Errors > 0 ? Controls.Palette.Red : verdict.Rounds > 0 ? Controls.Palette.Green : Controls.Palette.Grey));
            }

            if (result.Bursts > 0)
            {
                CoreChips.Add(new CoreChipViewModel(
                    result.TransientFroze ? T("Transitoires · gel")
                        : result.TransientErrors > 0 ? T("Transitoires · {0} erreur(s)", result.TransientErrors)
                        : T("Transitoires · {0} rafales, stable", result.Bursts),
                    result.TransientsStable ? Controls.Palette.Green : Controls.Palette.Red));
            }

            var failing = result.Cores.Where(c => !c.Stable).Select(c => Name(c.Core)).ToList();
            var whea = result.WheaEvents is > 0 ? " " + T("{0} erreur(s) matérielle(s) WHEA pendant le test : même sans plantage, le processeur est à la limite.", result.WheaEvents) : string.Empty;
            var advice = new List<string>();
            if (failing.Count > 0)
            {
                advice.Add(T("Cœur(s) instable(s) en phase 1 : {0}. Remontez le Curve Optimizer de ce(s) cœur(s) de 2 ou 3 points (par exemple de −15 à −12).", string.Join(", ", failing)));
            }

            if (!result.TransientsStable)
            {
                advice.Add(T("Erreur pendant les transitoires (tous les cœurs à la fois) : le réglage global ne tient pas les brusques variations de charge. Remontez l'ensemble du Curve Optimizer de 2 ou 3 points (ou réduisez l'undervolt)."));
            }

            CurveStatus = result.Aborted
                ? T("Programme interrompu : {0}", result.AbortReason) + " " + string.Join(" ", advice) + whea
                : advice.Count > 0
                    ? string.Join(" ", advice) + whea + " " + T("Puis refaites le programme.")
                    : result.WheaEvents is > 0
                        ? T("Aucune erreur de calcul, mais le processeur a signalé des erreurs matérielles.") + whea + " " + T("Remontez légèrement le Curve Optimizer ou l'undervolt, puis refaites le programme.")
                        : T("Programme complet réussi : {0} cœur(s) stables en phase 1, {1} rafales sans erreur en phase 2.", result.Cores.Count, result.Bursts) + " "
                          + (result.WheaEvents is null
                              ? T("Le journal des erreurs matérielles (WHEA) n'a pas pu être lu : regardez-le dans l'Observateur d'événements avant de conclure.")
                              : T("Aucune erreur matérielle WHEA."))
                          + " " + T("Utilisez ensuite le PC normalement quelques jours : certains gels n'arrivent qu'au repos.");

            if (result.Cores.Any(c => !c.Pinned))
            {
                CurveStatus += " " + T("Windows a refusé de fixer le test sur certains cœurs : le résultat par cœur est moins fiable.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CurveStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            try
            {
                process.PriorityClass = previousPriority;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Priorité d'origine non remise : elle reviendra au prochain lancement de MAUS.
            }

            await StopTestAsync();
        }
    }

    /// <summary>Programmes (hors MAUS et Windows lui-même) qui utilisent plus de 2 % du processeur, mesurés sur une seconde.</summary>
    private static async Task<IReadOnlyList<string>> BusyProgramsAsync()
    {
        try
        {
            var monitor = new ProcessMonitor(new WindowsProcessSource(), Environment.ProcessorCount);
            await Task.Run(monitor.Sample);
            await Task.Delay(1000);
            var samples = await Task.Run(monitor.Sample);
            return samples
                .Where(p => p.CpuPercent >= 2 && p.Pid != Environment.ProcessId && p.Pid > 4 && p.SessionId != 0)
                .OrderByDescending(p => p.CpuPercent)
                .Take(6)
                .Select(p => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{p.Name} ({p.CpuPercent:0} %)"))
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? T("{0} h {1:00} min", (int)duration.TotalHours, duration.Minutes)
        : T("{0} min", Math.Max(0, (int)Math.Ceiling(duration.TotalMinutes)));
}
