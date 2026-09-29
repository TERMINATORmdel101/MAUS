using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M05Power;

/// <summary>
/// Module 5 — Alimentation. Compare le mode de gestion actif et les modes d'alimentation (secteur, batterie)
/// au réglage proposé pour le profil du PC : fixe, fixe Ryzen X3D à deux CCD, fixe Intel hybride ou portable.
/// Vérifie aussi le démarrage rapide, la veille moderne et les utilitaires constructeur.
/// </summary>
public sealed class PowerModule : Fixes.IFixableModule
{
    internal const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    internal const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    internal const string SessionPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    internal const string PowerPolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";
    internal const string X3DServiceQuery = "SELECT Name, State FROM Win32_Service WHERE Name = 'amd3dvcacheSvc'";

    /// <summary>
    /// Système > Alimentation (« Power &amp; sleep », Microsoft Learn « Launch Windows Settings ») : là où se choisit le mode d'alimentation.
    /// Le mode de gestion et le démarrage rapide se règlent dans le Panneau de configuration, qui n'a pas d'adresse ms-settings.
    /// </summary>
    internal const string PowerSettingsPage = "ms-settings:powersleep";

    private static string PlanCategory => T("Mode de gestion");
    private static string ModeCategory => T("Mode d'alimentation");
    private static string StartupCategory => T("Démarrage et veille");
    private static string HardwareCategory => T("Matériel et utilitaires");

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EffectiveModeTimeout = TimeSpan.FromSeconds(3);

    private readonly IPowerPlatform _platform;

    public PowerModule()
        : this(new Win32PowerPlatform())
    {
    }

    internal PowerModule(IPowerPlatform platform)
    {
        _platform = platform;
    }

    /// <summary>Profil qui décide du réglage proposé.</summary>
    internal enum PowerProfile
    {
        Unknown,
        Desktop,
        DesktopX3D,
        DesktopHybrid,
        DesktopModernStandby,
        Laptop,
    }

    public string Id => "M05";

    public string Title => T("Alimentation");

    public int Order => 50;

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var registry = context.Registry;
        var listed = await ReadSchemeListAsync(context.Commands, cancellationToken).ConfigureAwait(false);
        var capabilities = CallPlatform(_platform.GetCapabilities);
        var efficiencyClasses = CallPlatform(() => _platform.GetEfficiencyClassCount());
        var isIntelHybrid = context.Hardware.Cpu.Vendor == HardwareVendor.Intel && efficiencyClasses > 1;
        var profile = DetermineProfile(context.Hardware, isIntelHybrid, capabilities, listed);
        var plan = ReadActivePlan(registry, listed);
        var overlays = ReadOverlays(registry);
        var effectiveMode = await ReadEffectiveModeAsync(cancellationToken).ConfigureAwait(false);

        var findings = new List<Finding> { EvaluatePlan(plan, profile, overlays, HasPlanPolicy(registry)) };
        if (plan?.Kind == SchemeKind.Balanced && profile is PowerProfile.Laptop or PowerProfile.DesktopX3D or PowerProfile.DesktopHybrid or PowerProfile.DesktopModernStandby)
        {
            findings.Add(EvaluateAcMode(overlays, profile, context.Preferences.LaptopPower));
            if (profile == PowerProfile.Laptop)
            {
                findings.Add(EvaluateDcMode(overlays, context.Preferences.LaptopPower));
            }
        }

        findings.Add(EvaluateFastStartup(registry, capabilities));
        if (context.Hardware.Cpu.IsAsymmetricDualCcdX3D)
        {
            findings.Add(EvaluateX3DOptimizer(context.Cim));
        }

        findings.Add(DescribeOemUtilities(OemPowerUtilities.Detect(context)));
        findings.Add(DescribeEffectiveMode(effectiveMode));
        findings.Add(DescribeModernStandby(capabilities));
        if (isIntelHybrid)
        {
            findings.Add(DescribeHybridCpu(context.Hardware.Cpu));
        }

