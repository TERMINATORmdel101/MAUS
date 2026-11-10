using System.Reflection;
using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Modules.M20Software;
using Maus.Core.Preferences;
using Maus.Core.Tests.Fixes;

namespace Maus.Core.Tests.Engine;

/// <summary>
/// winget est un outil propre au compte de l'utilisateur : le moteur ne le lance jamais (0.8.6). Quand le moteur fait l'audit, la
/// liste des logiciels à mettre à jour est lue par la fenêtre, sans droits, et prend la place du constat du moteur.
/// </summary>
public sealed class EngineSoftwareTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(1));

    /// <summary>Ce que la fenêtre lit sans droits : un navigateur à mettre à jour (orange).</summary>
    private static readonly Finding Outdated = new()
    {
        Id = "M20.software-updates",
        Title = "Logiciels à mettre à jour",
        Status = FindingStatus.Warning,
        Severity = Severity.Medium,
        Current = "1 logiciel(s) : Mozilla Firefox (129.0 → 130.0)",
        Explanation = "Liste lue sans droits.",
    };

    private sealed class LocalReads
    {
        public int Count { get; private set; }

        public Task<ModuleResult> ReadAsync(CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new ModuleResult("M20", "Mises à jour des logiciels", [Outdated], TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Moteur administrateur d'essai : le vrai M20, avec les droits administrateur, et un autre module.</summary>
    private static LocalMausEngine Elevated(UserPreferences? auditChoices = null, UserPreferences? storedChoices = null)
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        var journal = new InMemoryJournalStore();
        var audit = TestContext.Create(registry, parameters: parameters, elevated: true) with { Preferences = auditChoices ?? UserPreferences.Default };
        var store = new MemoryPreferencesStore();
        store.Save(storedChoices ?? UserPreferences.Default);
        return new LocalMausEngine(
            new AuditEngine([new VisualEffectsModule(), new SoftwareUpdatesModule(@"C:\Program Files")]),
            () => audit,
            context => TestFixContext.Create(context, registry, parameters, journal, new FakeSystemRestore(), new FakeNotifier(), false),
            store,
            journal);
    }

    private static (IMausEngine Engine, OnDemandEngineTests.Recorder Calls) Limited()
    {
        var engine = DispatchProxy.Create<IMausEngine, OnDemandEngineTests.Recorder>();
        return (engine, (OnDemandEngineTests.Recorder)(object)engine);
    }

    private static OnDemandMausEngine Running(IMausEngine limited, IMausEngine elevated, LocalReads reads) =>
        new(limited, _ => Task.FromResult(new ElevatedEngineSession(elevated, () => false)), reads.ReadAsync);

    [Fact]
    public void Listing_software_updates_is_not_an_engine_operation() =>
        Assert.DoesNotContain(typeof(IMausEngine).GetMethods(), m => m.Name.Contains("Software", StringComparison.Ordinal));

    [Fact]
    public async Task The_engine_audit_shows_the_list_read_without_rights_once_in_the_progress_and_in_the_result()
    {
        var (limited, limitedCalls) = Limited();
        var reads = new LocalReads();
        using var engine = Running(limited, Elevated(), reads);
        await engine.EnsureElevatedAsync(CancellationToken.None);
        var progressed = new List<ModuleResult>();

        var run = await engine.RunAuditAsync(new SyncProgress<ModuleResult>(r => { lock (progressed) { progressed.Add(r); } }), CancellationToken.None);

        Assert.Equal(1, reads.Count);
        Assert.Empty(limitedCalls.Calls);
        var software = Assert.Single(run.Results, r => r.ModuleId == "M20");
        Assert.Equal(FindingStatus.Warning, Assert.Single(software.Findings).Status);
        Assert.Equal(run.Results.Count, progressed.Count);
        Assert.Equal(FindingStatus.Warning, Assert.Single(Assert.Single(progressed, r => r.ModuleId == "M20").Findings).Status);
        Assert.Contains(run.Results, r => r.ModuleId == "M06");
    }

    [Fact]
    public async Task A_wanted_mark_kept_by_the_engine_applies_to_the_list_read_without_rights()
    {
        var (limited, _) = Limited();
        var wanted = UserPreferences.Default.Acknowledge(Outdated, Now);
        var reads = new LocalReads();
        using var engine = Running(limited, Elevated(auditChoices: wanted, storedChoices: wanted), reads);
        await engine.EnsureElevatedAsync(CancellationToken.None);

        var run = await engine.RunAuditAsync(null, CancellationToken.None);
        var again = await engine.RunModuleAsync("M20", CancellationToken.None);

        Assert.Equal(FindingStatus.Info, Assert.Single(Assert.Single(run.Results, r => r.ModuleId == "M20").Findings).Status);
        Assert.Equal(FindingStatus.Info, Assert.Single(again!.Findings).Status);
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public async Task A_software_module_left_unticked_never_starts_winget()
    {
        var (limited, _) = Limited();
        var reads = new LocalReads();
        using var engine = Running(limited, Elevated(auditChoices: UserPreferences.Default with { SkippedModules = ["M20"] }), reads);
        await engine.EnsureElevatedAsync(CancellationToken.None);
        var progressed = new List<ModuleResult>();

        var run = await engine.RunAuditAsync(new SyncProgress<ModuleResult>(r => { lock (progressed) { progressed.Add(r); } }), CancellationToken.None);

        Assert.Equal(0, reads.Count);
        Assert.True(Assert.Single(run.Results, r => r.ModuleId == "M20").NotChosen);
        Assert.True(Assert.Single(progressed, r => r.ModuleId == "M20").NotChosen);
    }

    [Fact]
    public async Task Without_a_running_engine_the_window_audits_alone_and_nothing_is_read_twice()
    {
        var (limited, limitedCalls) = Limited();
        var reads = new LocalReads();
        using var engine = new OnDemandMausEngine(limited, _ => throw new InvalidOperationException("ne doit pas démarrer"), reads.ReadAsync);

        await engine.RunAuditAsync(null, CancellationToken.None);

        Assert.Equal(0, reads.Count);
        Assert.Equal(["RunAuditAsync"], limitedCalls.Calls);
    }

    [Fact]
    public async Task A_window_started_as_administrator_says_why_the_list_is_not_read()
    {
        using var local = Elevated();

        var run = await local.RunAuditAsync(null, CancellationToken.None);

        var finding = Assert.Single(Assert.Single(run.Results, r => r.ModuleId == "M20").Findings);
        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("droits administrateur", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_reading_without_rights_is_a_module_error_never_the_engine_text()
    {
        var (limited, _) = Limited();
        using var engine = new OnDemandMausEngine(limited, _ => Task.FromResult(new ElevatedEngineSession(Elevated(), () => false)),
            _ => throw new InvalidOperationException("panne simulée"));
        await engine.EnsureElevatedAsync(CancellationToken.None);

        var run = await engine.RunAuditAsync(null, CancellationToken.None);

        var software = Assert.Single(run.Results, r => r.ModuleId == "M20");
        Assert.Empty(software.Findings);
        Assert.Contains("panne simulée", software.Error, StringComparison.Ordinal);
    }
}
