using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Rules;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>
/// Étape Plan du Module 1 : retour aux valeurs par défaut de Windows, pour les contrôles qui passent par le registre.
/// Règle des stratégies : restaurer = supprimer la valeur, jamais écrire la valeur « activée ».
/// Les constats Critique sont pré-cochés, les autres restent au choix de l'utilisateur.
/// Defender (exclusions), pare-feu local, BCD, fichier hosts, proxy WinHTTP, tâches et fichier d'échange arrivent dans une étape suivante.
/// </summary>
public sealed partial class RiskyChangesAuditModule : IFixableModule
{
    private const string LocalPolicyFile = @"System32\GroupPolicy\Machine\Registry.pol";

    /// <summary>Type de démarrage d'origine des services surveillés (build 26200 observée ; UCPD n'est jamais touché).</summary>
    private static readonly Dictionary<string, int> DefaultServiceStart = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = 3,
        ["UsoSvc"] = 2,
        ["WaaSMedicSvc"] = 3,
        ["BITS"] = 3,
        ["CryptSvc"] = 2,
        ["TrustedInstaller"] = 3,
        ["Audiosrv"] = 2,
        ["WSearch"] = 2,
        ["WinDefend"] = 2,
        ["mpssvc"] = 2,
    };

    private static readonly string[] PauseValues =
    [
        "FlightSettingsMaxPauseDays", "PauseUpdatesExpiryTime", "PauseUpdatesStartTime",
        "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    ];

    public IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(findings);

        var policyFileNote = HasLocalPolicyFile(context)
            ? T("Une stratégie locale (Registry.pol) existe sur ce PC : si elle contient ce réglage, Windows peut le réécrire. MAUS le verra au prochain audit.")
            : null;
        var plan = new PlanBuilder(Id, findings, policyFileNote);
        var registry = context.Registry;

        // Règles du catalogue (stratégies Defender, SmartScreen, mises à jour automatiques, UAC).
        foreach (var change in RegistryRulePlanner.Plan(Id, Rules.Value, findings))
        {
            var finding = plan.Finding(change.Id);
            plan.Changes.Add(change with
            {
                Recommended = finding.Severity == Severity.Critical,
                Risk = change.Risk ?? (change.Writes.All(w => w.Value is null) ? policyFileNote : null),
            });
        }

        PlanProtections(plan, registry);
        PlanUpdates(plan);
        PlanServices(plan, registry);
        PlanWinlogon(plan, registry);
        PlanAppInit(plan, registry);
        PlanDebuggers(plan, registry);
        return plan.Changes;
    }

    /// <summary>Protection en temps réel, confirmation de l'UAC, pare-feu.</summary>
    private static void PlanProtections(PlanBuilder plan, IRegistryReader registry)
    {
        if (plan.Deviates("M01.defender-realtime", out var realtime) && Read(registry, RealtimePolicyKey, "DisableRealtimeMonitoring") is not null)
        {
            plan.Add(realtime, T("Supprimer la stratégie qui coupe la protection en temps réel"),
                T("Rend la main à Sécurité Windows. Si la protection reste coupée ensuite, réactivez-la dans Sécurité Windows > Protection contre les virus et menaces."),
                [Delete(RealtimePolicyKey, "DisableRealtimeMonitoring")],
                risk: T("La protection contre les falsifications peut refuser l'écriture : MAUS le signalera sans rien casser."));
        }

        if (plan.Deviates("M01.uac-prompt", out var uacPrompt))
        {
            plan.Add(uacPrompt, T("Remettre la demande de confirmation de l'UAC par défaut"),
                T("Curseur de l'UAC sur le niveau par défaut : confirmation sur le bureau sécurisé."),
                [Set(UacKey, "ConsentPromptBehaviorAdmin", 5), Set(UacKey, "PromptOnSecureDesktop", 1)]);
        }

        if (plan.Deviates("M01.firewall", out var firewall))
        {
            plan.Add(firewall, T("Supprimer les stratégies qui coupent le pare-feu"),
                T("Rend la main à Sécurité Windows. Si un profil reste coupé ensuite, réactivez-le dans Sécurité Windows > Pare-feu et protection du réseau."),
                FirewallPolicyProfiles.Select(p => p.Key).Distinct(StringComparer.Ordinal)
                    .Where(key => registry.GetDword(Microsoft.Win32.RegistryHive.LocalMachine, FirewallPolicyKey + key, "EnableFirewall") == 0)
                    .Select(key => Delete(FirewallPolicyKey + key, "EnableFirewall")),
                effect: ChangeEffect.Restart);
        }
    }

    /// <summary>Serveur de mises à jour imposé, version figée, pause.</summary>
    private static void PlanUpdates(PlanBuilder plan)
    {
        if (plan.Deviates("M01.wu-server", out var wsus))
        {
            plan.Add(wsus, T("Revenir aux serveurs de mises à jour de Microsoft"),
                T("Supprime le serveur de mises à jour imposé et les valeurs qui l'accompagnent."),
                [
                    Delete(WuPolicyKey, "WUServer"), Delete(WuPolicyKey, "WUStatusServer"),
                    Delete(WuPolicyKey, "DoNotConnectToWindowsUpdateInternetLocations"), Delete(WuAuPolicyKey, "UseWUServer"),
                ]);
        }

        if (plan.Deviates("M01.wu-target-version", out var target))
        {
            plan.Add(target, T("Ne plus figer la version de Windows"),
                T("Windows suit de nouveau les versions prises en charge."),
                [Delete(WuPolicyKey, "TargetReleaseVersion"), Delete(WuPolicyKey, "TargetReleaseVersionInfo"), Delete(WuPolicyKey, "ProductVersion")]);
        }

        if (plan.Deviates("M01.wu-pause", out var pause))
        {
            plan.Add(pause, T("Reprendre les mises à jour"),
                T("Supprime la pause en cours et la durée de pause allongée (valeurs de la clé WindowsUpdate\\UX\\Settings lues par Paramètres)."),
                PauseValues.Select(name => Delete(WuUxSettingsKey, name)));
        }
    }

    /// <summary>Services de mise à jour et services essentiels désactivés.</summary>
    private static void PlanServices(PlanBuilder plan, IRegistryReader registry)
    {
        foreach (var (check, names) in new[] { ("M01.wu-services", UpdateServices), ("M01.core-services", CoreServices) })
        {
            if (!plan.Deviates(check, out var services))
            {
                continue;
            }

            foreach (var name in names)
            {
                var state = ServiceStart.Read(registry, name);
                if (state.IsDisabled && DefaultServiceStart.TryGetValue(name, out var start))
                {
                    plan.Add(services, T("Réactiver le service {0} ({1})", name, ServiceStart.Label(start)),
                        T("Remet le type de démarrage d'origine du service {0}.", name),
                        [Set(@"SYSTEM\CurrentControlSet\Services\" + name, "Start", start)],
                        id: $"{check}.{name}",
                        effect: ChangeEffect.Restart,
                        risk: name is "WinDefend" or "mpssvc" or "WaaSMedicSvc"
                            ? T("Windows protège ce service : l'écriture peut être refusée même en administrateur. MAUS le signalera sans rien casser.")
                            : null);
                }
            }
        }
    }

    /// <summary>Shell et Userinit détournés à l'ouverture de session.</summary>
    private void PlanWinlogon(PlanBuilder plan, IRegistryReader registry)
    {
        if (plan.Deviates("M01.winlogon", out var winlogon))
        {
            var writes = new List<SettingWrite>();
            var shell = registry.GetString(Microsoft.Win32.RegistryHive.LocalMachine, WinlogonKey, "Shell");
            if (IsSet(shell) && !IsDefaultWinlogonEntry(shell, "explorer.exe", _windowsDirectory, _windowsDirectory))
            {
                writes.Add(new SettingWrite(SettingKey.Registry("HKLM", WinlogonKey, "Shell"), SettingValue.Text("explorer.exe")));
            }

            var userShell = registry.GetString(Microsoft.Win32.RegistryHive.CurrentUser, WinlogonKey, "Shell");
            if (IsSet(userShell) && !IsDefaultWinlogonEntry(userShell, "explorer.exe", _windowsDirectory, _windowsDirectory))
            {
                writes.Add(new SettingWrite(SettingKey.Registry("HKCU", WinlogonKey, "Shell"), null));
            }

            var userinit = registry.GetString(Microsoft.Win32.RegistryHive.LocalMachine, WinlogonKey, "Userinit");
            if (IsSet(userinit) && !IsDefaultWinlogonEntry(userinit, "userinit.exe", _windowsDirectory + @"\System32", _windowsDirectory))
            {
                writes.Add(new SettingWrite(SettingKey.Registry("HKLM", WinlogonKey, "Userinit"), SettingValue.Text(_windowsDirectory + @"\system32\userinit.exe,")));
            }

            plan.Add(winlogon, T("Remettre Shell et Userinit par défaut"),
                T("Seuls le Bureau (explorer.exe) et userinit.exe démarrent de nouveau à l'ouverture de session."),
                writes, effect: ChangeEffect.SignOut,
                warning: T("Lancez aussi une analyse antivirus complète : un programme ajouté à ces valeurs est souvent malveillant."));
        }
    }

    /// <summary>Bibliothèques injectées dans tous les programmes (AppInit_DLLs).</summary>
    private static void PlanAppInit(PlanBuilder plan, IRegistryReader registry)
    {
        if (plan.Deviates("M01.appinit", out var appInit))
        {
            var writes = new List<SettingWrite>();
            foreach (var key in AppInitKeys.Where(key => IsSet(registry.GetString(Microsoft.Win32.RegistryHive.LocalMachine, key, "AppInit_DLLs"))))
            {
                writes.Add(new SettingWrite(SettingKey.Registry("HKLM", key, "AppInit_DLLs"), SettingValue.Text(string.Empty)));
                writes.Add(Set(key, "LoadAppInit_DLLs", 0));
            }

            plan.Add(appInit, "Vider AppInit_DLLs", T("Plus aucune bibliothèque n'est injectée dans tous les programmes."), writes,
                effect: ChangeEffect.Restart,
                warning: T("Lancez aussi une analyse antivirus complète."));
        }
    }

    /// <summary>Programmes détournés par un « débogueur » (Image File Execution Options).</summary>
    private static void PlanDebuggers(PlanBuilder plan, IRegistryReader registry)
    {
        if (plan.Deviates("M01.ifeo-debugger", out var ifeo))
        {
            foreach (var root in IfeoKeys)
            {
                foreach (var program in SafeSubKeys(registry, root))
                {
                    var path = $@"{root}\{program}";
                    string? debugger;
                    try
                    {
                        debugger = registry.GetString(Microsoft.Win32.RegistryHive.LocalMachine, path, "Debugger");
                    }
                    catch (MausAccessDeniedException)
                    {
                        continue;
                    }

                    if (IsSet(debugger))
                    {
                        plan.Add(ifeo, T("Supprimer le détournement de {0}", program),
                            T("{0} est actuellement remplacé par : {1}.", program, debugger),
                            [Delete(path, "Debugger")],
                            id: $"M01.ifeo-debugger.{program.ToLowerInvariant()}",
                            warning: T("Vérifiez d'abord : certains outils légitimes (Process Explorer qui remplace le Gestionnaire des tâches) utilisent ce mécanisme."));
                        plan.Changes[^1] = plan.Changes[^1] with { Recommended = false };
                    }
                }
            }
        }
    }

    /// <summary>Constats du dernier audit et corrections déjà prévues, partagés par les étapes de <see cref="Plan"/>.</summary>
    private sealed class PlanBuilder(string moduleId, IReadOnlyList<Finding> findings, string? policyFileNote)
    {
        private readonly Dictionary<string, Finding> _byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);

        public List<PlannedChange> Changes { get; } = [];

        public Finding Finding(string id) => _byId[id];

        /// <summary>Le constat existe, s'écarte de la valeur attendue et peut être corrigé.</summary>
        public bool Deviates(string id, out Finding finding) =>
            _byId.TryGetValue(id, out finding!) && finding.Fixable && finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem;

        public void Add(Finding finding, string title, string description, IEnumerable<SettingWrite> writes, string? id = null, ChangeEffect effect = ChangeEffect.Immediate, string? risk = null, string? warning = null)
        {
            var list = writes.ToList();
            if (list.Count == 0)
            {
                return;
            }

            var isPolicy = list.Any(w => w.Value is null && (w.Key.Path ?? string.Empty).Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase));
            Changes.Add(new PlannedChange
            {
                Id = id ?? finding.Id,
                FindingId = id is null ? null : finding.Id,
                ModuleId = moduleId,
                Title = title,
                Description = description,
                Category = finding.Category,
                Gain = T("Retour au réglage d'origine de Windows : sécurité et mises à jour rétablies."),
                Risk = risk ?? (isPolicy ? policyFileNote : null),
                Warning = warning,
                Effect = effect,
                Recommended = finding.Severity == Severity.Critical,
                Writes = list,
            });
        }
    }

    private bool HasLocalPolicyFile(AuditContext context) => context.Files.FileExists(_windowsDirectory + @"\" + LocalPolicyFile);

    private static IReadOnlyList<string> SafeSubKeys(IRegistryReader registry, string path)
    {
        try
        {
            return registry.GetSubKeyNames(Microsoft.Win32.RegistryHive.LocalMachine, path);
        }
        catch (MausAccessDeniedException)
        {
            return [];
        }
    }

    private static object? Read(IRegistryReader registry, string path, string name)
    {
        try
        {
            return registry.GetValue(Microsoft.Win32.RegistryHive.LocalMachine, path, name);
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static SettingWrite Delete(string path, string name) => new(SettingKey.Registry("HKLM", path, name), null);

    private static SettingWrite Set(string path, string name, int value) => new(SettingKey.Registry("HKLM", path, name), SettingValue.Dword(value));
}
