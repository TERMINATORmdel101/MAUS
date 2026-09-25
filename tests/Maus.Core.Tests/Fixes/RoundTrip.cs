using Maus.Core.Fixes;

namespace Maus.Core.Tests.Fixes;

/// <summary>
/// Critère d'acceptation de la fiche technique : après Apply, les constats corrigés deviennent conformes ;
/// après Revert, un nouveau Detect ne montre aucun écart avec l'état initial.
/// </summary>
public static class RoundTrip
{
    public static async Task<(IReadOnlyList<PlannedChange> Plan, ApplyResult Applied)> AssertAsync(
        IFixableModule module, AuditContext audit, FakeRegistry registry, FakeSystemParameters? parameters = null)
    {
        var before = await module.DetectAsync(audit, CancellationToken.None);
        var plan = module.Plan(audit, before);
        Assert.NotEmpty(plan);
        Assert.All(plan, c => Assert.Equal(module.Id, c.ModuleId));
        Assert.Equal(plan.Count, plan.Select(c => c.Id).Distinct().Count());

        var engine = new FixEngine(TestFixContext.Create(audit, registry, parameters));
        var applied = engine.Apply(plan, new ApplyOptions());
        Assert.False(applied.Blocked, applied.BlockedReason);
        Assert.All(applied.Changes, c => Assert.True(c.Status == ChangeStatus.Applied, $"{c.ChangeId} : {c.Message}"));

        var after = await module.DetectAsync(audit, CancellationToken.None);
        foreach (var change in plan)
        {
            var finding = after.Single(f => f.Id == change.Id);
            Assert.True(finding.Status is FindingStatus.Ok or FindingStatus.Info, $"{change.Id} reste {finding.Status} après correction");
        }

        Assert.Empty(module.Plan(audit, after));

        var reverted = engine.Revert(applied.Session!.Id);
        Assert.True(reverted.Completed, string.Join(" ; ", reverted.Entries.Select(e => e.Message)));

        var restored = await module.DetectAsync(audit, CancellationToken.None);
        Assert.Equal(
            before.Select(f => (f.Id, f.Status, f.Current)),
            restored.Select(f => (f.Id, f.Status, f.Current)));
        return (plan, applied);
    }
}
