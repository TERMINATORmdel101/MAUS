using Maus.Core.Platform;

namespace Maus.Core.Rules;

public static class RegistryRuleEvaluator
{
    public static IReadOnlyList<Finding> EvaluateAll(IEnumerable<RegistryRule> rules, IRegistryReader registry) =>
        rules.Select(rule => Evaluate(rule, registry)).ToList();

    public static Finding Evaluate(RegistryRule rule, IRegistryReader registry)
    {
        string? current;
        try
        {
            current = RegistryReaderExtensions.Normalize(registry.GetValue(rule.ParsedHive, rule.Path, rule.Name));
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(rule.Id, rule.Title, rule.Category);
        }

        var compliant = IsCompliant(rule, current);
        return new Finding
        {
            Id = rule.Id,
            Title = rule.Title,
            Category = rule.Category,
            Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(rule.Severity),
            Severity = rule.Severity,
            Current = current ?? "absente",
            Expected = rule.ExpectedLabel ?? DescribeExpectation(rule),
            Explanation = rule.Explanation,
            Advice = compliant ? null : rule.Advice,
            Fixable = !compliant && rule.Fix is not null,
        };
    }

    public static bool IsCompliant(RegistryRule rule, string? current) => rule.Expect switch
    {
        RuleExpectation.Absent => current is null,
        RuleExpectation.EqualTo => current is null ? rule.AbsentIsOk : Same(current, rule.Value),
        RuleExpectation.NotEqualTo => current is null || !Same(current, rule.Value),
        RuleExpectation.OneOf => current is null ? rule.AbsentIsOk : rule.Values.Any(v => Same(current, v)),
        _ => false,
    };

    private static bool Same(string current, string? expected) =>
        string.Equals(current, expected?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string DescribeExpectation(RegistryRule rule) => rule.Expect switch
    {
        RuleExpectation.Absent => "absente",
        RuleExpectation.EqualTo => rule.AbsentIsOk ? $"{rule.Value} ou absente" : rule.Value ?? "?",
        RuleExpectation.NotEqualTo => $"différente de {rule.Value}",
        RuleExpectation.OneOf => string.Join(" ou ", rule.Values),
        _ => "?",
    };
}
