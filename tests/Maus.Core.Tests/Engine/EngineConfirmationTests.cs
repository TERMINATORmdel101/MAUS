using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Preferences;

namespace Maus.Core.Tests.Engine;

/// <summary>Point 5 du rapport de sécurité (0.8.4) : les actions sensibles attendent le « Oui » donné dans la fenêtre du moteur.</summary>
public class EngineConfirmationTests
{
    private sealed class Answer(bool yes) : IEngineConfirmation
    {
        public List<string> Asked { get; } = [];

        public bool Confirm(string title, string message)
        {
            Asked.Add(title + " | " + message);
            return yes;
        }
    }

    private static LocalMausEngine Engine(IEngineConfirmation? confirmation, MemoryPreferencesStore preferences) => new(
        new AuditEngine([new VisualEffectsModule()]),
        () => TestContext.Create(),
        preferences: preferences,
        journal: new InMemoryJournalStore())
    {
        Confirmation = confirmation,
    };

    private static UserPreferences WithWanted(string findingId) => UserPreferences.Default with
    {
        Acknowledged = [new Acknowledgement(findingId, "désactivé", DateTimeOffset.UnixEpoch)],
    };

    [Fact]
    public async Task Marking_a_finding_wanted_is_refused_when_the_engine_window_says_no()
    {
        var store = new MemoryPreferencesStore();
        var no = new Answer(false);
        using var engine = Engine(no, store);

        var state = await engine.SavePreferencesAsync(WithWanted("M01.defender"), CancellationToken.None);

        Assert.Empty(store.Load().Acknowledged);
        Assert.NotNull(state.NotSaved);
        Assert.Contains("M01.defender", Assert.Single(no.Asked), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marking_a_finding_wanted_is_saved_after_yes_and_other_choices_never_ask()
    {
        var store = new MemoryPreferencesStore();
        var yes = new Answer(true);
        using var engine = Engine(yes, store);

        await engine.SavePreferencesAsync(WithWanted("M01.defender"), CancellationToken.None);
        await engine.SavePreferencesAsync(store.Load() with { Language = "en" }, CancellationToken.None);

        Assert.Equal("M01.defender", Assert.Single(store.Load().Acknowledged).FindingId);
        Assert.Equal("en", store.Load().Language);
        Assert.Single(yes.Asked);
    }

    [Fact]
    public async Task Without_a_separate_engine_nothing_more_is_asked()
    {
        var store = new MemoryPreferencesStore();
        using var engine = Engine(null, store);

        await engine.SavePreferencesAsync(WithWanted("M01.defender"), CancellationToken.None);

        Assert.Single(store.Load().Acknowledged);
    }

    [Fact]
    public async Task PawnIo_console_is_not_opened_when_the_engine_window_says_no()
    {
        var no = new Answer(false);
        using var engine = Engine(no, new MemoryPreferencesStore());

        var install = await engine.OpenConsoleAsync(new ConsoleRequest(ConsoleKind.PawnIoInstall), CancellationToken.None);
        var uninstall = await engine.OpenConsoleAsync(new ConsoleRequest(ConsoleKind.PawnIoUninstall), CancellationToken.None);

        Assert.False(install.Opened);
        Assert.False(uninstall.Opened);
        Assert.Equal(2, no.Asked.Count);
    }

    [Fact]
    public async Task Closing_a_program_waits_for_yes_in_the_engine_window()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var no = new Answer(false);
            using var engine = Engine(no, new MemoryPreferencesStore());

            var refused = await engine.TerminateProcessAsync(child.Id, null, CancellationToken.None);

            Assert.NotNull(refused);
            Assert.False(child.HasExited);
            Assert.Contains("ping", Assert.Single(no.Asked), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill();
        }
    }
}
