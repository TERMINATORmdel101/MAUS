using Maus.Core.Rules;
using Microsoft.Win32;

namespace Maus.Core.Tests.Rules;

public class RegistryRuleEvaluatorTests
{
    private static RegistryRule Rule(RuleExpectation expect, string? value = null, bool absentIsOk = false, Severity severity = Severity.High) => new()
    {
        Id = "T.rule",
        Title = "Règle de test",
        Hive = "HKLM",
        Path = @"SOFTWARE\Test",
        Name = "Value",
        Expect = expect,
        Value = value,
        AbsentIsOk = absentIsOk,
        Severity = severity,
        Explanation = "Explication",
        Advice = "Conseil",
    };

    [Fact]
    public void Absent_rule_is_ok_when_value_missing()
    {
        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.Absent), new FakeRegistry());

        Assert.Equal(FindingStatus.Ok, finding.Status);
        Assert.Equal("absente", finding.Current);
        Assert.Null(finding.Advice);
    }

    [Fact]
    public void Absent_rule_flags_present_policy_with_severity_mapping()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Test", "Value", 1);

        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.Absent), registry);

        Assert.Equal(FindingStatus.Problem, finding.Status);
        Assert.Equal("1", finding.Current);
        Assert.Equal("Conseil", finding.Advice);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void EqualTo_compares_dword_as_text(int stored, bool compliant)
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Test", "Value", stored);

        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.EqualTo, "1"), registry);

        Assert.Equal(compliant ? FindingStatus.Ok : FindingStatus.Problem, finding.Status);
    }

    [Theory]
    [InlineData(true, FindingStatus.Ok)]
    [InlineData(false, FindingStatus.Warning)]
    public void EqualTo_uses_AbsentIsOk_when_value_missing(bool absentIsOk, FindingStatus expected)
    {
        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.EqualTo, "1", absentIsOk, Severity.Medium), new FakeRegistry());

        Assert.Equal(expected, finding.Status);
    }

    [Fact]
    public void NotEqualTo_is_ok_when_absent_and_flags_forbidden_value()
    {
        var rule = Rule(RuleExpectation.NotEqualTo, "1", severity: Severity.Critical);

        Assert.Equal(FindingStatus.Ok, RegistryRuleEvaluator.Evaluate(rule, new FakeRegistry()).Status);
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Test", "Value", 1);
        Assert.Equal(FindingStatus.Problem, RegistryRuleEvaluator.Evaluate(rule, registry).Status);
    }

    [Fact]
    public void OneOf_accepts_any_listed_value_case_insensitively()
    {
        var rule = Rule(RuleExpectation.OneOf) with { Values = ["explorer.exe", "other.exe"] };
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Test", "Value", "Explorer.EXE");

        Assert.Equal(FindingStatus.Ok, RegistryRuleEvaluator.Evaluate(rule, registry).Status);
    }

    [Fact]
    public void Low_severity_deviation_is_an_optimisation_not_a_warning()
    {
        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.EqualTo, "0", severity: Severity.Low), new FakeRegistry());

        Assert.Equal(FindingStatus.Improvable, finding.Status);
    }

    [Fact]
    public void Access_denied_becomes_unknown_admin_required()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, @"SOFTWARE\Test");

        var finding = RegistryRuleEvaluator.Evaluate(Rule(RuleExpectation.Absent), registry);

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("administrateur", finding.Explanation);
    }

    [Fact]
    public void Unknown_hive_is_rejected()
    {
        var rule = Rule(RuleExpectation.Absent) with { Hive = "HKCR" };

        Assert.Throws<FormatException>(() => rule.ParsedHive);
    }
}
