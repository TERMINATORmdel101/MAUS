using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Cœur physique : son numéro, et le premier processeur logique qui le représente (groupe et masque).</summary>
public sealed record CpuCore(int Index, ushort Group, ulong Mask, byte EfficiencyClass);

/// <summary>Liste des cœurs et épinglage d'un fil sur l'un d'eux (Windows, ou faux dans les tests).</summary>
public interface ICoreTopology
{
    /// <summary>Cœurs physiques, un seul processeur logique par cœur (SMT / Hyper-Threading ignoré).</summary>
    IReadOnlyList<CpuCore> Cores();

    /// <summary>Épingle le fil courant sur ce cœur ; <c>false</c> si Windows refuse.</summary>
    bool PinCurrentThread(CpuCore core);
}

/// <summary>
/// Trace écrite avant chaque cœur : si le PC gèle ou redémarre, elle dit au lancement suivant quel cœur était testé.
/// <paramref name="Phase"/> : 0 = test cœur par cœur, 1 = programme complet phase cœur par cœur, 2 = programme complet phase
/// de transitoires (tous les cœurs à la fois, <paramref name="Core"/> vaut alors −1).
/// </summary>
public sealed record CoreTestCheckpoint(DateTimeOffset StartedAt, int Core, int Position, int CoreCount, int Phase = 0);

public interface ICoreTestCheckpoint
{
    CoreTestCheckpoint? Load();

    void Save(CoreTestCheckpoint state);

    void Clear();
}

public sealed record CoreCycleOptions(TimeSpan PerCore, int Rounds = 1, CpuStressMode Load = CpuStressMode.Automatic)
{
    /// <summary>Un cœur qui ne donne plus signe de vie pendant ce délai est déclaré figé.</summary>
    public TimeSpan FreezeAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pour les tests : altère un résultat (cœur, tour, résultat) pour simuler une erreur de calcul.</summary>
    internal Func<int, long, ulong, ulong>? Fault { get; init; }

    /// <summary>Pour les tests : ce cœur cesse de répondre (fil bloqué jusqu'à l'arrêt).</summary>
    internal Func<int, bool>? Stall { get; init; }
}

public sealed record CoreCycleProgress(int Core, int Position, int CoreCount, string Phase, double Percent, int FailedCores);

/// <summary>Bilan d'un cœur : tours de calcul vérifiés, erreurs, cœur figé, épinglage accepté par Windows.</summary>
public sealed record CoreVerdict(int Core, long Rounds, int Errors, bool Froze, bool Pinned)
{
    public bool Stable => Errors == 0 && !Froze;
}

public sealed record CoreCycleResult(IReadOnlyList<CoreVerdict> Cores, TimeSpan Duration, bool Aborted, string? AbortReason, int? WheaEvents)
{
    public bool Stable => Cores.All(c => c.Stable) && WheaEvents is null or 0;
}

/// <summary>
/// Test « cœur par cœur », pour valider un Curve Optimizer (AMD) ou un undervolt (Intel). Ces réglages lâchent surtout quand
/// un cœur seul monte à sa fréquence maximale, au réveil et aux changements de charge, rarement sous une charge continue sur
/// tous les cœurs (technique connue, popularisée par l'outil libre CoreCycler). MAUS teste donc chaque cœur à tour de rôle,
/// avec un seul fil épinglé, en alternant charge continue, à-coups et repos, et compare chaque calcul à la référence.
/// </summary>
public static class CoreCycleTest
{
    private static readonly TimeSpan SustainedPhase = TimeSpan.FromSeconds(3);
    private const int Bursts = 10;
    private static readonly TimeSpan BurstWork = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan BurstPause = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan IdlePhase = TimeSpan.FromSeconds(1);

