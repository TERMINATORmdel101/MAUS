using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public class FixVerificationTests
{
    private static readonly WindowsInfo Home = new("Windows 11 Famille", "Core", "25H2", 26200, 1000);
    private static readonly WindowsInfo Pro = new("Windows 11 Pro", "Professional", "25H2", 26200, 1000);

    private static PlannedChange Change(string id, string path, ChangeEffect effect = ChangeEffect.Immediate) => new()
    {
        Id = id,
        ModuleId = "M04",
        Title = id,
        Description = "d",
        Effect = effect,
        Writes = [new SettingWrite(SettingKey.Registry("HKLM", path, "X"), SettingValue.Dword(0))],
    };

    private static Finding Found(string id, FindingStatus status) => new() { Id = id, Title = id, Status = status, Explanation = "e" };

    [Fact]
    public void Ignored_policy_on_home_is_reported_as_without_effect_on_this_edition()
    {
        PlannedChange[] changes =
        [
            Change("M04.policy", @"SOFTWARE\Policies\Microsoft\Windows\System"),
            Change("M04.ok", @"SOFTWARE\Microsoft\Other"),
            Change("M09.hags", @"SYSTEM\X", ChangeEffect.Restart),
        ];
        var applied = new ApplyResult(null, changes.Select(c => new ChangeOutcome(c.Id, c.Title, ChangeStatus.Applied, "ok", c.Effect)).ToList(), null);
        var after = new[]
        {
            new ModuleResult("M04", "t", [Found("M04.policy", FindingStatus.Improvable), Found("M04.ok", FindingStatus.Ok), Found("M09.hags", FindingStatus.Improvable)], TimeSpan.Zero),
        };

        var home = FixVerification.CompareWithAudit(changes, applied, after, Home);
        var pro = FixVerification.CompareWithAudit(changes, applied, after, Pro);

        Assert.Equal(EffectCheck.NoEffect, home[0].Check);
        Assert.Contains("Windows Famille", home[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Famille", pro[0].Message, StringComparison.Ordinal);
        Assert.Equal(EffectCheck.Confirmed, home[1].Check);
        Assert.Equal(EffectCheck.PendingRestart, home[2].Check);
    }

    [Fact]
    public void Failed_verification_audit_leaves_changes_not_checked_instead_of_without_effect()
    {
        var registry = new FakeRegistry();
        var audit = TestContext.Create(registry);
        var journal = new InMemoryJournalStore();
        var engine = new FixEngine(TestFixContext.Create(audit, registry, journal: journal));
        var change = Change("M04.a", @"SOFTWARE\Test\A");
        var applied = engine.Apply([change], new ApplyOptions { CreateRestorePoint = false });

        // Le piège : comparer aux résultats d'AVANT les corrections (audit de vérification en échec) donnerait « sans effet ».
        var stale = new[] { new ModuleResult("M04", "t", [Found("M04.a", FindingStatus.Improvable)], TimeSpan.Zero) };
        Assert.Equal(EffectCheck.NoEffect, FixVerification.CompareWithAudit([change], applied, stale, Pro).Single().Check);

        var verified = FixVerification.AuditFailed(applied);
        engine.RecordVerification(applied.Session!.Id, verified);

        var outcome = Assert.Single(verified);
        Assert.Equal(EffectCheck.NotChecked, outcome.Check);
        Assert.Contains("l'audit de vérification a échoué", outcome.Message, StringComparison.Ordinal);
        Assert.Null(journal.Load(applied.Session.Id)!.Entries.Single().EffectNote);
    }

    [Fact]
    public void Failed_verification_keeps_the_message_of_changes_that_were_not_applied()
    {
        var failed = new ChangeOutcome("M04.b", "b", ChangeStatus.Failed, "valeur d'origine remise", ChangeEffect.Immediate);

        var verified = FixVerification.AuditFailed(new ApplyResult(null, [failed], null));

        Assert.Equal(EffectCheck.NotChecked, verified.Single().Check);
        Assert.Equal("valeur d'origine remise", verified.Single().Message);
    }

    [Fact]
    public void Single_change_can_be_reverted_and_verification_is_journaled()
    {
        var registry = new FakeRegistry();
        var audit = TestContext.Create(registry);
        var journal = new InMemoryJournalStore();
        var engine = new FixEngine(TestFixContext.Create(audit, registry, journal: journal));
        var a = Change("M04.a", @"SOFTWARE\Test\A");
        var b = Change("M04.b", @"SOFTWARE\Test\B");
        var applied = engine.Apply([a, b], new ApplyOptions { CreateRestorePoint = false });

        engine.RecordVerification(applied.Session!.Id, [new VerifiedOutcome(applied.Changes[1], EffectCheck.NoEffect, "sans effet")]);
        var reverted = engine.Revert(applied.Session.Id, changeId: "M04.a");

        Assert.True(reverted.Completed);
        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, @"SOFTWARE\Test\A", "X"));
        Assert.Equal(0, registry.GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Test\B", "X"));
        var session = journal.Load(applied.Session.Id)!;
        Assert.True(session.CanRevert);
        Assert.Null(session.RevertedAt);
        Assert.Equal("sans effet", session.Entries.Single(e => e.ChangeId == "M04.b").EffectNote);
        Assert.NotNull(engine.Revert(applied.Session.Id, changeId: "M04.a").Error);
    }
}
