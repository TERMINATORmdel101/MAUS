using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Réglages du programme complet de validation d'un Curve Optimizer (ou d'un undervolt).</summary>
/// <param name="CyclingPhase">Durée de la phase 1 : un cœur à la fois, pic de charge puis chute au repos.</param>
/// <param name="TransientPhase">Durée de la phase 2 : tous les fils en charge puis arrêt simultané, en boucle.</param>
/// <param name="Load">Calcul utilisé pendant les pics et les rafales.</param>
public sealed record CurveProgramOptions(TimeSpan CyclingPhase, TimeSpan TransientPhase, CpuStressMode Load = CpuStressMode.Automatic)
{
    /// <summary>Pic de charge sur le cœur testé.</summary>
    public TimeSpan Spike { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Chute : le cœur ne fait plus rien et peut entrer en veille profonde avant d'être réveillé.</summary>
    public TimeSpan Drop { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Durée des rafales de la phase 2 : de 5 à 10 secondes, variée d'une rafale à l'autre.</summary>
    public TimeSpan MinBurst { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBurst { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Repos complet entre deux rafales.</summary>
    public TimeSpan Rest { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Un fil qui ne donne plus signe de vie pendant ce délai est déclaré figé.</summary>
    public TimeSpan FreezeAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Fils de calcul de la phase 2 ; par défaut, un par processeur logique.</summary>
    public int? Threads { get; init; }

    /// <summary>Pour les tests : altère un résultat (cœur en phase 1, fil en phase 2, tour, résultat).</summary>
    internal Func<int, long, ulong, ulong>? Fault { get; init; }

    /// <summary>Pour les tests : ce cœur (phase 1) ou ce fil (phase 2, index + 1000) cesse de répondre.</summary>
    internal Func<int, bool>? Stall { get; init; }

    /// <summary>Durée de la rafale n : 5, 6, 7, 8, 9, 10 secondes, puis on recommence.</summary>
    public TimeSpan BurstLength(int index)
    {
        var steps = 5;
        var step = (index * 7) % (steps + 1);
        return MinBurst + ((MaxBurst - MinBurst) * step / steps);
    }
}

/// <summary>Avancement : phase (1 ou 2), cœur testé (phase 1), pourcentage et temps restant du programme entier.</summary>
public sealed record CurveProgramProgress(int Phase, int? Core, int Position, int CoreCount, double Percent, TimeSpan Remaining, int FailedCores, int TransientErrors, int Bursts);

/// <summary>Bilan : verdict de chaque cœur (phase 1), puis erreurs et gel éventuel pendant les transitoires (phase 2).</summary>
public sealed record CurveProgramResult(
    IReadOnlyList<CoreVerdict> Cores,
    long TransientRounds,
    int TransientErrors,
    bool TransientFroze,
    int Bursts,
    TimeSpan Duration,
    bool Aborted,
    string? AbortReason,
    int? WheaEvents)
{
    public bool TransientsStable => TransientErrors == 0 && !TransientFroze;

    public bool Stable => Cores.All(c => c.Stable) && TransientsStable && WheaEvents is null or 0;
}

/// <summary>
/// Programme complet de validation d'un Curve Optimizer (AMD) ou d'un undervolt (Intel), en deux phases.
/// Phase 1 : un seul cœur à la fois (fil épinglé), pic de charge, chute au repos pour laisser le cœur s'endormir, puis réveil
/// vérifié (le « ping » : un calcul contrôlé doit revenir à temps), et on passe au cœur suivant. Phase 2 : tous les fils
/// chargent ensemble 5 à 10 secondes, s'arrêtent au même instant (le passage brutal de 100 % à 0 % fait remonter la tension),
/// se reposent 2 secondes et recommencent. Chaque calcul est comparé à une référence ; un fil muet trop longtemps est déclaré figé.
/// Les fils de calcul tournent en priorité « inférieure à la normale » : le programme de MAUS (surveillance, arrêt de sécurité,
/// fenêtre) garde la main. MAUS ne ferme, ne suspend et ne ralentit aucun autre programme.
/// </summary>
public static class CurveOptimizerProgram
{
    public static async Task<CurveProgramResult> RunAsync(
        ICoreTopology topology,
        ICoreTestCheckpoint? checkpoint,
        CurveProgramOptions options,
        IProgress<CurveProgramProgress>? progress = null,
        Func<string?>? danger = null,
        Func<DateTimeOffset, int?>? wheaSince = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var cores = topology.Cores();
        var kernel = CpuStress.Kernel(options.Load);
        var reference = kernel();
        var total = options.CyclingPhase + options.TransientPhase;
        var verdicts = cores.ToDictionary(c => c.Index, c => new CoreVerdict(c.Index, 0, 0, false, true));
        string? abortReason = null;
        long transientRounds = 0;
        var transientErrors = 0;
        var transientFroze = false;
        var bursts = 0;

        string? Check()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return T("Test arrêté par l'utilisateur.");
            }

            return danger?.Invoke() is { } alarm ? T("arrêt de sécurité : {0}", alarm) : null;
        }

        void Report(int phase, int? core, int position, int currentErrors)
        {
            var share = Math.Min(1, clock.Elapsed / total);
            progress?.Report(new CurveProgramProgress(phase, core, position, cores.Count, 100 * share,
                total - clock.Elapsed > TimeSpan.Zero ? total - clock.Elapsed : TimeSpan.Zero,
                verdicts.Values.Count(v => !v.Stable) + currentErrors, transientErrors, bursts));
        }

        // ----- Phase 1 : un cœur à la fois, pic puis chute -----
        var phase = Stopwatch.StartNew();
        for (var step = 0; cores.Count > 0 && abortReason is null && phase.Elapsed < options.CyclingPhase; step++)
        {
            var position = step % cores.Count;
            var core = cores[position];
            checkpoint?.Save(new CoreTestCheckpoint(started, core.Index, position, cores.Count, Phase: 1));
            var worker = new SpikeWorker(core, topology, options, kernel, reference);
            var thread = new Thread(worker.Run) { IsBackground = true, Name = $"MAUS pic cœur {core.Index}", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            var froze = false;
            while (!worker.Finished)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                // Le cœur en cours ne compte qu'une fois : il peut déjà être en erreur depuis un tour précédent.
                Report(1, core.Index, position, worker.Errors > 0 && verdicts[core.Index].Stable ? 1 : 0);
                if (Check() is { } reason)
                {
                    abortReason = reason;
                    break;
                }

                // Le « ping » : pic, chute et réveil doivent se terminer ; un fil muet trop longtemps = cœur figé.
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
        }

        // ----- Phase 2 : tous les fils ensemble, arrêt simultané, repos, et on recommence -----
        if (abortReason is null && options.TransientPhase > TimeSpan.Zero)
        {
            var threads = Math.Max(1, options.Threads ?? Environment.ProcessorCount);
            checkpoint?.Save(new CoreTestCheckpoint(started, -1, 0, threads, Phase: 2));
            using var group = new BurstGroup(threads, options, kernel, reference);
            phase.Restart();
            while (abortReason is null && !transientFroze && phase.Elapsed < options.TransientPhase)
            {
                group.Start(options.BurstLength(bursts));
                while (group.Running)
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    transientErrors = group.Errors;
                    Report(2, null, 0, 0);
                    if (Check() is { } reason)
                    {
                        abortReason = reason;
                        group.StopNow();
                        break;
                    }

                    if (group.SilentFor > options.FreezeAfter)
                    {
                        transientFroze = true;
                        group.StopNow();
                        break;
                    }
                }

                bursts++;
                var rest = Stopwatch.StartNew();
                while (abortReason is null && !transientFroze && rest.Elapsed < options.Rest)
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    Report(2, null, 0, 0);
                    abortReason = Check();
                }
            }

            transientRounds = group.Rounds;
            transientErrors = group.Errors;
        }

        // Fin du programme (même interrompu par l'utilisateur) : seule une vraie coupure laisse la trace pour le lancement suivant.
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

        return new CurveProgramResult(cores.Select(c => verdicts[c.Index]).ToList(), transientRounds, transientErrors, transientFroze, bursts,
            clock.Elapsed, abortReason is not null, abortReason, whea);
    }

    /// <summary>Phase 1 : un fil épinglé sur un cœur fait un pic de charge, laisse le cœur s'endormir, puis le réveille par un calcul vérifié.</summary>
    private sealed class SpikeWorker(CpuCore core, ICoreTopology topology, CurveProgramOptions options, Func<ulong> kernel, ulong reference)
    {
        private volatile bool _stopping;
        private long _heartbeat = Stopwatch.GetTimestamp();
        private long _rounds;
        private int _errors;

        public bool Pinned { get; private set; } = true;

        private volatile bool _finished;

        public bool Finished => _finished;

        public long Rounds => Interlocked.Read(ref _rounds);

        public int Errors => Volatile.Read(ref _errors);

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

                // Pic : charge continue sur ce seul cœur.
                var watch = Stopwatch.StartNew();
                while (!_stopping && watch.Elapsed < options.Spike)
                {
                    Verify();
                }

                // Chute : le fil dort, le cœur n'a plus rien à faire.
                var drop = Stopwatch.StartNew();
                while (!_stopping && drop.Elapsed < options.Drop)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(25, (options.Drop - drop.Elapsed).TotalMilliseconds + 1)));
                }