    public static async Task<CoreCycleResult> RunAsync(
        ICoreTopology topology,
        ICoreTestCheckpoint? checkpoint,
        CoreCycleOptions options,
        IProgress<CoreCycleProgress>? progress = null,
        Func<string?>? danger = null,
        Func<DateTimeOffset, int?>? wheaSince = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var cores = topology.Cores();
        var kernel = CpuStress.Kernel(options.Load);
        var reference = kernel();
        var verdicts = cores.ToDictionary(c => c.Index, c => new CoreVerdict(c.Index, 0, 0, false, true));
        string? abortReason = null;
        var total = Math.Max(1, cores.Count * options.Rounds);
        var done = 0;

        for (var round = 0; round < options.Rounds && abortReason is null; round++)
        {
            for (var position = 0; position < cores.Count && abortReason is null; position++)
            {
                var core = cores[position];
                checkpoint?.Save(new CoreTestCheckpoint(started, core.Index, position, cores.Count));
                var worker = new Worker(core, topology, options, kernel, reference);
                var thread = new Thread(worker.Run) { IsBackground = true, Name = $"MAUS core {core.Index}" };
                thread.Start();
                var coreClock = Stopwatch.StartNew();
                var froze = false;
                while (!worker.Finished && coreClock.Elapsed < options.PerCore)
                {
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    var failed = verdicts.Values.Count(v => !v.Stable) + (worker.Errors > 0 ? 1 : 0);
                    progress?.Report(new CoreCycleProgress(core.Index, position, cores.Count, worker.PhaseLabel,
                        100.0 * (done + Math.Min(1, coreClock.Elapsed / options.PerCore)) / total, failed));
                    if (cancellationToken.IsCancellationRequested)
                    {
                        abortReason = T("Test arrêté par l'utilisateur.");
                        break;
                    }

                    if (danger?.Invoke() is { } alarm)
                    {
                        abortReason = T("arrêt de sécurité : {0}", alarm);
                        break;
                    }

                    if (worker.SilentFor > options.FreezeAfter)
                    {
                        froze = true;
                        break;
                    }
                }

                worker.Stop();
                if (!froze)
                {
                    thread.Join(TimeSpan.FromSeconds(5));
                }

                var previous = verdicts[core.Index];
                verdicts[core.Index] = new CoreVerdict(core.Index, previous.Rounds + worker.Rounds, previous.Errors + worker.Errors, previous.Froze || froze, previous.Pinned && worker.Pinned);
                done++;
            }
        }

        // Fin du test (même interrompu par l'utilisateur) : seule une vraie coupure laisse la trace pour le lancement suivant.
        checkpoint?.Clear();
        clock.Stop();
        int? whea = null;
        try
        {
            whea = wheaSince?.Invoke(started);
        }
        catch (InvalidOperationException)
        {
            // Journal illisible : le bilan se fait sans lui.
        }

        return new CoreCycleResult(cores.Select(c => verdicts[c.Index]).ToList(), clock.Elapsed, abortReason is not null, abortReason, whea);
    }

    /// <summary>
    /// Découpe une durée totale en tours : chaque cœur passe au plus 2 minutes d'affilée (au moins 15 secondes), puis on
    /// recommence le tour des cœurs. Un test long revient donc plusieurs fois sur chaque cœur, à des températures différentes.
    /// </summary>
    public static CoreCycleOptions Plan(TimeSpan total, int coreCount)
    {
        var cores = Math.Max(1, coreCount);
        var slice = Math.Clamp(total.TotalSeconds / cores, 15, 120);
        var rounds = Math.Max(1, (int)Math.Round(total.TotalSeconds / (slice * cores)));
        return new CoreCycleOptions(TimeSpan.FromSeconds(slice), rounds);
    }

    /// <summary>Fil de calcul d'un cœur : il signale régulièrement qu'il est vivant (battement).</summary>
    private sealed class Worker(CpuCore core, ICoreTopology topology, CoreCycleOptions options, Func<ulong> kernel, ulong reference)
    {
        private volatile bool _stopping;
        private long _heartbeat = Stopwatch.GetTimestamp();
        private long _rounds;
        private int _errors;
        private volatile string _phase = T("charge continue");

        public bool Pinned { get; private set; } = true;

        public bool Finished { get; private set; }

        public long Rounds => Interlocked.Read(ref _rounds);

        public int Errors => Volatile.Read(ref _errors);

        public string PhaseLabel => _phase;

        public TimeSpan SilentFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _heartbeat));

        public void Stop() => _stopping = true;

        public void Run()
        {
            try
            {
                Pinned = topology.PinCurrentThread(core);
                if (options.Stall?.Invoke(core.Index) == true)
                {
                    while (!_stopping)
                    {
                        Thread.Sleep(20);
                    }

                    return;
                }

                while (!_stopping)
                {
                    _phase = T("charge continue");
                    Work(SustainedPhase);
                    _phase = T("à-coups (réveils du cœur)");
                    for (var i = 0; i < Bursts && !_stopping; i++)
                    {
                        Work(BurstWork);
                        Pause(BurstPause);
                    }

                    _phase = T("repos");
                    Pause(IdlePhase);
                }
            }
            catch (Exception)
            {
                // Une exception dans un fil d'arrière-plan fermerait MAUS : elle compte comme une erreur de ce cœur.
                Interlocked.Increment(ref _errors);
            }
            finally
            {
                Finished = true;
            }
        }

        private void Work(TimeSpan duration)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < duration && !_stopping)
            {
                var result = kernel();
                var count = Interlocked.Increment(ref _rounds);
                if (options.Fault is { } fault)
                {
                    result = fault(core.Index, count, result);
                }

                if (result != reference)
                {
                    Interlocked.Increment(ref _errors);
                }

                Interlocked.Exchange(ref _heartbeat, Stopwatch.GetTimestamp());
            }
        }

        /// <summary>Repos réel du cœur (il peut entrer en veille), par petites tranches pour répondre vite à l'arrêt.</summary>
        private void Pause(TimeSpan duration)
        {
            var watch = Stopwatch.StartNew();
            while (!_stopping && watch.Elapsed < duration)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(25, (duration - watch.Elapsed).TotalMilliseconds + 1)));
            }

            Interlocked.Exchange(ref _heartbeat, Stopwatch.GetTimestamp());
        }
    }
}
