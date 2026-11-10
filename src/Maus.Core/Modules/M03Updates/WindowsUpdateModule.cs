using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M03Updates;

/// <summary>
/// Module 3 — Mises à jour Windows (hors pilotes). En V0.1 (audit seul), le module lit la fin de maintenance de la version
/// installée, l'état des services et réglages de Windows Update, les mises à jour en attente (recherche WUA sans pilotes),
/// l'âge du dernier correctif et des définitions Defender. L'installation arrive en V0.2.
/// </summary>
public sealed class WindowsUpdateModule : IAuditModule
{
    /// <summary>
    /// Logiciels (obligatoires ou facultatifs) et pilotes proposés, en une seule recherche : <c>OR</c> n'est admis qu'au
    /// premier niveau du critère (IUpdateSearcher::Search, critère <c>Type</c> = 'Software' ou 'Driver'). Les pilotes sont
    /// comptés à part (constat « mises à jour de pilotes »), jamais avec les correctifs de Windows.
    /// </summary>
    internal const string SearchCriteria =
        "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0"
        + " or IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=1"
        + " or IsInstalled=0 and IsHidden=0 and Type='Driver'";

    /// <summary>Page « Mises à jour facultatives » de Windows Update (Microsoft Learn, « Launch Windows Settings »).</summary>
    internal const string OptionalUpdatesPage = "ms-settings:windowsupdate-optionalupdates";

    /// <summary>
    /// Page d'accueil de Windows Update (même source). Jamais « windowsupdate-action », qui lance une recherche dès l'ouverture :
    /// le bouton « Ouvrir dans Windows » ne doit rien déclencher.
    /// </summary>
    internal const string WindowsUpdatePage = "ms-settings:windowsupdate";

    /// <summary>
    /// Page « Sécurité Windows » des Paramètres (même source), qui liste les zones de protection ; aucune adresse
    /// « ms-settings: » n'ouvre directement « Protection contre les virus et menaces ».
    /// </summary>
    internal const string WindowsSecurityPage = "ms-settings:windowsdefender";

    internal const string QfeQuery = "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering";
    internal const string DefenderQuery =
        "SELECT AMRunningMode, AntivirusEnabled, AntivirusSignatureAge, AntivirusSignatureLastUpdated, AntivirusSignatureVersion FROM MSFT_MpComputerStatus";
    internal const string AntivirusQuery = "SELECT displayName, productState FROM AntiVirusProduct";

    internal const string UxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    internal const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    internal const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    internal const int WarningDaysBeforeEndOfService = 60;
    internal const int MaxInstallAgeDays = 45;
    internal const int MaxSearchAgeDays = 14;
    internal const int MaxSignatureAgeDays = 3;

    /// <summary>Clés de la retenue (Microsoft Learn, « Safeguard holds for Windows ») ; lisibles sans droits, vérifié le 2/11/2026.</summary>
    internal const string HoldStatusKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Appraiser\GWX";
    internal const string HoldIndicatorsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\TargetVersionUpgradeExperienceIndicators";

    /// <summary>
    /// Choix de MAUS, sans source publiée : Microsoft ne définit pas d'« échec en boucle ». À partir de ce nombre d'échecs d'une même
    /// mise à jour de Windows, sans réussite ensuite, le constat cesse d'être une simple information.
    /// </summary>
    internal const int LoopFailureCount = 3;

    /// <summary>
    /// Choix de MAUS, sans source publiée : un échec plus ancien n'est plus cité. Une mise à jour remplacée par une plus récente
    /// n'est pas forcément retentée : elle resterait alors « sans réussite » pour toujours.
    /// </summary>
    internal const int RecentFailureDays = 90;

    /// <summary>Lignes d'historique lues, les plus récentes d'abord (Microsoft Learn, « IUpdateSearcher::QueryHistory »).</summary>
    internal const int MaxHistoryEntries = 500;

    /// <summary>Services dont Windows Update dépend ; « Start = 4 » signifie désactivé.</summary>
    internal static (string Name, string Label)[] UpdateServices =>
    [
        ("wuauserv", "Windows Update"),
        ("UsoSvc", T("Orchestrateur de mises à jour")),
        ("BITS", T("Transfert intelligent en arrière-plan (BITS)")),
        ("WaaSMedicSvc", "Windows Update Medic"),
    ];

    private const int ServiceDisabled = 4;
    private const int MaxListedTitles = 3;

    private static string VersionCategory => T("Version de Windows");
    private const string UpdateCategory = "Windows Update";
    private const string DefenderCategory = "Defender";

    /// <summary>Fins de pause (ISO 8601) écrites par Paramètres > Windows Update ; la pause des mises à jour de fonctionnalité ne bloque pas les correctifs.</summary>
    private static readonly string[] PauseValueNames = ["PauseUpdatesExpiryTime", "PauseQualityUpdatesEndTime"];

    private static readonly TimeSpan DefaultSearchTimeout = TimeSpan.FromSeconds(150);

    /// <summary>L'historique est local : au-delà, le service ne répond pas et le constat dit « non lu », sans retenir l'audit.</summary>
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<WindowsLifecycleCatalog> Catalog = new(WindowsLifecycleCatalog.LoadEmbedded);

    private readonly IWindowsUpdateAgent _agent;
    private readonly TimeSpan _searchTimeout;

    public WindowsUpdateModule()
        : this(new ComWindowsUpdateAgent(), DefaultSearchTimeout)
    {
    }

    internal WindowsUpdateModule(IWindowsUpdateAgent agent, TimeSpan searchTimeout)
    {
        _agent = agent;
        _searchTimeout = searchTimeout;
    }

    public string Id => "M03";

    public string Title => T("Mises à jour Windows");

    public int Order => 30;

