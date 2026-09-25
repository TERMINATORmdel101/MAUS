using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Modules.M03Updates;

/// <summary>
/// Module 3 — Mises à jour Windows (hors pilotes). En V0.1 (audit seul), le module lit la fin de maintenance de la version
/// installée, l'état des services et réglages de Windows Update, les mises à jour en attente (recherche WUA sans pilotes),
/// l'âge du dernier correctif et des définitions Defender. L'installation arrive en V0.2.
/// </summary>
public sealed class WindowsUpdateModule : IAuditModule
{
    /// <summary>
    /// Logiciels seulement (les pilotes relèvent du Module 9), obligatoires ou facultatifs en une seule recherche :
    /// <c>OR</c> n'est admis qu'au premier niveau du critère.
    /// </summary>
    internal const string SearchCriteria =
        "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0"
        + " or IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=1";

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

    /// <summary>Services dont Windows Update dépend ; « Start = 4 » signifie désactivé.</summary>
    internal static readonly (string Name, string Label)[] UpdateServices =
    [
        ("wuauserv", "Windows Update"),
        ("UsoSvc", "Orchestrateur de mises à jour"),
        ("BITS", "Transfert intelligent en arrière-plan (BITS)"),
        ("WaaSMedicSvc", "Windows Update Medic"),
    ];

    private const int ServiceDisabled = 4;
    private const int MaxListedTitles = 3;

    private const string VersionCategory = "Version de Windows";
    private const string UpdateCategory = "Windows Update";
    private const string DefenderCategory = "Defender";

    /// <summary>Fins de pause (ISO 8601) écrites par Paramètres > Windows Update ; la pause des mises à jour de fonctionnalité ne bloque pas les correctifs.</summary>
    private static readonly string[] PauseValueNames = ["PauseUpdatesExpiryTime", "PauseQualityUpdatesEndTime"];

    private static readonly TimeSpan DefaultSearchTimeout = TimeSpan.FromSeconds(150);
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
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

    public string Title => "Mises à jour Windows (hors pilotes)";

    public int Order => 30;

    /// <summary>La recherche Windows Update interroge le serveur de mises à jour et peut durer plusieurs minutes.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(180);

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(context.Now.LocalDateTime);
        var findings = new List<Finding>
        {
            DetectSupport(context.Windows, today),
            DetectServices(context.Registry),
            DetectAutomaticUpdates(context.Registry, context.Now, context.Hardware.IsManaged),
        };

        // Lu avant notre propre recherche, pour refléter l'activité de Windows et non celle de MAUS.
        var automaticResults = ReadAutomaticUpdatesResults();
        var search = await SearchAsync(cancellationToken).ConfigureAwait(false);