        if (profile == PowerProfile.Laptop)
        {
            findings.Add(DescribeLaptopChoice(context.Preferences.LaptopPower));
        }

        findings.Add(DescribeProfile(context.Hardware, profile, capabilities));
        return findings;
    }

    /// <summary>
    /// Étape Plan : mode de gestion (API documentée <c>PowerSetActiveScheme</c>) et démarrage rapide.
    /// Les modes secteur et batterie passent par des fonctions non documentées : ils restent guidés vers Paramètres pour l'instant.
    /// Passer un fixe en « Haute performance » augmente la consommation au repos : proposé, jamais pré-coché.
    /// </summary>
    public IReadOnlyList<Fixes.PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var changes = new List<Fixes.PlannedChange>();

        if (byId.TryGetValue("M05.power-plan", out var plan) && plan.Fixable && plan.Status == FindingStatus.Improvable)
        {
            var capabilities = CallPlatform(_platform.GetCapabilities);
            var hybrid = context.Hardware.Cpu.Vendor == HardwareVendor.Intel && CallPlatform(() => _platform.GetEfficiencyClassCount()) > 1;
            var profile = DetermineProfile(context.Hardware, hybrid, capabilities, listed: null);
            var highPerformanceAvailable = TryRegistry(() => context.Registry.KeyExists(RegistryHive.LocalMachine, $@"{SchemesKey}\{PowerSchemes.HighPerformance:D}") ? "1" : null) is not null;
            var target = profile == PowerProfile.Desktop && (highPerformanceAvailable || capabilities?.AoAc != true)
                ? PowerSchemes.HighPerformance
                : PowerSchemes.Balanced;
            var toHighPerformance = target == PowerSchemes.HighPerformance;
            changes.Add(new Fixes.PlannedChange
            {
                Id = plan.Id,
                ModuleId = Id,
                Title = T("Passer au mode de gestion « {0} »", PowerSchemes.Label(toHighPerformance ? SchemeKind.HighPerformance : SchemeKind.Balanced)),
                Description = plan.Explanation,
                Category = PlanCategory,
                Gain = toHighPerformance
                    ? T("Le processeur reste plus souvent à haute fréquence. Le gain en jeu est souvent faible.")
                    : T("Les modes secteur et batterie redeviennent disponibles dans Paramètres > Système > Alimentation."),
                Risk = toHighPerformance ? T("La consommation au repos augmente.") : null,
                Recommended = !toHighPerformance,
                Writes = [new Fixes.SettingWrite(Fixes.SettingKey.ActivePowerScheme, Fixes.SettingValue.Text(target.ToString("D")))],
            });
        }

        if (byId.TryGetValue("M05.fast-startup", out var fastStartup) && fastStartup.Fixable && fastStartup.Status == FindingStatus.Improvable)
        {
            changes.Add(new Fixes.PlannedChange
            {
                Id = fastStartup.Id,
                ModuleId = Id,
                Title = T("Désactiver le démarrage rapide"),
                Description = T("Le noyau et les pilotes repartent de zéro à chaque allumage ; la veille prolongée n'est pas touchée."),
                Category = StartupCategory,
                Gain = T("Démarrages plus propres et mesure fiable du temps de démarrage (Module 12)."),
                Risk = T("L'allumage peut être un peu plus lent."),
                Writes = [new Fixes.SettingWrite(Fixes.SettingKey.Registry("HKLM", SessionPowerKey, "HiberbootEnabled"), Fixes.SettingValue.Dword(0))],
            });
        }

        return changes;
    }

    internal static PowerProfile DetermineProfile(HardwareProfile hardware, bool isIntelHybrid, PowerCapabilities? capabilities, IReadOnlyList<ListedScheme>? listed)
    {
        switch (hardware.FormFactor)
        {
            case FormFactor.Laptop:
                return PowerProfile.Laptop;
            case FormFactor.Unknown:
                return PowerProfile.Unknown;
        }

        if (hardware.Cpu.IsAsymmetricDualCcdX3D)
        {
            return PowerProfile.DesktopX3D;
        }

        if (isIntelHybrid)
        {
            return PowerProfile.DesktopHybrid;
        }

        // Veille moderne : « Haute performance » est souvent absent de la liste des modes.
        var highPerformanceListed = listed?.Any(s => s.Guid == PowerSchemes.HighPerformance || PowerSchemes.Classify(s.Guid, null, s.Name) is SchemeKind.HighPerformance or SchemeKind.UltimatePerformance);
        return capabilities?.AoAc == true && highPerformanceListed == false ? PowerProfile.DesktopModernStandby : PowerProfile.Desktop;
    }

    /// <summary>Mode de gestion actif : API, sinon « * » de <c>powercfg /list</c>, sinon registre.</summary>
    private ActivePlan? ReadActivePlan(IRegistryReader registry, IReadOnlyList<ListedScheme>? listed)
    {
        var guid = CallPlatform(_platform.GetActiveScheme)
            ?? listed?.FirstOrDefault(s => s.IsActive)?.Guid
            ?? (Guid.TryParse(TryRegistry(() => registry.GetString(RegistryHive.LocalMachine, SchemesKey, "ActivePowerScheme")), out var fromRegistry) ? (Guid?)fromRegistry : null);
        if (guid is null)
        {
            return null;
        }

        var listedName = listed?.FirstOrDefault(s => s.Guid == guid)?.Name;
        var friendlyName = TryRegistry(() => registry.GetString(RegistryHive.LocalMachine, $@"{SchemesKey}\{guid.Value:D}", "FriendlyName"));
        var kind = PowerSchemes.Classify(guid.Value, friendlyName, listedName);
        var name = string.IsNullOrWhiteSpace(listedName) ? (kind == SchemeKind.Other ? T("mode personnalisé ({0:D})", guid.Value) : PowerSchemes.Label(kind)) : listedName;
        return new ActivePlan(guid.Value, kind, name);
    }

    private static async Task<IReadOnlyList<ListedScheme>?> ReadSchemeListAsync(ICommandRunner commands, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commands.RunAsync("powercfg", ["/list"], CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0)
            {
                return null;
            }

            var schemes = PowerSchemes.ParseList(result.StandardOutput);
            return schemes.Count > 0 ? schemes : null;
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or MausAccessDeniedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Modes d'alimentation choisis dans Paramètres, stockés par Windows 11 sous forme de GUID texte ; absents, ils valent « Équilibré ».</summary>
    internal static OverlayState? ReadOverlays(IRegistryReader registry)
    {
        try
        {
            return new OverlayState(
                ParseOverlay(registry.GetString(RegistryHive.LocalMachine, SchemesKey, "ActiveOverlayAcPowerScheme")),
                ParseOverlay(registry.GetString(RegistryHive.LocalMachine, SchemesKey, "ActiveOverlayDcPowerScheme")));
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static OverlayValue ParseOverlay(string? raw) => raw switch
    {
        null => new OverlayValue(PowerSchemes.OverlayBalanced, IsDefault: true, IsValid: true),
        _ when Guid.TryParse(raw.Trim(), out var guid) => new OverlayValue(guid, IsDefault: false, IsValid: true),
        _ => new OverlayValue(Guid.Empty, IsDefault: false, IsValid: false),
    };

    private static bool HasPlanPolicy(IRegistryReader registry) =>
        TryRegistry(() => registry.GetString(RegistryHive.LocalMachine, PowerPolicyKey, "ActivePowerScheme")) is { Length: > 0 };

    private static Finding EvaluatePlan(ActivePlan? plan, PowerProfile profile, OverlayState? overlays, bool imposedByPolicy)
    {
        const string id = "M05.power-plan";
        var title = T("Mode de gestion de l'alimentation");
        if (plan is null)
        {
            return Finding.Unknown(id, title, T("Lecture du mode de gestion actif impossible."), PlanCategory);
        }

        var policyNote = imposedByPolicy
            ? T(" Ce mode est imposé par une stratégie (organisation ou script) : il se change par cette stratégie, pas dans les Options d'alimentation.")
            : string.Empty;
        var current = plan.Name;
        if (plan.Kind == SchemeKind.Balanced && profile is PowerProfile.Desktop && overlays?.Ac is { IsValid: true } ac)
        {
            current += $" (mode {PowerSchemes.OverlayLabel(ac.Guid)})";
        }

        if (profile == PowerProfile.Unknown)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = PlanCategory,
                Status = FindingStatus.Info,
                Current = current,
                Explanation = T("MAUS n'a pas pu déterminer si ce PC est fixe ou portable : aucune recommandation n'est faite sur le mode de gestion.") + policyNote,
            };
        }

        var (compliant, expected, explanation, advice) = profile switch
        {
            PowerProfile.DesktopX3D => (
                plan.Kind == SchemeKind.Balanced,
                "Utilisation normale",
                T("Nous gardons le mode Utilisation normale pour placer vos jeux sur les bons cœurs. Sur un Ryzen X3D à deux CCD, ce mode laisse le pilote AMD et la Game Bar mettre en sommeil les cœurs sans V-Cache pendant un jeu ; « Haute performance » empêche ce tri (voir aussi le Module 7)."),
                T("Revenir au mode « Utilisation normale » (Panneau de configuration > Options d'alimentation). Vous pouvez garder « Haute performance » si vous le préférez, en sachant que vos jeux risquent de tourner sur les cœurs sans V-Cache.")),
            PowerProfile.DesktopHybrid => (
                plan.Kind is SchemeKind.Balanced or SchemeKind.HighPerformance or SchemeKind.UltimatePerformance,
                T("Utilisation normale avec Meilleures performances, ou Haute performance"),
                T("Sur un processeur Intel hybride (cœurs P et E), « Utilisation normale » avec le mode « Meilleures performances » laisse Windows répartir les tâches entre les deux types de cœurs ; « Haute performance » reste un choix valable. Aucune consigne officielle d'Intel ne tranche."),
                T("Choisir « Utilisation normale » puis le mode « Meilleures performances » dans Paramètres > Système > Alimentation, ou « Haute performance » dans les Options d'alimentation.")),
            PowerProfile.DesktopModernStandby => (
                plan.Kind is SchemeKind.Balanced or SchemeKind.HighPerformance or SchemeKind.UltimatePerformance,
                T("Utilisation normale avec Meilleures performances"),
                T("Ce PC en veille moderne n'expose pas le mode « Haute performance » : « Utilisation normale » avec le mode « Meilleures performances » est le réglage le plus rapide disponible."),
                T("Choisir « Utilisation normale » dans les Options d'alimentation, puis le mode « Meilleures performances » dans Paramètres > Système > Alimentation.")),
            PowerProfile.Laptop => (
                plan.Kind == SchemeKind.Balanced,
                "Utilisation normale",
                T("Sur un portable, MAUS propose « Utilisation normale » avec le mode « Meilleures performances » sur secteur et « Équilibré » sur batterie : plus de chaleur et de bruit sur secteur, autonomie préservée sur batterie. Ces modes d'alimentation n'existent qu'avec « Utilisation normale » ou un mode qui en dérive."),
                T("Revenir au mode « Utilisation normale » (Panneau de configuration > Options d'alimentation), puis régler le mode d'alimentation dans Paramètres > Système > Alimentation. Le choix final vous revient.")),
            _ => (
                plan.Kind is SchemeKind.HighPerformance or SchemeKind.UltimatePerformance,
                T("Haute performance (ou une copie de Performances optimales)"),
                T("Sur un PC fixe, MAUS propose le mode « Haute performance » : le processeur reste plus souvent à haute fréquence. Le gain en jeu est souvent faible et la consommation au repos augmente."),
                T("Choisir « Haute performance » dans Panneau de configuration > Options d'alimentation (rubrique « Afficher les modes supplémentaires » si besoin).")),
        };

        if (plan.Kind == SchemeKind.UltimatePerformance)
        {
            explanation += T(" « Performances optimales » est une option experte : gain négligeable par rapport à « Haute performance » (à mesurer, voir Module 11) et consommation au repos plus élevée.");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = PlanCategory,
            Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = current,
            Expected = expected,
            Explanation = explanation + policyNote,
            Advice = compliant ? null : advice,
            Fixable = !compliant && !imposedByPolicy,
        };
    }

    private static Finding EvaluateAcMode(OverlayState? overlays, PowerProfile profile, LaptopPowerChoice choice)
    {
        const string id = "M05.power-mode-ac";
        var title = T("Mode d'alimentation sur secteur");
        if (overlays is null)
        {
            return Finding.AdminRequired(id, title, ModeCategory);
        }

        if (!overlays.Ac.IsValid)
        {
            return Finding.Unknown(id, title, T("Valeur du mode d'alimentation illisible."), ModeCategory);
        }

        var current = Describe(overlays.Ac);
        if (profile == PowerProfile.DesktopX3D)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = ModeCategory,
                Status = FindingStatus.Info,
                Current = current,
                Explanation = T("Sur un Ryzen X3D à deux CCD, l'intérêt de « Meilleures performances » plutôt qu'« Équilibré » reste à valider : MAUS n'en fait pas une recommandation."),
            };
        }

        if (profile == PowerProfile.Laptop && choice == LaptopPowerChoice.Battery)
        {
            var economical = overlays.Ac.Guid == PowerSchemes.OverlayBalanced || overlays.Ac.Guid == PowerSchemes.OverlayBestEfficiency;
            return new Finding
            {
                Id = id,
                Title = title,
                Category = ModeCategory,
                Status = economical ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
                Severity = Severity.Low,
                Current = current,
                Expected = T("Équilibré (votre choix : autonomie)"),
                Explanation = T("Vous avez choisi de privilégier l'autonomie et le silence : « Équilibré » sur secteur chauffe moins et fait moins de bruit."),
                Advice = economical ? null : T("Choisir « Équilibré » pour « Branché » dans Paramètres > Système > Alimentation (ms-settings:powersleep)."),
                SettingsPage = economical ? null : PowerSettingsPage,
            };
        }

        var compliant = overlays.Ac.Guid == PowerSchemes.OverlayBestPerformance;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = ModeCategory,
            Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = current,
            Expected = PowerSchemes.OverlayLabel(PowerSchemes.OverlayBestPerformance) + (profile == PowerProfile.Laptop && choice != LaptopPowerChoice.NotChosen ? T(" (votre choix)") : string.Empty),
            Explanation = T("Le mode d'alimentation (Paramètres > Système > Alimentation) ajuste « Utilisation normale » : « Meilleures performances » privilégie la réactivité et la fréquence du processeur quand le PC est branché."),
            Advice = compliant ? null : T("Choisir « Meilleures performances » pour « Branché » dans Paramètres > Système > Alimentation. Plus de chaleur et de bruit sur secteur."),
            SettingsPage = compliant ? null : PowerSettingsPage,
            Fixable = !compliant,
        };
    }

    private static Finding EvaluateDcMode(OverlayState? overlays, LaptopPowerChoice choice)
    {
        const string id = "M05.power-mode-dc";
        var title = T("Mode d'alimentation sur batterie");
        if (overlays is null)
        {
            return Finding.AdminRequired(id, title, ModeCategory);
        }

        if (!overlays.Dc.IsValid)
        {
            return Finding.Unknown(id, title, T("Valeur du mode d'alimentation illisible."), ModeCategory);
        }

        var dc = overlays.Dc.Guid;
        var (compliant, expected, explanation, advice) = choice switch
        {
            LaptopPowerChoice.PerformanceEverywhere => (
                dc == PowerSchemes.OverlayBestPerformance,
                T("Meilleures performances (votre choix : performance partout)"),
                T("Vous avez choisi la performance partout : le PC reste aussi rapide sur batterie, au prix d'une autonomie nettement réduite et de plus de chaleur."),
                T("Choisir « Meilleures performances » pour « Sur batterie » dans Paramètres > Système > Alimentation (ms-settings:powersleep).")),
            LaptopPowerChoice.Battery => (
                dc == PowerSchemes.OverlayBestEfficiency,
                T("Meilleure efficacité énergétique (votre choix : autonomie)"),
                T("Vous avez choisi l'autonomie : « Meilleure efficacité énergétique » allonge la durée sur batterie, le PC étant un peu moins réactif."),
                T("Choisir « Meilleure efficacité énergétique » pour « Sur batterie » dans Paramètres > Système > Alimentation (ms-settings:powersleep).")),
            _ => (
                dc == PowerSchemes.OverlayBalanced || dc == PowerSchemes.OverlayBestEfficiency,
                T("Équilibré (ou Meilleure efficacité énergétique)"),
                T("Sur batterie, « Équilibré » préserve l'autonomie sans trop brider le PC ; « Meilleure efficacité énergétique » est un choix d'économie encore plus poussé, proposé mais jamais imposé."),
                T("Choisir « Équilibré » pour « Sur batterie » dans Paramètres > Système > Alimentation : le mode actuel réduit l'autonomie et chauffe davantage.")),
        };

        return new Finding
        {
            Id = id,
            Title = title,
            Category = ModeCategory,
            Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = Describe(overlays.Dc),
            Expected = expected,
            Explanation = explanation,
            Advice = compliant ? null : advice,
            SettingsPage = compliant ? null : PowerSettingsPage,
        };
    }

    /// <summary>Décision du projet : sur un portable, l'utilisateur choisit au premier lancement ; la proposition par défaut est pré-sélectionnée.</summary>
    private static Finding DescribeLaptopChoice(LaptopPowerChoice choice) => new()
    {
        Id = "M05.laptop-choice",
        Title = T("Votre choix pour l'alimentation du portable"),
        Category = ModeCategory,
        Status = FindingStatus.Info,
        Current = choice switch
        {
            LaptopPowerChoice.Performance => T("performance sur secteur, Équilibré sur batterie"),
            LaptopPowerChoice.PerformanceEverywhere => T("performance partout"),
            LaptopPowerChoice.Battery => T("autonomie"),
            _ => T("pas encore choisi (proposition par défaut : performance sur secteur, Équilibré sur batterie)"),
        },
        Expected = T("au choix de l'utilisateur"),
        Explanation = T("Trois réglages sont possibles : performance sur secteur et Équilibré sur batterie (proposé) ; performance partout (autonomie réduite) ; " +
                      "autonomie (Équilibré sur secteur, Meilleure efficacité énergétique sur batterie)."),
        Advice = choice == LaptopPowerChoice.NotChosen
            ? T("Faites votre choix dans l'onglet Corrections de MAUS (ou maus --set alimentation=performance|partout|autonomie).")
            : T("Vous pouvez changer d'avis à tout moment dans l'onglet Corrections."),
    };

    /// <summary>Le démarrage rapide n'agit que si le fichier de veille prolongée existe.</summary>
    private static Finding EvaluateFastStartup(IRegistryReader registry, PowerCapabilities? capabilities)
    {
        const string id = "M05.fast-startup";
        var title = T("Démarrage rapide désactivé");
        int? hiberboot;
        int? hibernateEnabled;
        try
        {
            hiberboot = registry.GetDword(RegistryHive.LocalMachine, SessionPowerKey, "HiberbootEnabled");
            hibernateEnabled = registry.GetDword(RegistryHive.LocalMachine, PowerKey, "HibernateEnabled");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, StartupCategory);
        }

        var hibernationAvailable = capabilities?.HiberFilePresent ?? (hibernateEnabled is null ? (bool?)null : hibernateEnabled != 0);
        var enabled = hiberboot != 0;
        var effective = enabled && hibernationAvailable != false;
        var current = (enabled, hibernationAvailable) switch
        {
            (false, _) => T("désactivé"),
            (true, false) => T("activé, mais sans effet (veille prolongée désactivée)"),
            (true, true) => hiberboot is null ? T("activé (réglage Windows par défaut)") : T("activé"),
            _ => T("activé (veille prolongée non vérifiée)"),
        };

        return new Finding
        {
            Id = id,
            Title = title,
            Category = StartupCategory,
            Status = effective ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Ok,
            Severity = Severity.Low,
            Current = current,
            Expected = T("désactivé"),
            Explanation = T("Actif, le démarrage rapide recharge à chaque allumage le noyau et les pilotes figés dans le fichier hiberfil.sys : ils ne repartent pas de zéro, et la mesure du temps de démarrage est faussée. Il n'agit que si la veille prolongée est disponible."),
            Advice = effective ? T("Désactiver le démarrage rapide, sans toucher à la veille prolongée. Sa désactivation peut rendre l'allumage un peu plus lent.") : null,
            Fixable = effective,
        };
    }

    private static Finding EvaluateX3DOptimizer(ICimReader cim)
    {
        const string id = "M05.x3d-optimizer";
        const string title = "Service AMD 3D V-Cache Performance Optimizer actif";
        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(X3DServiceQuery);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, HardwareCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("Liste des services Windows illisible."), HardwareCategory);
        }

        var state = rows.Count > 0 ? rows[0].GetString("State") : null;
        var running = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
        return new Finding
        {
            Id = id,
            Title = title,
            Category = HardwareCategory,
            Status = running ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
            Severity = Severity.Low,
            Current = rows.Count == 0 ? T("absent") : running ? T("en cours d'exécution") : T("installé mais arrêté"),
            Expected = T("installé et en cours d'exécution"),
            Explanation = T("Sur un Ryzen X3D à deux CCD, ce service du pilote chipset AMD travaille avec la Game Bar et le mode « Utilisation normale » pour placer les jeux sur les cœurs dotés du V-Cache."),
            Advice = running ? null : T("Installer le dernier pilote chipset AMD depuis le site officiel d'AMD, puis redémarrer. Le service doit apparaître sous le nom « AMD 3D V-Cache Performance Optimizer Service »."),
        };
    }

    private static Finding DescribeOemUtilities(IReadOnlyList<string> utilities) => new()
    {
        Id = "M05.oem-utility",
        Title = T("Utilitaire constructeur de gestion d'énergie"),
        Category = HardwareCategory,
        Status = FindingStatus.Info,
        Current = utilities.Count > 0 ? string.Join(", ", utilities) : T("aucun détecté"),
        Explanation = T("Les utilitaires des constructeurs (Armoury Crate, Lenovo Vantage, Legion Space, MSI Center, Alienware Command Center, OMEN Gaming Hub) pilotent aussi la puissance du processeur et de la carte graphique, et souvent les ventilateurs. Ils peuvent changer le mode d'alimentation de leur côté."),
        Advice = utilities.Count > 0
            ? T("MAUS n'écrase pas leurs réglages : choisissez le profil « Performance » (ou équivalent) dans l'utilitaire.")
            : null,
    };

    private async Task<EffectivePowerMode?> ReadEffectiveModeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _platform.GetEffectivePowerModeAsync(EffectiveModeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static Finding DescribeEffectiveMode(EffectivePowerMode? mode)
    {
        const string id = "M05.effective-mode";
        var title = T("Mode effectif appliqué par Windows");
        return mode is null
            ? Finding.Unknown(id, title, T("Windows n'a pas communiqué le mode effectif."), ModeCategory)
            : new Finding
            {
                Id = id,
                Title = title,
                Category = ModeCategory,
                Status = FindingStatus.Info,
                Current = mode switch
                {
                    EffectivePowerMode.BatterySaver => T("Économiseur d'énergie"),
                    EffectivePowerMode.BetterBattery => T("Meilleure efficacité énergétique"),
                    EffectivePowerMode.Balanced => T("Équilibré"),
                    EffectivePowerMode.HighPerformance => T("Haute performance"),
                    EffectivePowerMode.MaxPerformance => T("Performances maximales"),
                    EffectivePowerMode.GameMode => T("Mode Jeu (un jeu est au premier plan)"),
                    _ => T("Réalité mixte"),
                },
                Explanation = T("C'est le mode réellement appliqué en ce moment, une fois combinés le mode de gestion, le mode d'alimentation, l'économiseur d'énergie et le Mode Jeu."),
            };
    }

    private static Finding DescribeModernStandby(PowerCapabilities? capabilities)
    {
        const string id = "M05.modern-standby";
        const string title = "Veille moderne (S0)";
        return capabilities is null
            ? Finding.Unknown(id, title, T("Lecture des capacités d'alimentation impossible."), StartupCategory)
            : new Finding
            {
                Id = id,
                Title = title,
                Category = StartupCategory,
                Status = FindingStatus.Info,
                Current = capabilities.AoAc ? T("prise en charge") : capabilities.SystemS3 ? T("non prise en charge (veille classique S3)") : T("non prise en charge"),
                Explanation = T("En veille moderne, le PC reste connecté pendant la veille, comme un téléphone. Ces PC n'exposent souvent que le mode « Utilisation normale » : les autres modes de gestion peuvent manquer."),
            };
    }

    private static Finding DescribeHybridCpu(CpuInfo cpu) => new()
    {
        Id = "M05.hybrid-cpu",
        Title = T("Processeur Intel hybride"),
        Category = HardwareCategory,
        Status = FindingStatus.Info,
        Current = T("{0} : cœurs performants (P) et économes (E)", cpu.Name),
        Explanation = T("Windows répartit les tâches entre cœurs P et E avec l'aide d'Intel Thread Director. Aucune consigne officielle d'Intel ne recommande un mode d'alimentation particulier : « Utilisation normale » avec « Meilleures performances » ou « Haute performance » conviennent tous deux."),
        Advice = T("Microcode du processeur : voir le Module 8."),
    };

    private static Finding DescribeProfile(HardwareProfile hardware, PowerProfile profile, PowerCapabilities? capabilities)
    {
        var current = profile switch
        {
            PowerProfile.Laptop => T("Portable"),
            PowerProfile.Unknown => T("Indéterminé"),
            PowerProfile.DesktopX3D => T("PC fixe, Ryzen X3D à deux CCD"),
            PowerProfile.DesktopHybrid => T("PC fixe, processeur Intel hybride"),
            PowerProfile.DesktopModernStandby => T("PC fixe en veille moderne"),
            _ => T("PC fixe"),
        };
        var explanation = T("MAUS adapte ses propositions d'alimentation au type de PC, reconnu par deux indices sur trois : type de châssis, type de système et présence d'une batterie.");
        var batteryIsUps = capabilities is { UpsPresent: true } or { BatteriesAreShortTerm: true };
        if (hardware.FormFactor == FormFactor.Desktop && (hardware.HasBattery || batteryIsUps))
        {
            explanation += T(" Une batterie est signalée sur ce PC fixe : il s'agit probablement d'un onduleur branché en USB.");
        }

        return new Finding
        {
            Id = "M05.profile",
            Title = T("Type de PC reconnu"),
            Category = HardwareCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = explanation,
            Advice = profile == PowerProfile.Laptop ? T("Au premier lancement, vous choisirez vos préférences : MAUS propose « Meilleures performances » sur secteur et « Équilibré » sur batterie.") : null,
        };
    }

    private static string Describe(OverlayValue overlay) =>
        overlay.IsDefault ? T("{0} (réglage par défaut)", PowerSchemes.OverlayLabel(overlay.Guid)) : PowerSchemes.OverlayLabel(overlay.Guid);

    /// <summary>Appel natif protégé : une DLL ou une fonction absente équivaut à une donnée indisponible.</summary>
    private static T? CallPlatform<T>(Func<T?> call)
        where T : struct
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static PowerCapabilities? CallPlatform(Func<PowerCapabilities?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? TryRegistry(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    internal sealed record ActivePlan(Guid Guid, SchemeKind Kind, string Name);

    internal sealed record OverlayValue(Guid Guid, bool IsDefault, bool IsValid);

    internal sealed record OverlayState(OverlayValue Ac, OverlayValue Dc);
}
