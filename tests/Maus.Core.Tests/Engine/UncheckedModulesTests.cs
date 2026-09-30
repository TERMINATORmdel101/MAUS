using Maus.Core.Engine;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Engine;

/// <summary>
/// Un module en erreur ou en délai dépassé n'a rien lu : il ne doit jamais passer pour « tout va bien » (donnée illisible =
/// gris, jamais un faux vert), ni dans le score, ni dans les familles, ni dans l'historique, ni dans l'audit de la semaine.
/// </summary>
public class UncheckedModulesTests
{
    private sealed class StubModule(string id, string title, Func<CancellationToken, Task<IReadOnlyList<Finding>>> detect, TimeSpan? timeout = null) : IAuditModule
    {
        public string Id => id;

        public string Title => title;

        public int Order => int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture) * 10;

        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(5);

        public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken) => detect(cancellationToken);
    }

    private static Finding Found(string id, FindingStatus status, Severity severity = Severity.Info) =>
        new() { Id = id, Title = "Titre " + id, Status = status, Severity = severity, Explanation = "e" };

    private static Task<IReadOnlyList<Finding>> Returns(params Finding[] findings) => Task.FromResult<IReadOnlyList<Finding>>(findings);

    /// <summary>
    /// Audit réel du moteur : M03 (famille U) dépasse son délai, M13 (famille S) lève une exception, M06 (famille M) a un
    /// point à surveiller, M01 (famille A) est conforme.
    /// </summary>
    private static Task<IReadOnlyList<ModuleResult>> AuditWithFailuresAsync() => new AuditEngine(
    [
        new StubModule("M01", "Audit des modifications risquées", _ => Returns(Found("M01.ok", FindingStatus.Ok))),
        new StubModule("M03", "Mises à jour Windows", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return [Found("M03.pending", FindingStatus.Problem, Severity.Critical)];
        }, TimeSpan.FromMilliseconds(200)),
        new StubModule("M06", "Interface et effets visuels", _ => Returns(Found("M06.warn", FindingStatus.Warning, Severity.Medium))),
        new StubModule("M13", "Sécurité du noyau", _ => throw new InvalidOperationException("boum")),
    ]).RunAsync(TestContext.Create());

    [Fact]
    public async Task Failed_modules_make_the_score_partial_without_adding_or_removing_points()
    {
        var results = await AuditWithFailuresAsync();

        var breakdown = HealthScore.Explain(results);

        // Seul le point à surveiller lu (M06, gravité moyenne) coûte : 100 × e^-0,04 = 96. Rien n'est inventé pour M03 et M13.
        Assert.Equal(4, breakdown.Points);
        Assert.Equal(96, breakdown.Score);
        Assert.True(breakdown.IsPartial);
        Assert.Equal(["M03", "M13"], breakdown.Unchecked.Select(u => u.ModuleId).Order(StringComparer.Ordinal));

        var note = HealthScore.PartialNote(breakdown);
        Assert.NotNull(note);
        Assert.Contains("Score partiel : 2 modules n'ont pas pu être vérifiés", note, StringComparison.Ordinal);
        Assert.Contains("Mises à jour Windows", note, StringComparison.Ordinal);
        Assert.Contains("Sécurité du noyau", note, StringComparison.Ordinal);

        // « Pourquoi ce score ? » : chaque module avec sa raison, et le score partiel n'est pas gardé.
        var detail = HealthScore.PartialDetail(breakdown);
        Assert.NotNull(detail);
        Assert.Contains("• Mises à jour Windows : Délai dépassé", detail, StringComparison.Ordinal);
        Assert.Contains("• Sécurité du noyau : Erreur inattendue : boum", detail, StringComparison.Ordinal);
        Assert.Contains("n'est pas gardé dans l'historique", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_audit_is_not_partial()
    {
        var results = await new AuditEngine(
        [
            new StubModule("M06", "Interface et effets visuels", _ => Returns(Found("M06.warn", FindingStatus.Warning, Severity.Medium))),
        ]).RunAsync(TestContext.Create());

        var breakdown = HealthScore.Explain(results);

        Assert.False(breakdown.IsPartial);
        Assert.Empty(breakdown.Unchecked);
        Assert.Null(HealthScore.PartialNote(breakdown));
        Assert.Null(HealthScore.PartialDetail(breakdown));
        Assert.NotNull(ScoreTrends.HealthEntry(results, DateTimeOffset.Now));
    }

    [Fact]
    public async Task A_family_with_a_failed_module_is_never_all_clear()
    {
        var families = HealthScore.Summaries(await AuditWithFailuresAsync());

        // U (M03, délai dépassé) et S (M13, erreur) : aucun constat, mais pas « tout est en ordre ».
        var updates = families.Single(f => f.Letter == "U");
        Assert.Equal(1, updates.Unchecked);
        Assert.Equal(0, updates.Problems);
        Assert.False(updates.AllClear);
        Assert.Equal(1, families.Single(f => f.Letter == "S").Unchecked);
        Assert.False(families.Single(f => f.Letter == "S").AllClear);

        // A (M01 conforme) : vraiment en ordre ; M (M06) : un point à surveiller, aucun module en échec.
        Assert.True(families.Single(f => f.Letter == "A").AllClear);
        Assert.Equal(0, families.Single(f => f.Letter == "M").Unchecked);
        Assert.False(families.Single(f => f.Letter == "M").AllClear);
    }

    [Fact]
    public async Task A_partial_score_is_not_kept_in_the_history()
    {
        var results = await AuditWithFailuresAsync();

        // Sans M03 ni M13, le score monterait : l'enregistrer ferait croire à une amélioration sur la courbe.
        Assert.Null(ScoreTrends.HealthEntry(results, DateTimeOffset.Now));
    }

    [Fact]
    public async Task Weekly_audit_names_the_failed_modules_but_does_not_wake_the_user_for_them_alone()
    {
        var results = await AuditWithFailuresAsync();

        // Aucun problème rouge lu : pas de notification (un module non vérifié ne dit rien de l'état du PC).
        Assert.Empty(ScheduledAudit.WorthNotifying(results));

        // Avec un problème rouge, la notification cite aussi les modules non vérifiés.
        var problems = new[] { Found("M02.disk", FindingStatus.Problem, Severity.High) };
        var text = ScheduledAudit.NotificationText(problems, results);
        Assert.Contains("Titre M02.disk", text, StringComparison.Ordinal);
        Assert.Contains("2 modules n'ont pas pu être vérifiés : ", text, StringComparison.Ordinal);
        Assert.Contains("Mises à jour Windows", text, StringComparison.Ordinal);
        Assert.Contains("Sécurité du noyau", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pas pu être vérifié", ScheduledAudit.NotificationText(problems, []), StringComparison.Ordinal);
    }
}
