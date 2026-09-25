using Maus.Core.Fixes;
using static Maus.Core.Localization.Texts;

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
                Title = T(fix.Title ?? rule.Advice ?? rule.Title),
                Description = T(rule.Explanation),
                Category = Optional(rule.Category),
                Gain = Optional(fix.Gain),
                Risk = Optional(fix.Risk),
                Warning = Optional(fix.Warning),
                Effect = fix.Effect,
                Advanced = fix.Advanced,
                Recommended = fix.Recommended && !fix.Advanced,
                Writes = writes,
            });
        }

        return changes;
    }
}
