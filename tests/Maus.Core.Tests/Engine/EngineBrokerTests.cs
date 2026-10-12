using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Microsoft.Win32;

namespace Maus.Core.Tests.Engine;

/// <summary>
/// Canal privé entre l'interface et le moteur (0.8.0) : vrai serveur, vrai client, vrai canal nommé, moteur local branché
/// sur un registre simulé et le module M06 (effets visuels, corrigeable).
/// </summary>
public sealed class EngineBrokerTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static (LocalMausEngine Engine, FakeRegistry Registry, InMemoryJournalStore Journal) Local()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        parameters.Values[SpiGet.MenuAnimation] = true;
        var journal = new InMemoryJournalStore();
        var audit = TestContext.Create(registry, parameters: parameters, elevated: true);
        var engine = new LocalMausEngine(
            new AuditEngine([new VisualEffectsModule()]),
            () => audit,
            context => TestFixContext.Create(context, registry, parameters, journal, new FakeSystemRestore(), new FakeNotifier(), false),
            new MemoryPreferences(),
            journal);
        return (engine, registry, journal);
    }

    private static async Task<T> WithServer<T>(IMausEngine engine, Func<RemoteMausEngine, Task<T>> use, int? clientProcessId = null)
    {
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = new EngineServer(engine, name, clientProcessId ?? Environment.ProcessId).RunAsync(stop.Token);
        try
        {
            return await use(new RemoteMausEngine(name, Environment.ProcessId));
        }
        finally
        {
            await stop.CancelAsync();
            await server;
        }
    }

    [Fact]
    public async Task Audit_plan_apply_and_revert_go_through_the_channel()
    {
        var (local, registry, _) = Local();
        using var _ = local;
        var progressed = new List<string>();

        await WithServer(local, async remote =>
        {
            var ping = await remote.PingAsync(CancellationToken.None);
            Assert.Equal(Environment.ProcessId, ping.ProcessId);

            var run = await remote.RunAuditAsync(new SyncProgress<ModuleResult>(r => progressed.Add(r.ModuleId)), CancellationToken.None);
            Assert.Equal(["M06"], progressed);
            Assert.Contains(run.Results.Single().Findings, f => f.Id == "M06.taskview" && f.Status == FindingStatus.Improvable);

            var plan = await remote.PlanAsync(CancellationToken.None);
            Assert.Contains(plan, c => c.Id == "M06.taskview");

            var applied = await remote.ApplyAsync(["M06.taskview"], new ApplyOptions { CreateRestorePoint = false }, CancellationToken.None);
            Assert.Equal(ChangeStatus.Applied, applied.Changes.Single().Status);
            Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));

            var listing = await remote.BrowseJournalAsync(CancellationToken.None);
            var reverted = await remote.RevertAsync(listing.Sessions.Single().Id, null, false, CancellationToken.None);
            Assert.Contains(reverted.Entries, e => e.Status == RevertStatus.Reverted);
            Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
            return true;
        });
    }

    [Fact]
    public async Task Unknown_changes_are_ignored_and_bad_identifiers_or_operations_are_refused()
    {
        var (local, registry, _) = Local();
        using var _ = local;

        await WithServer(local, async remote =>
        {
            await remote.RunAuditAsync(null, CancellationToken.None);

            // Le moteur reprend les corrections dans son propre plan : un identifiant qu'il ne propose pas n'écrit rien.
            var applied = await remote.ApplyAsync(["M06.inventee"], new ApplyOptions { CreateRestorePoint = false }, CancellationToken.None);
            Assert.Empty(applied.Changes);
            Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));

            await Assert.ThrowsAsync<EngineException>(() => remote.LoadSessionAsync(@"..\..\Windows\win.ini", CancellationToken.None));
            return true;
        });
    }

    [Fact]
    public async Task Preferences_are_read_and_saved_by_the_engine()
    {
        var (local, _, _) = Local();
        using var _ = local;

        await WithServer(local, async remote =>
        {
            var store = new EnginePreferencesStore(remote);
            store.Update(p => p with { GameBarProfile = 2 });
            Assert.Equal(2, store.Load().GameBarProfile);
            return true;
        });
    }

    [Fact]
    public async Task A_connection_from_another_process_is_refused()
    {
        var (local, _, _) = Local();
        using var _ = local;

        // Le moteur attend un autre processus que celui des tests : il ferme la connexion sans répondre.
        await WithServer(local, async remote =>
        {
            await Assert.ThrowsAsync<EngineException>(() => remote.PingAsync(CancellationToken.None));
            return true;
        }, clientProcessId: 4);
    }

    [Fact]
    public async Task The_client_refuses_a_channel_held_by_another_process()
    {
        var (local, _, _) = Local();
        using var _ = local;
        var name = "MAUS-essai-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = new EngineServer(local, name, Environment.ProcessId).RunAsync(stop.Token);

        // L'interface attend le moteur qu'elle a lancé (autre identifiant) : elle ne se sert pas de ce canal.
        var remote = new RemoteMausEngine(name, serverProcessId: 4);
        await Assert.ThrowsAsync<EngineException>(() => remote.PingAsync(CancellationToken.None));

        await stop.CancelAsync();
        await server;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MemoryPreferences : IPreferencesStore
    {
        private UserPreferences _value = UserPreferences.Default;

        public UserPreferences Load() => _value;

        public void Save(UserPreferences preferences) => _value = preferences;
    }
}
