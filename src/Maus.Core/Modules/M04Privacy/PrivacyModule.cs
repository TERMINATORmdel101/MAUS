using Maus.Core.Fixes;
using Maus.Core.Rules;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>
/// Module 4 — Confidentialité et télémétrie. Ramène la collecte de Microsoft au plancher de chaque édition
/// (« Requises » sur Famille et Pro, 0 sur Entreprise et Éducation) et signale publicités, suggestions et fonctions d'IA.
/// Ce sont des choix, pas des dangers : aucun constat ne dépasse « optimisation possible ».
/// </summary>
public sealed class PrivacyModule : IFixableModule
{
    private static readonly Lazy<IReadOnlyList<RegistryRule>> Rules = new(() => EmbeddedCatalog.LoadRegistryRules("m04-privacy-rules.json"));

    public string Id => "M04";

    public string Title => "Confidentialité et télémétrie";

    public int Order => 40;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var registry = context.Registry;
        var windows = context.Windows;

        // Sur Famille, les clés Policies ne sont pas garanties : seul l'état effectif est pris en compte.
        var policiesHonored = !windows.IsHomeEdition;

        var findings = new List<Finding>
        {
            PrivacyRegistryChecks.DiagnosticData(registry, windows),
            PrivacySystemChecks.DiagTrack(context.Cim),
            PrivacyRegistryChecks.DiagnosticLogs(registry, windows),
        };

        cancellationToken.ThrowIfCancellationRequested();
        findings.Add(PrivacySystemChecks.CeipTasksState(context.Cim));
        findings.Add(PrivacyRegistryChecks.FeedbackFrequency(registry, policiesHonored));
        findings.Add(PrivacyRegistryChecks.TailoredExperiences(registry, policiesHonored));
        findings.Add(PrivacyRegistryChecks.AdvertisingId(registry, policiesHonored));
        findings.Add(PrivacyRegistryChecks.SettingsSuggestions(registry));

        // Réglages simples, valables sur toutes les éditions : catalogue JSON.
        findings.AddRange(RegistryRuleEvaluator.EvaluateAll(Rules.Value, registry)
            .Select(finding => finding.Status == FindingStatus.Improvable ? finding with { Fixable = true } : finding));

        findings.Add(PrivacyRegistryChecks.TipsAndSilentApps(registry));
        findings.Add(PrivacyRegistryChecks.StartRecommendations(registry, windows));
        findings.Add(PrivacyRegistryChecks.LockScreenTips(registry));
        findings.Add(PrivacyRegistryChecks.WebSearch(registry, windows));
        findings.Add(PrivacyRegistryChecks.SearchHighlights(registry, policiesHonored));
        findings.Add(PrivacyRegistryChecks.ActivityHistory(registry, windows));

        cancellationToken.ThrowIfCancellationRequested();
        findings.Add(PrivacySystemChecks.Copilot(context.Packages));
        findings.Add(PrivacySystemChecks.Recall(context.Cim, registry));

        cancellationToken.ThrowIfCancellationRequested();
        findings.Add(PrivacySystemChecks.DeliveryOptimization(context.Cim, registry));
        findings.Add(PrivacyRegistryChecks.ErrorReporting(registry));

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    public IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings) =>
        [.. PrivacyPlanner.Plan(Id, context, findings), .. RegistryRulePlanner.Plan(Id, Rules.Value, findings)];
}