                // Réveil : un calcul vérifié, qui doit revenir avant le délai de gel.
                if (!_stopping)
                {
                    Verify();
                }
            }
            catch (Exception)
            {
                // Une exception dans un fil d'arrière-plan fermerait MAUS : elle compte comme une erreur de ce cœur.
                Interlocked.Increment(ref _errors);
            }
            finally
            {
                _finished = true;
            }
        }

        private void Verify()
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

    /// <summary>
    /// Phase 2 : un fil par processeur logique, sans épinglage. Tous sont libérés au même instant, calculent jusqu'à la même
    /// échéance (vérifiée après chaque calcul, de l'ordre de la milliseconde) puis attendent, bloqués, la rafale suivante.
    /// </summary>
    private sealed class BurstGroup : IDisposable
    {
        private readonly object _sync = new();
        private readonly Thread[] _threads;
        private readonly long[] _heartbeats;
        private readonly CurveProgramOptions _options;
        private readonly Func<ulong> _kernel;
        private readonly ulong _reference;
        private long _deadline;
        private long _burstStart;
        private int _generation;
        private int _running;
        private long _rounds;
        private int _errors;
        private volatile bool _exit;

        public BurstGroup(int threads, CurveProgramOptions options, Func<ulong> kernel, ulong reference)
        {
            _options = options;
            _kernel = kernel;
            _reference = reference;
            _heartbeats = Enumerable.Repeat(long.MaxValue, threads).ToArray();
            _threads = Enumerable.Range(0, threads)
                .Select(i => new Thread(() => Run(i)) { IsBackground = true, Name = $"MAUS rafale {i}", Priority = ThreadPriority.BelowNormal })
                .ToArray();
            foreach (var thread in _threads)
            {
                thread.Start();
            }
        }

        public bool Running => Volatile.Read(ref _running) > 0;

        public long Rounds => Interlocked.Read(ref _rounds);

        public int Errors => Volatile.Read(ref _errors);

        /// <summary>
        /// Plus long silence d'un fil encore en rafale (depuis le début de la rafale au plus), ou retard sur l'échéance
        /// quand un fil ne rend pas la main : les deux signes d'un cœur figé.
        /// </summary>
        public TimeSpan SilentFor
        {
            get
            {
                var now = Stopwatch.GetTimestamp();
                var start = Interlocked.Read(ref _burstStart);
                var oldest = long.MaxValue;
                for (var i = 0; i < _heartbeats.Length; i++)
                {
                    var beat = Interlocked.Read(ref _heartbeats[i]);
                    if (beat != long.MaxValue)
                    {
                        oldest = Math.Min(oldest, Math.Max(beat, start));
                    }
                }

                var silent = oldest == long.MaxValue ? 0 : now - oldest;
                var deadline = Interlocked.Read(ref _deadline);
                var overdue = Running && deadline > 0 && now > deadline ? now - deadline : 0;
                return TimeSpan.FromSeconds((double)Math.Max(silent, overdue) / Stopwatch.Frequency);
            }
        }

        public void Start(TimeSpan burst)
        {
            lock (_sync)
            {
                var now = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _burstStart, now);
                Interlocked.Exchange(ref _deadline, now + (long)(burst.TotalSeconds * Stopwatch.Frequency));
                Volatile.Write(ref _running, _threads.Length);
                _generation++;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>Échéance ramenée à maintenant : chaque fil s'arrête après son calcul en cours.</summary>
        public void StopNow() => Interlocked.Exchange(ref _deadline, 0);

        private void Run(int index)
        {
            var seen = 0;
            while (true)
            {
                lock (_sync)
                {
                    while (_generation == seen && !_exit)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_exit)
                    {
                        return;
                    }

                    seen = _generation;
                }

                Interlocked.Exchange(ref _heartbeats[index], Stopwatch.GetTimestamp());
                if (_options.Stall?.Invoke(1000 + index) == true)
                {
                    while (!_exit)
                    {
                        Thread.Sleep(20);
                    }

                    return;
                }

                try
                {
                    while (!_exit && Stopwatch.GetTimestamp() < Interlocked.Read(ref _deadline))
                    {
                        var result = _kernel();
                        var count = Interlocked.Increment(ref _rounds);
                        if (_options.Fault is { } fault)
                        {
                            result = fault(index, count, result);
                        }

                        if (result != _reference)
                        {
                            Interlocked.Increment(ref _errors);
                        }

                        Interlocked.Exchange(ref _heartbeats[index], Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception)
                {
                    // Une exception dans un fil d'arrière-plan fermerait MAUS : elle compte comme une erreur de la rafale.
                    Interlocked.Increment(ref _errors);
                }

                // Fil arrivé à l'échéance : il n'est plus surveillé jusqu'à la rafale suivante.
                Interlocked.Exchange(ref _heartbeats[index], long.MaxValue);
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _exit = true;
                Interlocked.Exchange(ref _deadline, 0);
                Monitor.PulseAll(_sync);
            }

            foreach (var thread in _threads)
            {
                // Un fil figé ne répond plus : il reste en arrière-plan et disparaît avec MAUS.
                thread.Join(TimeSpan.FromSeconds(2));
            }
        }
    }
}