        findings.Add(DetectPendingUpdates(search));
        findings.Add(DetectLastInstall(context.Cim, today));
        findings.Add(DetectLastSearch(automaticResults, context.Now));
        findings.Add(DetectDefenderSignatures(context.Cim));
        findings.Add(DetectOptionalUpdates(search));
        findings.Add(DetectLatestUpdatesToggle(context.Registry));
        findings.Add(DetectUpdateSource(context.Registry));
        return findings;
    }

    private static Finding DetectSupport(WindowsInfo windows, DateOnly today)
    {
        const string id = "M03.windows-support";
        const string title = "Prise en charge de la version de Windows";
        const string explanation = "Chaque version de Windows 11 reçoit des correctifs de sécurité pendant une durée limitée : "
            + "24 mois en Famille et Pro, 36 mois en Entreprise et Éducation. Passé cette date, Windows Update ne corrige plus "
            + "les failles découvertes dans cette version.";
        var catalog = Catalog.Value;
        if (windows.Build <= 0)
        {
            return Finding.Unknown(id, title, "Numéro de version de Windows illisible dans le registre.", VersionCategory);
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
                Current = $"Windows 10 (build {windows.FullBuild}) : support terminé le {LongDate(catalog.Windows10EndOfSupport)}",
                Expected = "Windows 11 23H2 ou plus récent",
                Explanation = "MAUS vise Windows 11 uniquement : Windows 10 est hors périmètre. Sans correctifs de sécurité, "
                    + "le PC reste exposé aux failles découvertes depuis la fin du support.",
                Advice = "Passez à Windows 11 si le PC est compatible. Sinon, le programme de mises à jour de sécurité étendues (ESU) "
                    + "prolonge les correctifs pour une durée limitée.",
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
                    ? "Cette version est plus récente que le catalogue de MAUS (préversion Windows Insider ou nouvelle version) : sa date de fin de maintenance n'est pas connue."
                    : "Cette version ne figure pas dans le catalogue de MAUS (préversion Windows Insider ?) : sa date de fin de maintenance n'est pas connue.",
            };
        }

        var channel = WindowsLifecycleCatalog.Classify(windows.EditionId);
        var end = release.EndOfService(channel);
        var daysLeft = end.DayNumber - today.DayNumber;
        var severity = daysLeft < 0 ? Severity.High : daysLeft < WarningDaysBeforeEndOfService ? Severity.Medium : Severity.Info;
        var channelLabel = WindowsLifecycleCatalog.Describe(channel);
        var current = daysLeft < 0
            ? $"Windows 11 {release.Version} ({channelLabel}) : correctifs arrêtés depuis le {ShortDate(end)}"
            : $"Windows 11 {release.Version} ({channelLabel}) : correctifs jusqu'au {ShortDate(end)}, dans {Plural(daysLeft, "jour", "jours")}";

        string? advice = null;
        var fixable = false;
        if (severity != Severity.Info)
        {
            var status = daysLeft < 0
                ? $"Windows 11 {release.Version} ne reçoit plus de correctifs de sécurité depuis le {LongDate(end)}."
                : $"Windows 11 {release.Version} ne recevra plus de correctifs de sécurité après le {LongDate(end)}.";
            if (release.EnablementPackage is { } package && channel is ServicingChannel.HomePro or ServicingChannel.EnterpriseEducation)
            {
                advice = $"{status} Passez à {release.EnablementTarget} par le package d'activation {package} : un seul redémarrage, "
                    + "applications, fichiers et réglages conservés. Il apparaît dans Paramètres > Windows Update ; MAUS le proposera en V0.2.";
                if (release.EnablementMinimumUbr is { } minimum && windows.Ubr < minimum)
                {
                    advice += $" Installez d'abord les mises à jour cumulatives en attente (révision {release.Build}.{minimum} requise, "
                        + $"{windows.FullBuild} installée).";
                }

                fixable = true;
            }
            else if (catalog.Releases.Where(r => !r.NewDevicesOnly && r.Build > release.Build).MaxBy(r => r.Build) is { } target)
            {
                advice = $"{status} Installez la mise à jour de fonctionnalité vers Windows 11 {target.Version} depuis Paramètres > Windows Update, "
                    + "ou avec l'Assistant d'installation de Windows 11. Sauvegardez vos données avant.";
            }
            else
            {
                advice = $"{status} Installez la version suivante de Windows 11 dès qu'elle est proposée dans Paramètres > Windows Update.";
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
            Expected = $"version maintenue encore au moins {WarningDaysBeforeEndOfService} jours",
            Explanation = explanation,
            Advice = advice,
            Fixable = fixable,
        };
    }

    private static Finding DetectServices(IRegistryReader registry)
    {
        const string id = "M03.update-services";
        const string title = "Services de Windows Update";
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
            details.Add("désactivés : " + string.Join(", ", disabled));
        }

        if (missing.Count > 0)
        {
            details.Add("absents : " + string.Join(", ", missing));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = severity == Severity.Info ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(severity),
            Severity = severity == Severity.Info ? Severity.High : severity,
            Current = details.Count == 0 ? "aucun service désactivé" : string.Join(" ; ", details),
            Expected = "aucun service désactivé",
            Explanation = "Windows Update s'appuie sur plusieurs services (Windows Update, Orchestrateur, BITS, Medic). "
                + "Certains outils d'« optimisation » les désactivent : le PC ne reçoit alors plus aucun correctif de sécurité.",
            Advice = details.Count == 0
                ? null
                : "Si un outil tiers a désactivé ces services, annulez ce réglage dans cet outil. "
                    + "La remise en service avec le démarrage d'origine sera proposée en V0.2.",
            Fixable = disabled.Count > 0,
        };
    }

    private static Finding DetectAutomaticUpdates(IRegistryReader registry, DateTimeOffset now, bool isManaged)
    {
        const string id = "M03.automatic-updates";
        const string title = "Installation automatique des mises à jour";
        var reasons = new List<string>();
        var byPolicy = false;
        try
        {
            if (registry.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "NoAutoUpdate") == 1)
            {
                reasons.Add("désactivée par une stratégie (NoAutoUpdate)");
                byPolicy = true;
            }
            else if (registry.GetDword(RegistryHive.LocalMachine, AuPolicyKey, "AUOptions") == 2)
            {
                reasons.Add("simple notification, sans téléchargement (stratégie AUOptions = 2)");
                byPolicy = true;
            }

            if (registry.GetDword(RegistryHive.LocalMachine, PolicyKey, "DisableWindowsUpdateAccess") == 1)
            {
                reasons.Add("accès à Windows Update bloqué par une stratégie (DisableWindowsUpdateAccess)");
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
                reasons.Add($"en pause jusqu'au {ShortDate(DateOnly.FromDateTime(pausedUntil.LocalDateTime))}");
            }
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, UpdateCategory);
        }

        var advice = new List<string>();
        if (reasons.Any(r => r.StartsWith("en pause", StringComparison.Ordinal)))
        {
            advice.Add("Reprenez les mises à jour : Paramètres > Windows Update > Reprendre les mises à jour.");
        }

        if (byPolicy)
        {
            advice.Add(isManaged
                ? "Ce PC est géré par une organisation : ces stratégies relèvent de son service informatique."
                : "Une stratégie bloque l'installation automatique : si vous ne l'avez pas voulue, supprimez-la (gpedit.msc ou l'outil qui l'a posée).");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = reasons.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = reasons.Count == 0 ? "active" : string.Join(" ; ", reasons),
            Expected = "active, sans pause",
            Explanation = "Windows installe seul les correctifs de sécurité publiés chaque mois. Une pause ou une stratégie qui "
                + "bloque cette installation laisse le PC exposé aux failles connues.",
            Advice = advice.Count == 0 ? null : string.Join(' ', advice),
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

    private async Task<SearchOutcome> SearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updates = await _agent.SearchAsync(SearchCriteria, _searchTimeout, cancellationToken).ConfigureAwait(false);
            return new SearchOutcome(updates.DistinctBy(u => u.UpdateId, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (TimeoutException)
        {
            return new SearchOutcome(null, $"aucune réponse en {_searchTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s (connexion lente ou serveur surchargé)");
        }
        catch (COMException ex)
        {
            return new SearchOutcome(null, UpdateParsers.DescribeSearchError(ex.HResult));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or MausAccessDeniedException)
        {
            return new SearchOutcome(null, "accès refusé");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DataSourceUnavailableException)
        {
            return new SearchOutcome(null, ex.Message.TrimEnd('.'));
        }
    }

    private static Finding DetectPendingUpdates(SearchOutcome search)
    {
        const string id = "M03.pending-updates";
        const string title = "Mises à jour Windows en attente (hors pilotes)";
        if (search.Updates is null)
        {
            return Finding.Unknown(id, title, $"Recherche Windows Update impossible : {search.Error}.", UpdateCategory);
        }

        var security = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Security).ToList();
        var other = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Other).ToList();
        var definitions = search.Updates.Count(u => UpdateParsers.Classify(u) == PendingUpdateKind.Definitions);
        const string explanation = "Windows Update propose des mises à jour de Windows pas encore installées : correctifs de sécurité, "
            + ".NET, outil de suppression de logiciels malveillants, définitions Defender. Les pilotes sont exclus : ils relèvent du Module 9.";
        var definitionsNote = definitions == 0
            ? null
            : $"{Plural(definitions, "mise à jour", "mises à jour")} de définitions Defender en cours (installées automatiquement plusieurs fois par jour)";
        if (security.Count == 0 && other.Count == 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = definitionsNote is null ? "aucune" : $"aucune en dehors de : {definitionsNote}",
                Expected = "aucune",
                Explanation = explanation,
            };
        }

        var severity = security.Count > 0 ? Severity.Medium : Severity.Low;
        var counts = new List<string>();
        if (security.Count > 0)
        {
            counts.Add($"{security.Count.ToString(CultureInfo.InvariantCulture)} de sécurité");
        }

        if (other.Count > 0)
        {
            counts.Add(Plural(other.Count, "autre", "autres"));
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = $"{string.Join(" et ", counts)} : {TitleList([.. security, .. other])}"
                + (definitionsNote is null ? string.Empty : $" ; en plus, {definitionsNote}"),
            Expected = "aucune",
            Explanation = explanation,
            Advice = "Installez-les depuis Paramètres > Windows Update ; MAUS les installera en V0.2. Aucun pilote n'est installé ici. "
                + "Aucun gain de performance n'est attendu : ces mises à jour servent la sécurité et la stabilité.",
            Fixable = true,
        };
    }

    private static Finding DetectOptionalUpdates(SearchOutcome search)
    {
        const string id = "M03.optional-updates";
        const string title = "Mises à jour facultatives disponibles";
        if (search.Updates is null)
        {
            return Finding.Unknown(id, title, $"Recherche Windows Update impossible : {search.Error}.", UpdateCategory);
        }

        var optional = search.Updates.Where(u => UpdateParsers.Classify(u) == PendingUpdateKind.Optional).ToList();
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = optional.Count == 0 ? "aucune" : $"{Plural(optional.Count, "facultative", "facultatives")} : {TitleList(optional)}",
            Explanation = "Les mises à jour facultatives, comme l'aperçu non sécuritaire publié le quatrième mardi du mois, "
                + "corrigent des bugs plus tôt. Leur contenu arrive de toute façon dans le correctif du mois suivant.",
            Advice = optional.Count == 0
                ? null
                : "Les aperçus corrigent des bugs plus tôt mais peuvent en introduire. Installez-les seulement si un correctif précis vous concerne.",
            Fixable = optional.Count > 0,
        };
    }

    private static Finding DetectLastInstall(ICimReader cim, DateOnly today)
    {
        const string id = "M03.last-install";
        const string title = "Dernier correctif Windows installé";
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
            return Finding.Unknown(id, title, "Liste des correctifs installés illisible par WMI.", UpdateCategory);
        }

        var dated = rows
            .Select(row => (HotFix: row.GetString("HotFixID"), Date: UpdateParsers.ParseQfeDate(row.GetString("InstalledOn"))))
            .Where(entry => entry.Date is not null)
            .ToList();

        // MaxBy lève une exception sur une liste vide de tuples : le cas « aucune date » est traité avant.
        if (dated.Count == 0)
        {
            return Finding.Unknown(id, title, "Windows n'indique aucune date d'installation de correctif.", UpdateCategory);
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
            Current = $"{latest.HotFix ?? "correctif"} installé le {ShortDate(date)} ({Ago(age)})",
            Expected = $"moins de {MaxInstallAgeDays} jours",
            Explanation = "Microsoft publie un correctif cumulatif de sécurité le deuxième mardi de chaque mois. Si aucun n'a été "
                + $"installé depuis plus de {MaxInstallAgeDays} jours, Windows Update est en pause, bloqué ou en panne.",
            Advice = stale
                ? "Lancez une recherche dans Paramètres > Windows Update. Si l'installation échoue en boucle, "
                    + "la réparation des composants de Windows Update (Module 2) sera proposée en V0.2."
                : null,
        };
    }

    private static Finding DetectLastSearch(AutomaticUpdatesResults? results, DateTimeOffset now)
    {
        const string id = "M03.last-search";
        const string title = "Dernière recherche automatique de mises à jour";
        if (results?.LastSearchSuccess is not { } last)
        {
            return Finding.Unknown(id, title, "Windows n'indique pas la date de sa dernière recherche réussie de mises à jour.", UpdateCategory);
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
            Current = $"le {ShortDate(DateOnly.FromDateTime(lastUtc.ToLocalTime()))} ({Ago(age)})",
            Expected = $"moins de {MaxSearchAgeDays} jours",
            Explanation = "Windows cherche seul de nouvelles mises à jour environ une fois par jour. Une dernière recherche réussie "
                + "vieille de plus de deux semaines signale un Windows Update bloqué.",
            Advice = stale
                ? "Ouvrez Paramètres > Windows Update et cliquez sur « Rechercher des mises à jour ». En cas d'échec répété, "
                    + "la réparation de Windows Update (Module 2) sera proposée en V0.2."
                : null,
        };
    }

    private static Finding DetectDefenderSignatures(ICimReader cim)
    {
        const string id = "M03.defender-signatures";
        const string title = "Définitions antivirus de Microsoft Defender";
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
                : Finding.Unknown(id, title, "Microsoft Defender ne répond pas (désinstallé, ou remplacé par un autre antivirus).", DefenderCategory);
        }

        var mode = row.GetString("AMRunningMode");
        if (thirdParty is not null || row.GetBool("AntivirusEnabled") == false
            || (!string.IsNullOrEmpty(mode) && !mode.Equals("Normal", StringComparison.OrdinalIgnoreCase)))
        {
            return DefenderNotActive(id, title, thirdParty, mode);
        }

        if (row.GetInt64("AntivirusSignatureAge") is not { } age)
        {
            return Finding.Unknown(id, title, "Defender n'indique pas l'âge de ses définitions.", DefenderCategory);
        }

        var stale = age > MaxSignatureAgeDays;
        var current = age == 0 ? "à jour (moins d'un jour)" : $"âge : {Plural((int)Math.Min(age, int.MaxValue), "jour", "jours")}";
        if (row.GetString("AntivirusSignatureVersion") is { Length: > 0 } version)
        {
            current += $", version {version}";
        }

        if (row.GetDateTime("AntivirusSignatureLastUpdated") is { } updated)
        {
            current += $" (du {ShortDate(DateOnly.FromDateTime(updated))})";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = DefenderCategory,
            Status = stale ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = current,
            Expected = $"{MaxSignatureAgeDays} jours au plus",
            Explanation = "Defender reçoit de nouvelles définitions de menaces plusieurs fois par jour. "
                + "Des définitions anciennes laissent passer les logiciels malveillants récents.",
            Advice = stale
                ? "Ouvrez Sécurité Windows > Protection contre les virus et menaces > Mises à jour de la protection > Rechercher des mises à jour. "
                    + "MAUS lancera cette mise à jour en V0.2."
                : null,
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
            ? $"antivirus actif : {thirdParty}"
            : $"Defender n'est pas l'antivirus actif{(string.IsNullOrEmpty(mode) ? string.Empty : $" (mode : {mode})")}",
        Explanation = "Les définitions de Defender ne sont vérifiées que lorsqu'il est l'antivirus actif. "
            + "Un autre antivirus gère ses propres mises à jour.",
    };

    private static Finding DetectLatestUpdatesToggle(IRegistryReader registry)
    {
        const string id = "M03.latest-updates-toggle";
        const string title = "Option « Obtenir les dernières mises à jour dès qu'elles sont disponibles »";
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

        var current = optedIn == 1 ? "activée" : "désactivée";
        if (policy is { } value)
        {
            current += $" (stratégie AllowOptionalContent = {value.ToString(CultureInfo.InvariantCulture)})";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = "Cette option installe plus tôt les nouveautés et les aperçus, sans changer le rythme des correctifs "
                + "de sécurité, avec davantage de redémarrages. MAUS la lit sans la modifier.",
            Advice = "Réglable dans Paramètres > Windows Update (ms-settings:windowsupdate).",
        };
    }

    private static Finding DetectUpdateSource(IRegistryReader registry)
    {
        const string id = "M03.update-source";
        const string title = "Source des mises à jour";
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
            Current = string.IsNullOrWhiteSpace(server) ? "Windows Update (serveurs Microsoft)" : $"serveur de l'organisation (WSUS) : {server}",
            Explanation = "Les mises à jour viennent des serveurs de Microsoft, ou d'un serveur interne (WSUS) sur les PC d'entreprise. "
                + "Dans ce cas, l'organisation choisit les mises à jour publiées.",
        };
    }

    private static string TitleList(List<PendingUpdate> updates)
    {
        var titles = string.Join(" ; ", updates.Take(MaxListedTitles).Select(u => u.Title));
        return updates.Count > MaxListedTitles
            ? $"{titles} (et {(updates.Count - MaxListedTitles).ToString(CultureInfo.InvariantCulture)} de plus)"
            : titles;
    }

    private static string Ago(int days) => days switch
    {
        0 => "aujourd'hui",
        1 => "hier",
        _ => $"il y a {days.ToString(CultureInfo.InvariantCulture)} jours",
    };

    private static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count > 1 ? plural : singular)}";

    private static string ShortDate(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string LongDate(DateOnly date) => date.ToString("d MMMM yyyy", French);

    /// <summary>Mises à jour trouvées, ou raison de l'échec de la recherche.</summary>
    private sealed record SearchOutcome(IReadOnlyList<PendingUpdate>? Updates, string? Error);
}
