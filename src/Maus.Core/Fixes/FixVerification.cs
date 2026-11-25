using static Maus.Core.Localization.Texts;

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
    /// <summary>
    /// Le nouvel audit a échoué : il n'y a rien à comparer. Chaque correction appliquée reste « non vérifiée » ; la comparer
    /// aux résultats d'avant les corrections la ferait passer à tort pour « sans effet », et le journal le garderait.
    /// </summary>
    public static IReadOnlyList<VerifiedOutcome> AuditFailed(ApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Changes.Select(outcome => outcome.Status == ChangeStatus.Applied
                ? new VerifiedOutcome(outcome, EffectCheck.NotChecked, T("Appliqué ; l'audit de vérification a échoué, l'effet n'a pas pu être relu."))
                : new VerifiedOutcome(outcome, EffectCheck.NotChecked, outcome.Message))
            .ToList();
    }

    public static IReadOnlyList<VerifiedOutcome> CompareWithAudit(
        IReadOnlyList<PlannedChange> changes,
        ApplyResult result,
        IReadOnlyList<ModuleResult> after,
        Platform.WindowsInfo windows)
    {
        ArgumentNullException.ThrowIfNull(result);

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
                return new VerifiedOutcome(outcome, EffectCheck.NotChecked, T("Appliqué ; l'effet n'a pas pu être relu par le nouvel audit."));
            }

            var policy = change.Writes.Any(w => (w.Key.Path ?? string.Empty).Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase));

            // Sur Famille, une stratégie posée devient une simple information (son effet n'est pas garanti, l'état réel n'est pas lu) :
            // ce n'est pas une confirmation.
            if (finding.Status == FindingStatus.Info && windows.IsHomeEdition && policy)
            {
                return new VerifiedOutcome(outcome, EffectCheck.NotChecked, T("Écrit ; sur Windows Famille, l'effet de cette stratégie n'est pas garanti et MAUS ne peut pas le relire. Vous pouvez l'annuler depuis l'Historique."));
            }

            if (finding.Status is FindingStatus.Ok or FindingStatus.Info)
            {
                return new VerifiedOutcome(outcome, EffectCheck.Confirmed, T("Appliqué et confirmé par le nouvel audit."));
            }

            if (change.Effect != ChangeEffect.Immediate)
            {
                return new VerifiedOutcome(outcome, EffectCheck.PendingRestart, T("Appliqué : {0}.", Reporting.Labels.Of(change.Effect)));
            }

            // Constat partagé par plusieurs corrections (une par programme détourné, par service, par tâche…) : il reste signalé
            // tant qu'un seul de ses éléments demeure, même si celui-ci a disparu. Rien ne permet d'y lire « sans effet ».
            if (change.FindingId is not null)
            {
                return new VerifiedOutcome(outcome, EffectCheck.NotChecked, T("Appliqué et relu. Ce constat regroupe plusieurs éléments et reste signalé : le nouvel audit ne dit pas si c'est pour celui-ci ou pour un autre encore présent."));
            }

            var message = windows.IsHomeEdition && policy
                ? T("Écrit, mais sans effet sur votre édition : Windows Famille ignore cette stratégie. Vous pouvez l'annuler depuis l'Historique.")
                : T("Écrit, mais Windows n'en tient pas compte pour l'instant (un autre réglage ou une application le remplace peut-être). Vous pouvez l'annuler depuis l'Historique.");
            return new VerifiedOutcome(outcome, EffectCheck.NoEffect, message);
        }).ToList();
    }
}
