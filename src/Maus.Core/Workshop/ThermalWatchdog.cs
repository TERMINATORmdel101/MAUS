using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Composant dont la température protège un test.</summary>
public enum ThermalTarget
{
    /// <summary>Le processeur (tests du processeur, de la mémoire vive, cœur par cœur, programme Curve Optimizer).</summary>
    Cpu,

    /// <summary>La carte graphique (test de la mémoire vidéo).</summary>
    Gpu,
}

/// <summary>Ce que MAUS sait de l'arrêt automatique sur la chaleur du processeur.</summary>
public enum CpuTemperatureWatch
{
    /// <summary>Température et limite du processeur lues, et la lecture peut dépasser la limite : l'arrêt automatique sur ce critère fonctionne.</summary>
    Watched,

    /// <summary>
    /// Température lue, mais elle s'arrête à la limite donnée par le processeur : Intel la calcule d'après la distance à cette
    /// limite, jamais négative. MAUS voit que le processeur l'atteint, pas qu'il la dépasse : le test s'arrête donc s'il y
    /// reste trop longtemps (<see cref="ThermalWatchdog.AtLimitTime"/>).
    /// </summary>
    CappedAtLimit,

    /// <summary>Aucune mesure encore, pilote PawnIO installé : la température devrait arriver avec les premières mesures.</summary>
    Pending,

    /// <summary>Pilote PawnIO absent : la température du processeur n'est pas lue.</summary>
    NoDriver,

    /// <summary>Pilote installé, mais la température du processeur n'est pas lue (pilote occupé, en panne, ou processeur non reconnu).</summary>
    NoTemperature,

    /// <summary>Pilote installé, mais MAUS est en mode limité : il faut les droits administrateur pour s'en servir.</summary>
    NoRights,

    /// <summary>
    /// Température lue, mais limite inconnue (ni donnée par le processeur, ni dans le catalogue des seuils) : aucune alarme
    /// n'est inventée, donc pas d'arrêt sur ce critère.
    /// </summary>
    NoLimit,
}

/// <summary>
/// Chien de garde de l'arrêt automatique pendant un test. Les tests lui demandent plusieurs fois par seconde, depuis leurs
/// propres fils, s'ils doivent s'arrêter (<see cref="AbortReason"/>) ; il répond d'après la dernière mesure réussie et son
/// heure, jamais d'après une alarme figée :
/// <list type="bullet">
/// <item>alarme « danger » dans la dernière mesure réussie : arrêt ;</item>
/// <item>processeur dont la lecture s'arrête à sa limite, resté à cette limite pendant <see cref="AtLimitTime"/> : arrêt ;</item>
/// <item>aucune mesure réussie depuis <see cref="Grace"/> (mesures en échec ou bloquées) : arrêt par prudence, plus rien ne
/// surveille la chaleur ;</item>
/// <item>la température du composant testé était lue avec sa limite, et ne l'est plus depuis <see cref="Grace"/> (pilote
/// PawnIO qui ne répond plus, par exemple) : arrêt par prudence.</item>
/// </list>
/// Une température jamais lue (pas de PawnIO, limite inconnue) n'arrête rien : l'utilisateur en est prévenu à la confirmation
/// du test (<see cref="Caveat"/>), ou pendant le test si elle était attendue et n'arrive pas (<see cref="Notice"/>).
/// </summary>
public sealed class ThermalWatchdog
{
    /// <summary>
    /// Délai de grâce : trois mesures à l'intervalle le plus lent proposé dans les paramètres (5 s), et plus du double du délai
    /// au-delà duquel les valeurs du pilote ne sont plus affichées (<see cref="CombinedSensorSource.MaxAge"/>, 6 s).
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Durée à la limite avant l'arrêt, choisie par MAUS, sans source publiée : assez longue pour qu'un pic de charge n'arrête
    /// rien, assez courte pour qu'un ventilateur en panne ne laisse pas un test de plusieurs heures tourner ainsi. La limite
    /// d'Intel n'est pas un danger immédiat : le test est arrêté par prudence, et il ne prouve plus la stabilité à pleine vitesse.
    /// </summary>
    public static readonly TimeSpan AtLimitTime = TimeSpan.FromMinutes(10);

    private readonly Func<TimeSpan> _clock;
    private readonly Lock _gate = new();
    private bool _running;
    private ThermalTarget _target;
    private bool _pending;
    private TimeSpan _startedAt;
    private TimeSpan? _lastSampleAt;
    private TimeSpan? _lastWatchedAt;
    private string? _danger;
    private TimeSpan? _atLimitSince;
    private string? _lastFailure;
    private CpuTemperatureWatch _cpuWatch;
    private int _failures;