    /// <summary>La recherche Windows Update interroge le serveur de mises à jour et peut durer plusieurs minutes.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(180);

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var today = DateOnly.FromDateTime(context.Now.LocalDateTime);
        var findings = new List<Finding>
        {
            DetectSupport(context.Windows, today),
            DetectServices(context.Registry),
            DetectAutomaticUpdates(context.Registry, context.Now, context.Hardware.IsManaged),
        };

        // Lu avant notre propre recherche, pour refléter l'activité de Windows et non celle de MAUS.
        var automaticResults = ReadAutomaticUpdatesResults();

        // La recherche en ligne tourne sur son propre fil : les lectures locales se font pendant qu'elle attend le serveur
        // (liste des correctifs : 0,6 à 0,8 s mesurés sur le PC du porteur), au lieu d'allonger la fin de l'audit.
        var searching = SearchAsync(!context.Offline, cancellationToken);
        var readingHistory = ReadHistoryAsync(cancellationToken);
        var lastInstall = DetectLastInstall(context.Cim, today);
        var defender = DetectDefenderSignatures(context.Cim);
        var latestToggle = DetectLatestUpdatesToggle(context.Registry);
        var source = DetectUpdateSource(context.Registry);
        var hold = DetectSafeguardHold(context.Registry);
        var history = await readingHistory.ConfigureAwait(false);
        var search = await searching.ConfigureAwait(false);

