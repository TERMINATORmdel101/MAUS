namespace Maus.Core.Fixes;

/// <summary>Effet réel d'une correction, vu par un nouvel audit (l'écriture relue ne suffit pas : une stratégie peut être ignorée).</summary>
public enum EffectCheck
{
    /// <summary>Le constat n'a pas pu être relu (module en erreur, constat absent).</summary>
    NotChecked,

    /// <summary>Le nouvel audit confirme la correction.</summary>
    Confirmed,

    /// <summary>La correction attend un redémarrage, une déconnexion ou le redémarrage de l'Explorateur.</summary>
    PendingRestart,

    /// <summary>La valeur est écrite, mais Windows n'en tient pas compte (stratégie ignorée sur Famille, réglage remplacé…).</summary>
    NoEffect,
}

public sealed record VerifiedOutcome(ChangeOutcome Outcome, EffectCheck Check, string Message);

/// <summary>Étape Verify, second niveau : comparer chaque correction appliquée au constat du nouvel audit.</summary>
public static class FixVerification
{
    public static IReadOnlyList<VerifiedOutcome> CompareWithAudit(
        IReadOnlyList<PlannedChange> changes,
        ApplyResult result,
        IReadOnlyList<ModuleResult> after,
        Platform.WindowsInfo windows)
    {
        var findings = after.Where(r => r.Error is null).SelectMany(r => r.Findings).GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byId = changes.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return result.Changes.Select(outcome =>
        {
            if (outcome.Status != ChangeStatus.Applied || !byId.TryGetValue(outcome.ChangeId, out var change))
            {
                return new VerifiedOutcome(outcome, EffectCheck.NotChecked, outcome.Message);
            }

            if (!findings.TryGetValue(change.FindingId ?? change.Id, out var finding) || finding.Status == FindingStatus.Unknown)
            {
                return new VerifiedOutcome(outcome, EffectCheck.NotChecked, "Appliqué ; l'effet n'a pas pu être relu par le nouvel audit.");
            }

            if (finding.Status is FindingStatus.Ok or FindingStatus.Info)
            {
                return new VerifiedOutcome(outcome, EffectCheck.Confirmed, "Appliqué et confirmé par le nouvel audit.");
            }

            if (change.Effect != ChangeEffect.Immediate)
            {
                return new VerifiedOutcome(outcome, EffectCheck.PendingRestart, $"Appliqué : {Reporting.Labels.Of(change.Effect)}.");
            }

            var policy = change.Writes.Any(w => (w.Key.Path ?? string.Empty).Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase));
            var message = windows.IsHomeEdition && policy
                ? "Écrit, mais sans effet sur votre édition : Windows Famille ignore cette stratégie. Vous pouvez l'annuler depuis l'Historique."
                : "Écrit, mais Windows n'en tient pas compte pour l'instant (un autre réglage ou une application le remplace peut-être). Vous pouvez l'annuler depuis l'Historique.";
            return new VerifiedOutcome(outcome, EffectCheck.NoEffect, message);
        }).ToList();
    }
}