    /// <param name="clock">
    /// Horloge monotone (temps écoulé) ; par défaut, le compteur de Windows, insensible aux changements d'heure pendant un
    /// test de plusieurs heures.
    /// </param>
    public ThermalWatchdog(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (static () => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>
    /// État de l'arrêt automatique sur la chaleur du processeur d'après la dernière mesure (sans mesure : présence du pilote).
    /// La limite est la même que celle des alarmes (<see cref="SensorAlarms.Check"/>) : celle du processeur, sinon celle du catalogue.
    /// </summary>
    /// <param name="engineElevated">Le moteur a les droits administrateur ; <c>false</c> en mode limité, où le pilote installé ne sert pas.</param>
    public static CpuTemperatureWatch Assess(SensorSnapshot? latest, int? cpuMaxC, bool pawnIoInstalled, bool engineElevated = true) => latest switch
    {
        null or { CpuTemperatureC: null } when pawnIoInstalled && !engineElevated => CpuTemperatureWatch.NoRights,
        null => pawnIoInstalled ? CpuTemperatureWatch.Pending : CpuTemperatureWatch.NoDriver,
        { CpuTemperatureC: null } => pawnIoInstalled ? CpuTemperatureWatch.NoTemperature : CpuTemperatureWatch.NoDriver,
        { CpuTjMaxC: not null } => CpuTemperatureWatch.CappedAtLimit,
        _ when cpuMaxC is null => CpuTemperatureWatch.NoLimit,
        _ => CpuTemperatureWatch.Watched,
    };

    /// <summary>La température du processeur est lue : si elle ne l'est plus, le test s'arrête par prudence.</summary>
    private static bool IsRead(CpuTemperatureWatch watch) => watch is CpuTemperatureWatch.Watched or CpuTemperatureWatch.CappedAtLimit;

    /// <summary>Avertissement : le test ne pourra pas s'arrêter sur la chaleur du processeur, et pourquoi ; <c>null</c> sinon.</summary>
    public static string? Caveat(CpuTemperatureWatch watch) => watch switch
    {
        CpuTemperatureWatch.NoDriver => T("Sans le pilote PawnIO, MAUS ne lit pas la température du processeur : il ne peut pas arrêter le test sur ce critère. Le processeur se protège lui-même en ralentissant, mais installez PawnIO (onglet En direct) pour suivre sa température."),
        CpuTemperatureWatch.NoTemperature => T("Le pilote PawnIO est installé, mais MAUS ne lit pas la température du processeur en ce moment (voir l'onglet En direct) : il ne peut pas arrêter le test sur ce critère. Le processeur se protège lui-même en ralentissant. Un autre logiciel de surveillance du matériel occupe peut-être le pilote : fermez-le, puis relancez MAUS."),
        CpuTemperatureWatch.NoRights => T("Le pilote PawnIO est installé, mais MAUS est en mode limité : il ne lit pas la température du processeur et ne peut pas arrêter le test sur ce critère. Le processeur se protège lui-même en ralentissant. Pour suivre sa température : bouton « Activer les capteurs avancés » (onglet En direct)."),
        CpuTemperatureWatch.NoLimit => T("MAUS lit la température du processeur, mais ne connaît pas la limite publiée pour ce modèle : il ne peut pas arrêter le test sur ce critère. Le processeur se protège lui-même en ralentissant ; gardez un œil sur la température affichée."),
        CpuTemperatureWatch.CappedAtLimit => T("Sur ce processeur, la température lue s'arrête à sa limite : MAUS voit qu'il l'atteint, pas qu'il la dépasse. Il arrête donc le test si le processeur reste à sa limite pendant {0:0} minutes, une durée choisie par MAUS. Le processeur se protège aussi lui-même en ralentissant, puis en s'éteignant ; gardez un œil sur la température affichée.", AtLimitTime.TotalMinutes),
        _ => null,
    };

    /// <summary>
    /// Début d'un test, avant sa première mesure. <paramref name="cpuAtStart"/> : ce que l'utilisateur a lu à la confirmation
    /// (<see cref="Assess"/>). Température lue au départ : elle doit le rester, le délai de grâce court dès maintenant.
    /// </summary>
    public void Begin(ThermalTarget target, CpuTemperatureWatch cpuAtStart)
    {
        lock (_gate)
        {
            var now = _clock();
            _running = true;
            _target = target;
            _startedAt = now;
            _lastSampleAt = null;
            _danger = null;
            _atLimitSince = null;
            _lastFailure = null;
            _failures = 0;
            _cpuWatch = cpuAtStart;
            _lastWatchedAt = target == ThermalTarget.Cpu && IsRead(cpuAtStart) ? now : null;
            _pending = target == ThermalTarget.Cpu && cpuAtStart == CpuTemperatureWatch.Pending;
        }
    }

    /// <summary>Fin du test : plus aucune raison d'arrêter.</summary>
    public void End()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    /// <summary>
    /// Mesure réussie (après chaque mesure en direct, test en cours ou non). Les alarmes sont recalculées ici, d'après cette
    /// mesure. Rend le nombre d'échecs qui la précédaient (0 d'habitude).
    /// </summary>
    public int Record(SensorSnapshot snapshot, int? cpuMaxC, bool pawnIoInstalled, bool engineElevated = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var danger = SensorAlarms.Check(snapshot, cpuMaxC).FirstOrDefault(a => a.Level == AlarmLevel.Danger)?.Message;
        var cpuWatch = Assess(snapshot, cpuMaxC, pawnIoInstalled, engineElevated);
        var gpuWatched = snapshot.Gpus.Any(g => g is { TemperatureC: not null, SlowdownTemperatureC: not null });
        var atLimit = snapshot is { CpuTemperatureC: { } cpu, CpuTjMaxC: { } max } && ThermalRules.CpuAtLimit(cpu, max);
        lock (_gate)
        {
            var now = _clock();
            var failures = _failures;
            _failures = 0;
            _lastFailure = null;
            _lastSampleAt = now;
            _danger = danger;
            _cpuWatch = cpuWatch;
            _atLimitSince = atLimit && _target == ThermalTarget.Cpu ? _atLimitSince ?? now : null;
            if (_target == ThermalTarget.Cpu ? IsRead(cpuWatch) : gpuWatched)
            {
                _lastWatchedAt = now;
            }

            return failures;
        }
    }

    /// <summary>Mesure en échec (exception). Rend le nombre d'échecs de suite, celui-ci compris.</summary>
    public int RecordFailure(string message)
    {
        lock (_gate)
        {
            _lastFailure = message;
            return ++_failures;
        }
    }

    /// <summary>Raison d'arrêter le test maintenant, ou <c>null</c>. Appelée par les tests, depuis n'importe quel fil.</summary>
    public string? AbortReason()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return null;
            }

            if (_danger is { } danger)
            {
                return danger;
            }

            var now = _clock();
            if (_atLimitSince is { } since && now - since >= AtLimitTime)
            {
                return T("Le processeur reste à sa limite de température depuis {0:0} minutes : il ralentit pour se protéger, et le test ne prouve plus sa stabilité à pleine vitesse. Test arrêté par prudence : vérifiez le refroidissement (ventilateurs, poussière, pâte thermique) avant de recommencer.", (now - since).TotalMinutes);
            }

            var silent = now - (_lastSampleAt ?? _startedAt);
            if (silent > Grace)
            {
                var reason = T("Les mesures en direct ne répondent plus depuis {0:0} s : le test s'est arrêté par prudence, car sans elles sa protection contre la surchauffe ne peut plus jouer. Relancez le test ; si cela se reproduit, redémarrez MAUS.", silent.TotalSeconds);
                return _lastFailure is { } failure ? reason + " " + T("Dernière erreur : {0}", failure) : reason;
            }

            if (_lastWatchedAt is { } seen && now - seen > Grace)
            {
                return _target == ThermalTarget.Cpu
                    ? T("La température du processeur n'est plus lue depuis {0:0} s : le test s'est arrêté par prudence, car sans elle sa protection contre la surchauffe ne peut plus jouer. Le pilote PawnIO ne répond peut-être plus (un autre logiciel de surveillance du matériel l'occupe parfois) : fermez les autres logiciels de surveillance, puis relancez le test.", (now - seen).TotalSeconds)
                    : T("La température de la carte graphique n'est plus lue depuis {0:0} s : le test s'est arrêté par prudence, car sans elle sa protection contre la surchauffe ne peut plus jouer. Relancez le test.", (now - seen).TotalSeconds);
            }

            return null;
        }
    }

    /// <summary>
    /// Avertissement pendant un test dont la température du processeur était attendue (<see cref="CpuTemperatureWatch.Pending"/> :
    /// aucune mesure avant la confirmation, pilote installé) mais n'est toujours pas lue, avec sa limite, après le délai de
    /// grâce. Le test continue, comme sans PawnIO ; l'utilisateur sait qu'il ne s'arrêtera pas sur ce critère.
    /// </summary>
    public string? Notice()
    {
        lock (_gate)
        {
            return _running && _pending && _lastWatchedAt is null && _clock() - _startedAt > Grace ? Caveat(_cpuWatch) : null;
        }
    }
}
