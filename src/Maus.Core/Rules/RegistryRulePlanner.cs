using Maus.Core.Fixes;

namespace Maus.Core.Rules;

/// <summary>Étape Plan des règles déclaratives : une correction par règle corrigeable en écart.</summary>
public static class RegistryRulePlanner
{
    public static IReadOnlyList<PlannedChange> Plan(string moduleId, IEnumerable<RegistryRule> rules, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();
        foreach (var rule in rules)
        {
            if (rule.Fix is not { } fix
                || !byId.TryGetValue(rule.Id, out var finding)
                || finding.Status is FindingStatus.Ok or FindingStatus.Info or FindingStatus.Unknown
                || fix.ToWrites(rule) is not { Count: > 0 } writes)
            {
                continue;
            }

            changes.Add(new PlannedChange
            {
                Id = rule.Id,
                ModuleId = moduleId,
                Title = fix.Title ?? rule.Advice ?? rule.Title,
                Description = rule.Explanation,
                Category = rule.Category,
                Gain = fix.Gain,
                Risk = fix.Risk,
                Warning = fix.Warning,
                Effect = fix.Effect,
                Advanced = fix.Advanced,
                Recommended = fix.Recommended && !fix.Advanced,
                Writes = writes,
            });
        }

        return changes;
    }
}
