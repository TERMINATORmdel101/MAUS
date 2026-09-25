using Maus.Core.Engine;

namespace Maus.Core.Tests.Engine;

public class AuditEngineTests
{
    private sealed class StubModule(string id, int order, Func<CancellationToken, Task<IReadOnlyList<Finding>>> detect, TimeSpan? timeout = null) : IAuditModule
    {
        public string Id => id;

        public string Title => $"Module {id}";

        public int Order => order;

        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(5);

        public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken) => detect(cancellationToken);
    }

    private static Finding Ok(string id) => new() { Id = id, Title = id, Status = FindingStatus.Ok, Explanation = "ok" };

    [Fact]
    public async Task Results_are_ordered_by_module_order()
    {
        var engine = new AuditEngine(
        [
            new StubModule("M02", 20, _ => Task.FromResult<IReadOnlyList<Finding>>([Ok("b")])),
            new StubModule("M01", 10, _ => Task.FromResult<IReadOnlyList<Finding>>([Ok("a")])),
        ]);

        var results = await engine.RunAsync(TestContext.Create());

        Assert.Equal(["M01", "M02"], results.Select(r => r.ModuleId));
    }

    [Fact]
    public async Task A_crashing_module_does_not_stop_the_audit()
    {
        var engine = new AuditEngine(
        [
            new StubModule("M01", 10, _ => throw new InvalidOperationException("boum")),
            new StubModule("M02", 20, _ => Task.FromResult<IReadOnlyList<Finding>>([Ok("b")])),
        ]);

        var results = await engine.RunAsync(TestContext.Create());

        Assert.Contains("boum", results[0].Error);
        Assert.Equal(FindingStatus.Unknown, results[0].WorstStatus);
        Assert.Null(results[1].Error);
    }

    [Fact]
    public async Task A_slow_module_times_out()
    {
        var engine = new AuditEngine(
        [
            new StubModule("M01", 10, async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return [];
            }, TimeSpan.FromMilliseconds(200)),
        ]);

        var results = await engine.RunAsync(TestContext.Create());

        Assert.Contains("Délai dépassé", results[0].Error);
    }

    [Fact]
    public void Built_in_modules_have_unique_ids_and_ids_match_the_spec_numbering()
    {
        var modules = AuditEngine.CreateWithBuiltInModules().Modules;

        Assert.NotEmpty(modules);
        Assert.Equal(modules.Count, modules.Select(m => m.Id).Distinct().Count());
        Assert.All(modules, m => Assert.Matches(@"^M(0[1-9]|1[0-6])$", m.Id));
    }
}
