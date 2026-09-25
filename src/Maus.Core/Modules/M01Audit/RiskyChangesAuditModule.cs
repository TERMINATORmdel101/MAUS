using System.Diagnostics.CodeAnalysis;
using System.Management;
using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Maus.Core.Rules;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>
/// Module 1 — Audit des modifications risquées. Compare le PC au catalogue condensé des contrôles
/// (Defender, défenses, Windows Update, réseau, composants, démarrage, persistance, système) et repère
/// les traces typiques des scripts de « debloat », des « optimiseurs » et des logiciels malveillants.
/// Lecture seule : aucune valeur n'est écrite, aucune commande qui modifie le système n'est lancée.
/// </summary>
public sealed partial class RiskyChangesAuditModule : IAuditModule
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private static readonly Lazy<IReadOnlyList<RegistryRule>> Rules = new(() => EmbeddedCatalog.LoadRegistryRules("m01-audit-rules.json"));

    private readonly ISignatureVerifier _signatures;
    private readonly IScheduledTaskReader _tasks;
    private readonly string _windowsDirectory;

    public RiskyChangesAuditModule()
        : this(new WinTrustSignatureVerifier(), new ComScheduledTaskReader(), Environment.GetFolderPath(Environment.SpecialFolder.Windows))
    {
    }

    internal RiskyChangesAuditModule(ISignatureVerifier signatures, IScheduledTaskReader tasks, string windowsDirectory)
    {
        _signatures = signatures;
        _tasks = tasks;
        _windowsDirectory = windowsDirectory.TrimEnd('\\');
    }

    public string Id => "M01";

    public string Title => T("Audit des modifications risquées");

    public int Order => 10;

    public TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var managed = context.Hardware.IsManaged;
        var findings = new List<Finding>();
        if (managed)
        {
            findings.Add(ManagedFinding());
        }

        // Sources partagées par plusieurs contrôles : lues une seule fois.
        var antivirus = ReadSecurityProducts(context.Cim, "AntiVirusProduct");
        var firewalls = ReadSecurityProducts(context.Cim, "FirewallProduct");
        var defender = CimQueryResult.Run(context.Cim, DefenderStatusQuery, CimScopes.Defender);
        var winHttp = await RunAsync(context, "netsh.exe", ["winhttp", "show", "proxy"], cancellationToken).ConfigureAwait(false);
        var bcd = context.IsElevated
            ? BcdEditParser.Parse(await RunAsync(context, "bcdedit.exe", ["/enum", "{current}"], cancellationToken).ConfigureAwait(false))
            : null;
        var winRe = context.IsElevated
            ? ReAgentInfoParser.ParseEnabled(await RunAsync(context, "reagentc.exe", ["/info"], cancellationToken).ConfigureAwait(false))
            : null;
        if (bcd is { Count: 0 })
        {
            bcd = null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Defender
        findings.Add(Guard(RealtimeCheck, () => DetectRealtimeProtection(context.Registry, defender, antivirus)));
        findings.Add(Catalog("M01.defender-policy", context.Registry));
        findings.Add(Guard(TamperCheck, () => DetectTamperProtection(defender, antivirus)));
        findings.Add(Guard(ExclusionsCheck, () => DetectExclusions(context)));

        // Défenses
        findings.Add(Catalog("M01.smartscreen", context.Registry));
        findings.Add(Catalog("M01.uac", context.Registry));
        findings.Add(Guard(UacPromptCheck, () => DetectUacPrompt(context.Registry)));
        findings.Add(Guard(FirewallCheck, () => DetectFirewall(context, firewalls)));

        // Windows Update
        findings.Add(Guard(UpdateServicesCheck, () => DetectUpdateServices(context.Registry)));
        findings.Add(Catalog("M01.wu-auto-update", context.Registry));
        findings.Add(Guard(WsusCheck, () => DetectWsus(context.Registry, managed)));
        findings.Add(Guard(PauseCheck, () => DetectUpdatePause(context)));
        findings.Add(Guard(TargetVersionCheck, () => DetectTargetVersion(context.Registry, managed)));
        findings.Add(Guard(UpdateTasksCheck, () => DetectUpdateTasks(context)));

        cancellationToken.ThrowIfCancellationRequested();

        // Réseau
        findings.Add(Guard(HostsCheck, () => DetectHosts(context)));
        findings.Add(Guard(ProxyCheck, () => DetectProxy(context.Registry, winHttp, managed)));

        // Composants
        findings.Add(Guard(CoreServicesCheck, () => DetectCoreServices(context.Registry, antivirus)));
        findings.Add(Guard(AppsCheck, () => DetectSystemApps(context)));
        findings.Add(Guard(WebViewCheck, () => DetectWebView(context.Registry)));
        findings.Add(Guard(PageFileCheck, () => DetectPageFile(context.Cim)));

        // Démarrage
        findings.Add(Guard(DepCheck, () => DetectDep(context.Cim, bcd)));
        findings.Add(Guard(DriverSigningCheck, () => DetectDriverSigning(context.Registry, bcd)));
        findings.Add(Guard(TimerCheck, () => DetectTimerTweaks(bcd, context.IsElevated)));
        findings.Add(Guard(SecureBootCheck, () => DetectSecureBoot(context.Registry)));

        cancellationToken.ThrowIfCancellationRequested();

        // Persistance
        findings.Add(Guard(IfeoCheck, () => DetectIfeoDebuggers(context.Registry)));
        findings.Add(Guard(WinlogonCheck, () => DetectWinlogon(context.Registry)));
        findings.Add(Guard(AppInitCheck, () => DetectAppInit(context.Registry)));
        findings.Add(Guard(StartupCheck, () => DetectUnsignedStartup(context)));

        // Système
        findings.Add(Guard(ModifiedImageCheck, () => DetectModifiedImage(context, winRe)));
        findings.Add(Guard(ActivationCheck, () => DetectActivation(context)));
        findings.Add(Guard(ResidualPoliciesCheck, () => DetectResidualPolicies(context.Registry, managed)));

        return findings;
    }

    /// <summary>Évalue une règle du catalogue JSON ; restaurer une stratégie consiste à supprimer sa valeur, d'où « corrigeable ».</summary>
    private static Finding Catalog(string id, IRegistryReader registry)
    {
        var finding = RegistryRuleEvaluator.Evaluate(Rules.Value.Single(r => r.Id == id), registry);
        return finding.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable
            ? finding with { Fixable = true }
            : finding;
    }

    /// <summary>Un contrôle qui échoue ne doit jamais faire échouer le module : droits manquants ou source absente donnent « indéterminé ».</summary>
    private static Finding Guard(Check check, Func<Finding> detect)
    {
        try
        {
            return detect();
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or UnauthorizedAccessException)
        {
            return check.AdminRequired();
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or ManagementException or COMException)
        {
            return check.Unknown(T("Information indisponible sur ce PC."));
        }
        catch (IOException ex)
        {
            return check.Unknown(T("Lecture impossible : {0}", ex.Message));
        }
    }

    private static async Task<string?> RunAsync(AuditContext context, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await context.Commands.RunAsync(executable, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
            return result is { TimedOut: false, ExitCode: 0 } ? result.StandardOutput : null;
        }
        catch (DataSourceUnavailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<SecurityProduct>? ReadSecurityProducts(ICimReader cim, string className)
    {
        var result = CimQueryResult.Run(cim, $"SELECT displayName, productState FROM {className}", CimScopes.SecurityCenter2);
        return result.Rows?
            .Select(row => new SecurityProduct(row.GetString("displayName") ?? T("Produit inconnu"), row.GetInt64("productState") ?? 0))
            .ToList();
    }

    private static string Join(IEnumerable<string> items, int max = 5)
    {
        var list = items.ToList();
        var shown = string.Join(" ; ", list.Take(max));
        return list.Count > max ? T("{0} ; et {1} autre(s)", shown, list.Count - max) : shown;
    }

    private static bool IsSet([NotNullWhen(true)] string? value) => !string.IsNullOrWhiteSpace(value);

    private static CimRow? FirstRow(IReadOnlyList<CimRow> rows) => rows.Count > 0 ? rows[0] : null;
}