        findings.Add(DetectPendingUpdates(search));
        findings.Add(lastInstall);
        findings.Add(DetectLastSearch(automaticResults, context.Now));
        findings.Add(defender);
        findings.Add(DetectOptionalUpdates(search));
        findings.Add(DetectDriverUpdates(search));
        findings.Add(DetectFailedUpdates(history, search.Updates, context.Now));
        findings.Add(hold);
        findings.Add(latestToggle);
        findings.Add(source);
        return findings;
    }

    private static Finding DetectSupport(WindowsInfo windows, DateOnly today)
    {
        const string id = "M03.windows-support";
        var title = T("Prise en charge de la version de Windows");
        var explanation = T("Chaque version de Windows 11 reçoit des correctifs de sécurité pendant une durée limitée : "
            + "24 mois en Famille et Pro, 36 mois en Entreprise et Éducation. Passé cette date, Windows Update ne corrige plus "
            + "les failles découvertes dans cette version.");
        var catalog = Catalog.Value;
        if (windows.Build <= 0)
        {
            return Finding.Unknown(id, title, T("Numéro de version de Windows illisible dans le registre."), VersionCategory);
        }

        if (!windows.IsWindows11)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = VersionCategory,
                Status = FindingStatusExtensions.ForDeviation(Severity.High),
                Severity = Severity.High,
                Current = T("Windows 10 (build {0}) : support terminé le {1}", windows.FullBuild, ShortDate(catalog.Windows10EndOfSupport)),
                Expected = T("Windows 11 23H2 ou plus récent"),
                Explanation = T("MAUS vise Windows 11 uniquement : Windows 10 est hors périmètre. Sans correctifs de sécurité, "
                    + "le PC reste exposé aux failles découvertes depuis la fin du support."),
                Advice = T("Passez à Windows 11 si le PC est compatible. Sinon, le programme de mises à jour de sécurité étendues (ESU) "
                    + "prolonge les correctifs pour une durée limitée."),
            };
        }

        var release = catalog.Find(windows.Build);
        if (release is null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = VersionCategory,
                Status = FindingStatus.Info,
                Current = $"build {windows.FullBuild}{(windows.DisplayVersion.Length > 0 ? $" ({windows.DisplayVersion})" : string.Empty)}",
                Explanation = windows.Build > catalog.NewestBuild
                    ? T("Cette version est plus récente que le catalogue de MAUS (préversion Windows Insider ou nouvelle version) : sa date de fin de maintenance n'est pas connue.")
                    : T("Cette version ne figure pas dans le catalogue de MAUS (préversion Windows Insider ?) : sa date de fin de maintenance n'est pas connue."),
            };
        }

        var channel = WindowsLifecycleCatalog.Classify(windows.EditionId);
        var end = release.EndOfService(channel);
        var daysLeft = end.DayNumber - today.DayNumber;
        var severity = daysLeft < 0 ? Severity.High : daysLeft < WarningDaysBeforeEndOfService ? Severity.Medium : Severity.Info;
        var channelLabel = WindowsLifecycleCatalog.Describe(channel);
        var current = daysLeft < 0
            ? T("Windows 11 {0} ({1}) : correctifs arrêtés depuis le {2}", release.Version, channelLabel, ShortDate(end))
            : T("Windows 11 {0} ({1}) : correctifs jusqu'au {2}, dans {3}", release.Version, channelLabel, ShortDate(end), Plural(daysLeft, T("jour"), T("jours")));

        string? advice = null;
        var fixable = false;
        if (severity != Severity.Info)
        {
            var status = daysLeft < 0
                ? T("Windows 11 {0} ne reçoit plus de correctifs de sécurité depuis le {1}.", release.Version, ShortDate(end))
                : T("Windows 11 {0} ne recevra plus de correctifs de sécurité après le {1}.", release.Version, ShortDate(end));
            if (release.EnablementPackage is { } package && channel is ServicingChannel.HomePro or ServicingChannel.EnterpriseEducation)
            {
                advice = T("{0} Passez à {1} par le package d'activation {2} : un seul redémarrage, "
                    + "applications, fichiers et réglages conservés. Il apparaît dans Paramètres > Windows Update.", status, release.EnablementTarget, package);
                if (release.EnablementMinimumUbr is { } minimum && windows.Ubr < minimum)
                {
                    advice += T(" Installez d'abord les mises à jour cumulatives en attente (révision {0}.{1} requise, "
                        + "{2} installée).", release.Build, minimum, windows.FullBuild);
                }

                fixable = true;
            }
            else if (catalog.Releases.Where(r => !r.NewDevicesOnly && r.Build > release.Build).MaxBy(r => r.Build) is { } target)
            {
                advice = T("{0} Installez la mise à jour de fonctionnalité vers Windows 11 {1} depuis Paramètres > Windows Update, "
                    + "ou avec l'Assistant d'installation de Windows 11. Sauvegardez vos données avant.", status, target.Version);
            }
            else
            {
                advice = T("{0} Installez la version suivante de Windows 11 dès qu'elle est proposée dans Paramètres > Windows Update.", status);
            }
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = VersionCategory,
            Status = severity == Severity.Info ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(severity),
            Severity = severity == Severity.Info ? Severity.High : severity,
            Current = current,
            Expected = T("version maintenue encore au moins {0} jours", WarningDaysBeforeEndOfService),
            Explanation = explanation,
            Advice = advice,
            SettingsPage = advice is null ? null : WindowsUpdatePage,
            Fixable = fixable,
        };
    }

    private static Finding DetectServices(IRegistryReader registry)
    {
        const string id = "M03.update-services";
        var title = T("Services de Windows Update");
        var disabled = new List<string>();
        var missing = new List<string>();
        var denied = false;
        foreach (var (name, label) in UpdateServices)
        {
            var key = $@"{ServicesKey}\{name}";
            try
            {
                if (!registry.KeyExists(RegistryHive.LocalMachine, key))
                {
                    missing.Add(label);
                }
                else if (registry.GetDword(RegistryHive.LocalMachine, key, "Start") == ServiceDisabled)
                {
                    disabled.Add(label);
                }
            }
            catch (MausAccessDeniedException)
            {
                denied = true;
            }
        }

        if (disabled.Count == 0 && missing.Count == 0 && denied)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        var severity = disabled.Count > 0 ? Severity.High : missing.Count > 0 ? Severity.Medium : Severity.Info;
        var details = new List<string>();
        if (disabled.Count > 0)
        {
            details.Add(T("désactivés : ") + string.Join(", ", disabled));
        }

        if (missing.Count > 0)
        {
            details.Add(T("absents : ") + string.Join(", ", missing));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = severity == Severity.Info ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(severity),
            Severity = severity == Severity.Info ? Severity.High : severity,
            Current = details.Count == 0 ? T("aucun service désactivé") : string.Join(" ; ", details),
            Expected = T("aucun service désactivé"),
            Explanation = T("Windows Update s'appuie sur plusieurs services (Windows Update, Orchestrateur, BITS, Medic). "
                + "Certains outils d'« optimisation » les désactivent : le PC ne reçoit alors plus aucun correctif de sécurité."),
            Advice = details.Count == 0
                ? null
                : T("Si un outil tiers a désactivé ces services, annulez ce réglage dans cet outil. "
                    + "MAUS peut remettre le démarrage d'origine : correction « Services de Windows Update », page Corrections."),
            Fixable = disabled.Count > 0,
        };
    }

    private static Finding DetectAutomaticUpdates(IRegistryReader registry, DateTimeOffset now, bool isManaged)
    {
        const string id = "M03.automatic-updates";
        var title = T("Installation automatique des mises à jour");
        var reasons = new List<string>();
        var paused = false;
        var byPolicy = false;
        try
        {
            if (registry.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate") == 1)
            {
                reasons.Add(T("désactivée par une stratégie (NoAutoUpdate)"));
                byPolicy = true;
            }
            else if (registry.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "AUOptions") == 2)
            {
                reasons.Add(T("simple notification, sans téléchargement (stratégie AUOptions = 2)"));
                byPolicy = true;
            }

            if (registry.GetDword(RegistryHive.LocalMachine, PolicyKey, "DisableWindowsUpdateAccess") == 1)
            {
                reasons.Add(T("accès à Windows Update bloqué par une stratégie (DisableWindowsUpdateAccess)"));
                byPolicy = true;
            }

            var pausedUntil = PauseValueNames
                .Select(name => UpdateParsers.ParseIsoDate(registry.GetString(RegistryHive.LocalMachine, UxSettingsKey, name)))
                .OfType<DateTimeOffset>()
                .Where(date => date > now)
                .DefaultIfEmpty()
                .Max();
            if (pausedUntil != default)
            {
                paused = true;
                reasons.Add(T("en pause jusqu'au {0}", ShortDate(DateOnly.FromDateTime(pausedUntil.LocalDateTime))));
            }
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        var advice = new List<string>();
        if (paused)
        {
            advice.Add(T("Reprenez les mises à jour : Paramètres > Windows Update > Reprendre les mises à jour. "
                + "Une courte pause pour attendre les premiers retours sur une mise à jour est un choix qui se défend ; une longue pause laisse ouvertes les failles corrigées entre-temps."));
        }

        if (byPolicy)
        {
            advice.Add(isManaged
                ? T("Ce PC est géré par une organisation : ces stratégies relèvent de son service informatique.")
                : T("Une stratégie bloque l'installation automatique : si vous ne l'avez pas voulue, supprimez-la (gpedit.msc ou l'outil qui l'a posée)."));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = reasons.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = reasons.Count == 0 ? T("active") : string.Join(" ; ", reasons),
            Expected = T("active, sans pause"),
            Explanation = T("Windows installe seul les correctifs de sécurité publiés chaque mois. Une pause ou une stratégie qui "
                + "bloque cette installation laisse le PC exposé aux failles connues."),
            Advice = advice.Count == 0 ? null : string.Join(' ', advice),

            // Seule la pause se lève dans les Paramètres ; une stratégie se retire ailleurs (gpedit.msc, outil qui l'a posée).
            SettingsPage = paused ? WindowsUpdatePage : null,
        };
    }

    private AutomaticUpdatesResults? ReadAutomaticUpdatesResults()
    {
        try
        {
            return _agent.GetAutomaticUpdatesResults();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<UpdateHistoryEntry>?> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => _agent.GetHistory(MaxHistoryEntries), cancellationToken).WaitAsync(HistoryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Délai dépassé ou toute autre erreur de l'historique : « non lu » pour ce seul constat, les autres restent.
            return null;
        }
    }

    private async Task<SearchOutcome> SearchAsync(bool online, CancellationToken cancellationToken)
    {
        try
        {
            var updates = await _agent.SearchAsync(SearchCriteria, online, _searchTimeout, cancellationToken).ConfigureAwait(false);
            return new SearchOutcome(updates.DistinctBy(u => u.UpdateId, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (TimeoutException)
        {
            return new SearchOutcome(null, T("aucune réponse en {0} s (connexion lente ou serveur surchargé)", _searchTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
        }
        catch (COMException ex)
        {
            return new SearchOutcome(null, UpdateParsers.DescribeSearchError(ex.HResult));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or MausAccessDeniedException)
        {
            return new SearchOutcome(null, T("accès refusé"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DataSourceUnavailableException)
        {
            return new SearchOutcome(null, ex.Message.TrimEnd('.'));
        }
    }

    private static Finding DetectPendingUpdates(SearchOutcome search)
    {
        const string id = "M03.pending-updates";
        var title = T("Mises à jour Windows en attente (hors pilotes)");
        if (search.Updates is null)
        {
            return Finding.Unknown(id, title, T("Recherche Windows Update impossible : {0}.", search.Error), UpdateCategory);
        }

        var security = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Security).ToList();
        var other = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Other).ToList();
        var definitions = search.Updates.Count(u => UpdateParsers.Classify(u) == PendingUpdateKind.Definitions);
        var explanation = T("Windows Update propose des mises à jour de Windows pas encore installées : correctifs de sécurité, "
            + ".NET, outil de suppression de logiciels malveillants, définitions Defender. Les pilotes sont exclus : ils relèvent du Module 9.");
        var definitionsNote = definitions == 0
            ? null
            : T("{0} de définitions Defender en cours (installées automatiquement plusieurs fois par jour)", Plural(definitions, T("mise à jour"), T("mises à jour")));
        if (security.Count == 0 && other.Count == 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = definitionsNote is null ? T("aucune") : T("aucune en dehors de : {0}", definitionsNote),
                Expected = T("aucune"),
                Explanation = explanation,
            };
        }

        var severity = security.Count > 0 ? Severity.Medium : Severity.Low;
        var counts = new List<string>();
        if (security.Count > 0)
        {
            counts.Add(T("{0} de sécurité", security.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (other.Count > 0)
        {
            counts.Add(Plural(other.Count, T("autre"), T("autres")));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = $"{string.Join(T(" et "), counts)} : {TitleList([.. security, .. other])}"
                + (definitionsNote is null ? string.Empty : T(" ; en plus, {0}", definitionsNote)),
            Expected = T("aucune"),
            Explanation = explanation,
            Advice = T("Installez-les depuis Paramètres > Windows Update : MAUS n'installe et ne bloque aucune mise à jour de Windows. "
                + "Aucun gain de performance n'est attendu : ces mises à jour servent la sécurité et la stabilité. "
                + "Aucune mise à jour n'est tout à fait sans risque : créez d'abord un point de restauration (page Historique), gardez une sauvegarde récente, "
                + "et regardez si votre matériel est cité dans les problèmes connus publiés par Microsoft : https://learn.microsoft.com/windows/release-health/"),
            SettingsPage = WindowsUpdatePage,
        };
    }

    /// <summary>
    /// Information seulement, ni blocage ni réglage. Ne dit que ce que Microsoft publie (« Update release cycle for Windows clients »,
    /// https://learn.microsoft.com/windows/deployment/update/release-cycle) : aperçu facultatif sans correctifs de sécurité, en général la quatrième
    /// semaine du mois, pour valider plus tôt ; contenu repris par le correctif de sécurité du mois suivant. Les correctifs de sécurité ne sont pas concernés.
    /// </summary>
    private static Finding DetectOptionalUpdates(SearchOutcome search)
    {
        const string id = "M03.optional-updates";
        var title = T("Mises à jour facultatives disponibles");
        if (search.Updates is null)
        {
            return Finding.Unknown(id, title, T("Recherche Windows Update impossible : {0}.", search.Error), UpdateCategory);
        }

        var optional = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Optional).ToList();
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Measures = new Dictionary<string, double> { [Reporting.UpdateReadiness.CountMeasure] = optional.Count },
            Current = optional.Count == 0 ? T("aucune") : $"{Plural(optional.Count, T("facultative"), T("facultatives"))} : {TitleList(optional)}",
            Explanation = T("Microsoft publie, en général la quatrième semaine du mois, un aperçu facultatif sans correctifs de sécurité, qui sert le plus souvent à valider plus tôt "
                + "le contenu du correctif de sécurité du mois suivant : ce correctif reprend ce contenu. Windows Update peut aussi proposer des pilotes facultatifs : voir le constat sur les pilotes juste après. "
                + "Les correctifs de sécurité ne sont pas concernés par cette information."),
            Advice = optional.Count == 0
                ? null
                : T("Sauf si une mise à jour facultative corrige un problème que vous avez, vous pouvez attendre : pour l'aperçu mensuel, le correctif de sécurité du mois suivant reprend son contenu. "
                    + "Si possible, faites d'abord un point de restauration (page Historique)."),

            // Page d'accueil de Windows Update, point de départ de toute installation (Options avancées > Mises à jour facultatives comprises).
            SettingsPage = optional.Count == 0 ? null : WindowsUpdatePage,
            Fixable = optional.Count > 0,
        };
    }

    /// <summary>
    /// Pilotes proposés par Windows Update. Information seulement : Windows les range dans les mises à jour facultatives et
    /// précise lui-même, sur cette page, qu'ils servent surtout « si vous rencontrez un problème spécifique ». Microsoft écrit que Windows Update installe
    /// les pilotes recommandés automatiquement et pas les pilotes facultatifs (« Automatically get recommended and updated hardware drivers »,
    /// https://support.microsoft.com/windows/hardware/drivers/automatically-get-recommended-and-updated-hardware-drivers). « Par défaut » : le Module 9 laisse
    /// à l'utilisateur le choix de bloquer ces pilotes, et ce texte ne le contredit pas.
    /// </summary>
    private static Finding DetectDriverUpdates(SearchOutcome search)
    {
        const string id = "M03.driver-updates";
        var title = T("Mises à jour de pilotes proposées par Windows Update");
        if (search.Updates is null)
        {
            return Finding.Unknown(id, title, T("Recherche Windows Update impossible : {0}.", search.Error), UpdateCategory);
        }

        // Facultatifs (BrowseOnly) : ils attendent le choix de l'utilisateur dans les mises à jour facultatives.
        // Les autres sont installés automatiquement par Windows Update et n'apparaissent pas sur cette page.
        var drivers = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Driver).ToList();
        var optional = drivers.Where(u => u.BrowseOnly).ToList();
        var automatic = drivers.Where(u => !u.BrowseOnly).ToList();
        var parts = new List<string>();
        if (optional.Count > 0)
        {
            parts.Add(T("{0} : {1}", Plural(optional.Count, T("pilote facultatif"), T("pilotes facultatifs")), TitleList(optional)));
        }

        if (automatic.Count > 0)
        {
            parts.Add(T("{0} : {1}", Plural(automatic.Count, T("pilote installé automatiquement"), T("pilotes installés automatiquement")), TitleList(automatic)));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = parts.Count == 0 ? T("aucune") : string.Join(" ; ", parts),
            Explanation = T("Windows Update propose parfois des pilotes pour vos périphériques. Les pilotes facultatifs attendent votre choix dans les mises à jour facultatives : "
                + "Windows indique lui-même qu'ils servent surtout si un périphérique pose un problème précis. Les autres sont installés automatiquement par Windows Update. "
                + "Un pilote reste un changement qui peut apporter un problème : si un périphérique fonctionne moins bien après une mise à jour, "
                + "le Gestionnaire de périphériques permet de revenir au pilote précédent (Propriétés > Pilote > Restaurer le pilote)."),
            Advice = optional.Count == 0
                ? null
                : T("N'installez un pilote facultatif que si un appareil pose problème ; sinon, par défaut, Windows Update installe de lui-même les pilotes recommandés. "
                    + "Pour la carte graphique, préférez la version du constructeur (Module 9 et Atelier > Pilotes). Faites d'abord un point de restauration (page Historique), "
                    + "puis bouton « Ouvrir dans Windows » (Windows Update > Options avancées > Mises à jour facultatives) et cochez ce pilote."),
            SettingsPage = optional.Count == 0 ? null : OptionalUpdatesPage,
        };

    }

    /// <summary>
    /// Ce que Windows a noté, sans rien lancer. MAUS cite le code et jamais sa cause : la cause se cherche avec les outils de Microsoft.
    /// Pilotes et applications du Store restent de simples informations, pour ne pas crier à la panne de Windows. Les définitions de
    /// Microsoft Defender ne comptent jamais dans le seuil : la suivante remplace celle qui a échoué. Une mise à jour de Windows sans
    /// réussite notée n'est « à améliorer » que si Windows la propose encore (<paramref name="pending"/>, lu par le même audit) ; sans
    /// cette liste, que si aucune autre mise à jour de Windows ne s'est installée depuis son dernier échec.
    /// </summary>
    private static Finding DetectFailedUpdates(IReadOnlyList<UpdateHistoryEntry>? history, IReadOnlyList<PendingUpdate>? pending, DateTimeOffset now)
    {
        const string id = "M03.failed-updates";
        var title = T("Mises à jour qui ont échoué");
        if (history is null)
        {
            return Finding.Unknown(id, title, T("Windows ne donne pas l'historique de ses mises à jour."), UpdateCategory);
        }

        var explanation = T("MAUS lit l'historique de Windows Update, sans rien lancer : quelle mise à jour a échoué, combien de fois, avec quel code, et si elle a réussi ensuite. "
            + "Un échec peut se régler seul à l'essai suivant. MAUS cite le code sans l'expliquer. Les pilotes et les applications du Store sont comptés à part : "
            + "leur échec n'est pas un échec de Windows. Ce tri se fait d'après ce que note l'historique ; ce qui n'est pas reconnu reste avec Windows.");
        if (history.Count == 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Info,
                Current = T("historique vide"),
                Explanation = explanation,
            };
        }

        var since = now.UtcDateTime.AddDays(-RecentFailureDays);
        var failed = UpdateParsers.SummarizeFailures(history, since);
        if (failed.Count == 0)
        {
            // Windows peut n'avoir de traces que depuis peu : la date dite est celle du début réel de ce qui a été lu.
            var oldest = history.Min(e => e.DateUtc);
            var start = oldest > since ? oldest : since;
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Info,
                Current = T("aucun échec noté par Windows depuis le {0}", ShortDate(DateOnly.FromDateTime(start.ToLocalTime()))),
                Explanation = explanation,
            };
        }

        var parts = new List<string>();
        AddFailures(parts, failed, UpdateFamily.Windows, T("Windows et autres produits Microsoft"));
        AddFailures(parts, failed, UpdateFamily.Driver, T("Pilotes"));
        AddFailures(parts, failed, UpdateFamily.Store, T("Applications du Store"));

        var unresolved = failed.Where(f => f.SucceededAfterUtc is null).ToList();
        var windowsUnresolved = unresolved
            .Where(f => f.Family == UpdateFamily.Windows && !Maus.Core.Follow.CrashTimelineReader.IsDefenderDefinitions(f.Title))
            .ToList();
        var stillWanted = windowsUnresolved
            .Where(f => pending is null ? !UpdateParsers.OtherWindowsSuccessAfter(history, f) : UpdateParsers.IsStillPending(f, pending))
            .ToList();
        var maybeReplaced = windowsUnresolved.Except(stillWanted).Any(f => UpdateParsers.OtherWindowsSuccessAfter(history, f));
        var looping = stillWanted.Any(f => f.Failures >= LoopFailureCount);
        var current = string.Join(" | ", parts);
        if (history.Count >= MaxHistoryEntries)
        {
            current += " " + T("(sur les {0} dernières lignes de l'historique)", MaxHistoryEntries);
        }

        if (maybeReplaced)
        {
            current += " | " + T("D'autres mises à jour de Windows se sont installées depuis : celle-ci a peut-être été remplacée par une plus récente, MAUS ne peut pas le savoir.");
        }

        string? advice = null;
        if (stillWanted.Count > 0)
        {
            advice = T("Pour en chercher la cause : utilitaire de résolution des problèmes de Microsoft, dans Paramètres > Système > Résolution des problèmes > "
                + "Autres utilitaires de résolution des problèmes > Windows Update. Pour le passage à une nouvelle version de Windows, SetupDiag, l'outil de Microsoft, lit les journaux "
                + "de l'installation (à lancer en administrateur) : https://learn.microsoft.com/windows/deployment/upgrade/setupdiag");
        }
        else if (unresolved.Count > 0 && unresolved.All(f => f.Family != UpdateFamily.Windows))
        {
            advice = T("Ces échecs touchent des pilotes ou des applications du Store, pas Windows lui-même : ils ne méritent votre attention que si l'appareil ou l'application concernée "
                + "fonctionne mal. Pour en chercher la cause : utilitaire de résolution des problèmes de Microsoft, dans Paramètres > Système > Résolution des problèmes > "
                + "Autres utilitaires de résolution des problèmes.");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = looping ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Info,
            Severity = looping ? Severity.Low : Severity.Info,
            Current = current,
            Expected = looping ? T("moins de {0} échecs d'une même mise à jour de Windows sans réussite ensuite (seuil choisi par MAUS)", LoopFailureCount) : null,
            Explanation = explanation,
            Advice = advice,
            SettingsPage = stillWanted.Count > 0 ? WindowsUpdatePage : null,
        };
    }

    private static void AddFailures(List<string> parts, List<FailedUpdate> failed, UpdateFamily family, string label)
    {
        var inFamily = failed.Where(f => f.Family == family).ToList();
        if (inFamily.Count == 0)
        {
            return;
        }

        var listed = string.Join(" ; ", inFamily.Take(MaxListedTitles).Select(DescribeFailure));
        parts.Add(inFamily.Count > MaxListedTitles
            ? T("{0} : {1} (et {2} de plus)", label, listed, (inFamily.Count - MaxListedTitles).ToString(CultureInfo.InvariantCulture))
            : T("{0} : {1}", label, listed));
    }

    private static string DescribeFailure(FailedUpdate failure)
    {
        var outcome = failure.SucceededAfterUtc is { } succeeded
            ? T("réussie ensuite le {0}", ShortDate(DateOnly.FromDateTime(succeeded.ToLocalTime())))
            : T("pas de réussite notée depuis");
        var count = Plural(failure.Failures, T("échec"), T("échecs"));
        var lastDate = ShortDate(DateOnly.FromDateTime(failure.LastFailureUtc.ToLocalTime()));
        if (failure.LastCode == 0)
        {
            return T("{0} : {1}, dernier le {2}, sans code noté, {3}", failure.Title, count, lastDate, outcome);
        }

        return T("{0} : {1}, dernier le {2}, code {3}, {4}", failure.Title, count, lastDate, UpdateParsers.FormatCode(failure.LastCode), outcome);
    }

    /// <summary>
    /// Retenue de Microsoft (« safeguard hold », https://learn.microsoft.com/windows/deployment/update/safeguard-holds), simple information :
    /// il suffit d'attendre. MAUS ne propose jamais de la contourner, et il écrit ce que Windows note, car Microsoft prévient qu'une retenue peut être périmée sur un PC.
    /// </summary>
    private static Finding DetectSafeguardHold(IRegistryReader registry)
    {
        const string id = "M03.safeguard-hold";
        var title = T("Retenue d'une nouvelle version de Windows par Microsoft");
        return Finding.Guard(id, title, UpdateCategory, () =>
        {
            var indicators = registry.GetSubKeyNames(RegistryHive.LocalMachine, HoldIndicatorsKey)
                .Select(name =>
                {
                    var key = $@"{HoldIndicatorsKey}\{name}";
                    return new SafeguardIndicators(
                        name,
                        registry.GetString(RegistryHive.LocalMachine, key, "GStatus"),
                        UpdateParsers.RegistryTexts(registry.GetValue(RegistryHive.LocalMachine, key, "GatedBlockId")),
                        UpdateParsers.RegistryTexts(registry.GetValue(RegistryHive.LocalMachine, key, "GatedBlockReason")));
                })
                .ToList();
            var hold = UpdateParsers.ReadHold(registry.GetString(RegistryHive.LocalMachine, HoldStatusKey, "GStatus"), indicators);
            var explanation = T("Quand un problème connu pourrait toucher ce PC, Microsoft peut retenir la proposition d'une nouvelle version de Windows, le temps de le corriger. Ce n'est pas une panne.");
            if (hold.Held != true)
            {
                return new Finding
                {
                    Id = id,
                    Title = title,
                    Category = UpdateCategory,
                    Status = FindingStatus.Info,
                    Current = hold.Held == false ? T("Windows n'indique aucune retenue") : T("Windows n'en dit rien sur ce PC"),
                    Explanation = explanation,
                };
            }

            var current = hold.TargetVersion is null ? T("Windows note une retenue") : T("Windows note une retenue pour la version {0}", hold.TargetVersion);
            if (hold.Ids.Count > 0)
            {
                current += T(", numéro {0}", string.Join(", ", hold.Ids));
            }

            if (hold.Reasons.Count > 0)
            {
                current += T(", motif indiqué par Windows : {0}", string.Join(", ", hold.Reasons));
            }

            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Info,
                Current = current,
                Explanation = explanation,
                Advice = T("Il suffit d'attendre : Windows Update proposera cette version une fois la retenue levée, et Microsoft recommande de ne pas l'installer à la main d'ici là. "
                    + "Microsoft prévient aussi qu'une retenue peut rester notée à tort si Windows n'arrive plus à rafraîchir cette information : "
                    + "le numéro se cherche dans les problèmes connus de la version concernée, https://learn.microsoft.com/windows/release-health/"),
                SettingsPage = WindowsUpdatePage,
            };
        });
    }

    private static Finding DetectLastInstall(ICimReader cim, DateOnly today)
    {
        const string id = "M03.last-install";
        var title = T("Dernier correctif Windows installé");
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(QfeQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            return Finding.Unknown(id, title, T("Liste des correctifs installés illisible par WMI."), UpdateCategory);
        }

        var dated = rows
            .Select(row => (HotFix: row.GetString("HotFixID"), Date: UpdateParsers.ParseQfeDate(row.GetString("InstalledOn"))))
            .Where(entry => entry.Date is not null)
            .ToList();

        // MaxBy lève une exception sur une liste vide de tuples : le cas « aucune date » est traité avant.
        if (dated.Count == 0)
        {
            return Finding.Unknown(id, title, T("Windows n'indique aucune date d'installation de correctif."), UpdateCategory);
        }

        var latest = dated.MaxBy(entry => entry.Date);
        var date = latest.Date!.Value;

        var age = Math.Max(0, today.DayNumber - date.DayNumber);
        var stale = age > MaxInstallAgeDays;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = stale ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = T("{0} installé le {1} ({2})", latest.HotFix ?? T("correctif"), ShortDate(date), Ago(age)),
            Expected = T("moins de {0} jours", MaxInstallAgeDays),
            Explanation = T("Microsoft publie un correctif cumulatif de sécurité le deuxième mardi de chaque mois. Si aucun n'a été "
                + "installé depuis plus de {0} jours, Windows Update est en pause, bloqué ou en panne.", MaxInstallAgeDays),
            Advice = stale
                ? T("Lancez une recherche dans Paramètres > Windows Update. Si l'installation échoue, "
                    + "le constat « Mises à jour qui ont échoué » cite la mise à jour et le code noté par Windows.")
                : null,
            SettingsPage = stale ? WindowsUpdatePage : null,
        };
    }

    private static Finding DetectLastSearch(AutomaticUpdatesResults? results, DateTimeOffset now)
    {
        const string id = "M03.last-search";
        var title = T("Dernière recherche automatique de mises à jour");
        if (results?.LastSearchSuccess is not { } last)
        {
            return Finding.Unknown(id, title, T("Windows n'indique pas la date de sa dernière recherche réussie de mises à jour."), UpdateCategory);
        }

        var lastUtc = DateTime.SpecifyKind(last, DateTimeKind.Utc);
        var age = Math.Max(0, (int)Math.Floor((now.UtcDateTime - lastUtc).TotalDays));
        var stale = age > MaxSearchAgeDays;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = stale ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = T("le {0} ({1})", ShortDate(DateOnly.FromDateTime(lastUtc.ToLocalTime())), Ago(age)),
            Expected = T("moins de {0} jours", MaxSearchAgeDays),
            Explanation = T("Windows cherche seul de nouvelles mises à jour environ une fois par jour. Une dernière recherche réussie "
                + "vieille de plus de deux semaines signale un Windows Update bloqué."),
            Advice = stale
                ? T("Ouvrez Paramètres > Windows Update et cliquez sur « Rechercher des mises à jour ». En cas d'échec répété, "
                    + "la page d'aide de Windows Update de Microsoft donne la marche à suivre.")
                : null,
            SettingsPage = stale ? WindowsUpdatePage : null,
        };
    }

    private static Finding DetectDefenderSignatures(ICimReader cim)
    {
        const string id = "M03.defender-signatures";
        var title = T("Définitions antivirus de Microsoft Defender");
        var thirdParty = ReadThirdPartyAntivirus(cim);
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(DefenderQuery, CimScopes.Defender);
        }
        catch (MausAccessDeniedException)
        {
            return thirdParty is not null ? DefenderNotActive(id, title, thirdParty, null) : Finding.AdminRequired(id, title, DefenderCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            rows = [];
        }

        var row = rows.Count > 0 ? rows[0] : null;
        if (row is null)
        {
            return thirdParty is not null
                ? DefenderNotActive(id, title, thirdParty, null)
                : Finding.Unknown(id, title, T("Microsoft Defender ne répond pas (désinstallé, ou remplacé par un autre antivirus)."), DefenderCategory);
        }

        var mode = row.GetString("AMRunningMode");
        if (thirdParty is not null || row.GetBool("AntivirusEnabled") == false
            || (!string.IsNullOrEmpty(mode) && !mode.Equals("Normal", StringComparison.OrdinalIgnoreCase)))
        {
            return DefenderNotActive(id, title, thirdParty, mode);
        }

        if (row.GetInt64("AntivirusSignatureAge") is not { } age)
        {
            return Finding.Unknown(id, title, T("Defender n'indique pas l'âge de ses définitions."), DefenderCategory);
        }

        var stale = age > MaxSignatureAgeDays;
        var current = age == 0 ? T("à jour (moins d'un jour)") : T("âge : {0}", Plural((int)Math.Min(age, int.MaxValue), T("jour"), T("jours")));
        if (row.GetString("AntivirusSignatureVersion") is { Length: > 0 } version)
        {
            current += $", version {version}";
        }

        if (row.GetDateTime("AntivirusSignatureLastUpdated") is { } updated)
        {
            current += T(" (du {0})", ShortDate(DateOnly.FromDateTime(updated)));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DefenderCategory,
            Status = stale ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = current,
            Expected = T("{0} jours au plus", MaxSignatureAgeDays),
            Explanation = T("Defender reçoit de nouvelles définitions de menaces plusieurs fois par jour. "
                + "Des définitions anciennes laissent passer les logiciels malveillants récents."),
            Advice = stale
                ? T("Ouvrez Sécurité Windows > Protection contre les virus et menaces > Mises à jour de la protection > Rechercher des mises à jour.")
                : null,
            SettingsPage = stale ? WindowsSecurityPage : null,
            Fixable = stale,
        };
    }

    /// <summary>Premier antivirus tiers actif déclaré au Centre de sécurité, ou <c>null</c>.</summary>
    private static string? ReadThirdPartyAntivirus(ICimReader cim)
    {
        try
        {
            return cim.Query(AntivirusQuery, CimScopes.SecurityCenter2)
                .Where(row => row.GetInt64("productState") is { } state && UpdateParsers.IsAntivirusEnabled(state))
                .Select(row => row.GetString("displayName"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !UpdateParsers.IsMicrosoftDefender(name));
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException or ManagementException or COMException)
        {
            // Centre de sécurité illisible : on s'en tient à l'état déclaré par Defender.
            return null;
        }
    }

    private static Finding DefenderNotActive(string id, string title, string? thirdParty, string? mode) => new()
    {
        Id = id,
        Title = title,
        Category = DefenderCategory,
        Status = FindingStatus.Info,
        Current = thirdParty is not null
            ? T("antivirus actif : {0}", thirdParty)
            : T("Defender n'est pas l'antivirus actif{0}", (string.IsNullOrEmpty(mode) ? string.Empty : $" (mode : {mode})")),
        Explanation = T("Les définitions de Defender ne sont vérifiées que lorsqu'il est l'antivirus actif. "
            + "Un autre antivirus gère ses propres mises à jour."),
    };

    private static Finding DetectLatestUpdatesToggle(IRegistryReader registry)
    {
        const string id = "M03.latest-updates-toggle";
        var title = T("Option « Obtenir les dernières mises à jour dès qu'elles sont disponibles »");
        int? optedIn;
        int? policy;
        try
        {
            optedIn = registry.GetDword(RegistryHive.LocalMachine, UxSettingsKey, "IsContinuousInnovationOptedIn");
            policy = registry.GetDword(RegistryHive.LocalMachine, PolicyKey, "AllowOptionalContent");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        var current = optedIn == 1 ? T("activée") : T("désactivée");
        if (policy is { } value)
        {
            current += T(" (stratégie AllowOptionalContent = {0})", value.ToString(CultureInfo.InvariantCulture));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = T("Cette option installe plus tôt les nouveautés et les aperçus, sans changer le rythme des correctifs "
                + "de sécurité, avec davantage de redémarrages. MAUS la lit sans la modifier."),
            Advice = T("Réglable dans Paramètres > Windows Update (ms-settings:windowsupdate)."),
            SettingsPage = WindowsUpdatePage,
        };
    }

    private static Finding DetectUpdateSource(IRegistryReader registry)
    {
        const string id = "M03.update-source";
        var title = T("Source des mises à jour");
        string? server;
        try
        {
            server = registry.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "UseWUServer") == 1
                ? registry.GetString(RegistryHive.LocalMachine, PolicyKey, "WUServer")
                : null;
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = string.IsNullOrWhiteSpace(server) ? T("Windows Update (serveurs Microsoft)") : T("serveur de l'organisation (WSUS) : {0}", server),
            Explanation = T("Les mises à jour viennent des serveurs de Microsoft, ou d'un serveur interne (WSUS) sur les PC d'entreprise. "
                + "Dans ce cas, l'organisation choisit les mises à jour publiées."),
        };
    }

    private static string TitleList(List<PendingUpdate> updates)
    {
        var titles = string.Join(" ; ", updates.Take(MaxListedTitles).Select(u => u.Title));
        return updates.Count > MaxListedTitles
            ? T("{0} (et {1} de plus)", titles, (updates.Count - MaxListedTitles).ToString(CultureInfo.InvariantCulture))
            : titles;
    }

    private static string Ago(int days) => days switch
    {
        0 => T("aujourd'hui"),
        1 => T("hier"),
        _ => T("il y a {0} jours", days.ToString(CultureInfo.InvariantCulture)),
    };

    private static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count > 1 ? plural : singular)}";

    /// <summary>Date courte dans la langue de MAUS (13/10/2026 en français, 10/13/2026 en anglais), jamais un format figé.</summary>
    private static string ShortDate(DateOnly date) => date.ToString("d", Culture);

    /// <summary>Mises à jour trouvées, ou raison de l'échec de la recherche.</summary>
    private sealed record SearchOutcome(IReadOnlyList<PendingUpdate>? Updates, string? Error);
}
