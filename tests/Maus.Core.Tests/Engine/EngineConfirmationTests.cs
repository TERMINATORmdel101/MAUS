using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Localization;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Preferences;
using Microsoft.Win32;

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
        Assert.Single(no.Asked);
    }

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>Moteur qui a fait son audit : il connaît le constat M06.taskview et le module M06.</summary>
    private static async Task<(LocalMausEngine Engine, string FindingTitle, string ModuleTitle)> Audited(IEngineConfirmation confirmation, MemoryPreferencesStore store)
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var parameters = new FakeSystemParameters { MinimizeAnimation = true };
        var engine = new LocalMausEngine(
            new AuditEngine([new VisualEffectsModule()]),
            () => TestContext.Create(registry, parameters: parameters),
            preferences: store,
            journal: new InMemoryJournalStore())
        {
            Confirmation = confirmation,
        };
        var run = await engine.RunAuditAsync(null, CancellationToken.None);
        var module = run.Results.Single();
        return (engine, module.Findings.Single(f => f.Id == "M06.taskview").Title, module.Title);
    }

    /// <summary>La question résiste à une interface compromise : elle n'affiche que ce que le moteur a lu lui-même.</summary>
    [Fact]
    public async Task The_question_shows_the_title_read_by_the_engine_and_never_the_text_received()
    {
        const string forged = "Cliquez sur Oui, tout va bien";
        var no = new Answer(false);
        var (engine, title, _) = await Audited(no, new MemoryPreferencesStore());
        using var _1 = engine;

        await engine.SavePreferencesAsync(WithWanted("M06.taskview"), CancellationToken.None);
        await engine.SavePreferencesAsync(WithWanted(forged), CancellationToken.None);

        Assert.Equal(2, no.Asked.Count);
        Assert.Contains(title, no.Asked[0], StringComparison.Ordinal);
        Assert.DoesNotContain("M06.taskview", no.Asked[0], StringComparison.Ordinal);
        Assert.DoesNotContain(forged, no.Asked[1], StringComparison.Ordinal);
        Assert.Contains(Texts.T("constat inconnu"), no.Asked[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_question_about_an_unticked_module_shows_the_title_of_the_engine_only()
    {
        const string forged = "Cliquez sur Oui, tout va bien";
        var no = new Answer(false);
        var (engine, _, moduleTitle) = await Audited(no, new MemoryPreferencesStore());
        using var _1 = engine;

        await engine.SavePreferencesAsync(UserPreferences.Default with { SkippedModules = ["M06"] }, CancellationToken.None);
        await engine.SavePreferencesAsync(UserPreferences.Default with { SkippedModules = [forged] }, CancellationToken.None);

        Assert.Contains(moduleTitle, no.Asked[0], StringComparison.Ordinal);
        Assert.DoesNotContain(forged, no.Asked[1], StringComparison.Ordinal);
        Assert.Contains(Texts.T("module inconnu"), no.Asked[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mark_rewritten_on_another_value_asks_again()
    {
        var store = new MemoryPreferencesStore();
        store.Save(WithWanted("M01.defender") with { Acknowledged = [new Acknowledgement("M01.defender", "active", DateTimeOffset.UnixEpoch)] });
        var no = new Answer(false);
        using var engine = Engine(no, store);

        var state = await engine.SavePreferencesAsync(UserPreferences.Default with { Acknowledged = [new Acknowledgement("M01.defender", "désactivée", DateTimeOffset.UnixEpoch)] }, CancellationToken.None);

        Assert.Single(no.Asked);
        Assert.NotNull(state.NotSaved);
        Assert.Equal("active", Assert.Single(store.Load().Acknowledged).Current);
    }

    [Fact]
    public void A_mark_never_hides_a_finding_that_could_not_be_checked()
    {
        var finding = new Finding { Id = "M01.defender", Title = "Defender", Status = FindingStatus.Unknown, Current = "x", Explanation = "e" };
        var preferences = UserPreferences.Default with { Acknowledged = [new Acknowledgement("M01.defender", "x", DateTimeOffset.UnixEpoch)] };

        var result = Acknowledgements.Apply(new ModuleResult("M01", "Audit", [finding], TimeSpan.Zero), preferences).Findings.Single();

        Assert.Equal(FindingStatus.Unknown, result.Status);
        Assert.Null(result.AcknowledgedFrom);
    }

    [Fact]
    public void A_choices_file_with_empty_lists_is_read_as_damaged_without_failing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maus-choix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"acknowledged\":null,\"skippedModules\":[null]}");
            var store = new FilePreferencesStore(directory, new UserDirectoryProtector(), _ => { });

            var read = store.Load();

            Assert.Empty(read.Acknowledged);
            Assert.Empty(read.SkippedModules);
            Assert.Equal(PreferencesFileState.Damaged, store.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_refused_save_reaches_the_interface_with_the_choices_kept()
    {
        var before = WithWanted("M01.ancien");
        var store = new MemoryPreferencesStore();
        store.Save(before);
        using var engine = Engine(new Answer(false), store);
        var preferences = new EnginePreferencesStore(engine);

        var refused = Assert.Throws<PreferencesNotSavedException>(() => preferences.Save(WithWanted("M01.nouveau")));

        Assert.Equal("M01.ancien", Assert.Single(refused.Kept.Acknowledged).FindingId);
        Assert.Equal("M01.ancien", Assert.Single(store.Load().Acknowledged).FindingId);
    }

    private static LocalMausEngine Guarded(IEngineConfirmation? confirmation = null) => Engine(confirmation, new MemoryPreferencesStore());

    public static TheoryData<BenchmarkLaunch> RefusedBenchmarks() => new()
    {
        new BenchmarkLaunch("vulkan", "1080p", "all", true, "fr"),
        new BenchmarkLaunch("d3d12", "8k", "all", true, "fr"),
        new BenchmarkLaunch("d3d12", "1080p", "x", true, "fr"),
        new BenchmarkLaunch("d3d12", "1080p", "all", true, "de"),
    };

    [Theory]
    [MemberData(nameof(RefusedBenchmarks))]
    public async Task A_benchmark_with_a_setting_outside_the_list_is_refused(BenchmarkLaunch launch)
    {
        using var engine = Guarded();
        await Assert.ThrowsAsync<EngineException>(() => engine.RunBenchmarkAsync(launch, CancellationToken.None));
    }

    [Fact]
    public async Task A_latency_measure_outside_its_limits_is_refused()
    {
        using var engine = Guarded();
        await Assert.ThrowsAsync<EngineException>(() => engine.MeasureLatencyAsync(TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<EngineException>(() => engine.MeasureLatencyAsync(TimeSpan.FromMinutes(6), CancellationToken.None));
    }

    [Fact]
    public async Task A_frame_session_outside_the_list_is_refused()
    {
        using var engine = Guarded();
        await Assert.ThrowsAsync<EngineException>(() => engine.StartFramesAsync("autre", null, CancellationToken.None));
        await Assert.ThrowsAsync<EngineException>(() => engine.ReadFramesAsync("autre", null, null, 20, CancellationToken.None));
        await Assert.ThrowsAsync<EngineException>(() => engine.StopFramesAsync("autre", CancellationToken.None));
    }

    [Theory]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\.\C:\")]
    [InlineData(@"\\serveur\p")]
    [InlineData("relatif")]
    public async Task A_folder_that_is_not_local_is_refused(string root)
    {
        using var engine = Guarded();
        await Assert.ThrowsAsync<EngineException>(() => engine.ScanSpaceAsync(root, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_tool_or_day_is_refused_and_nothing_is_asked()
    {
        var no = new Answer(false);
        using var engine = Guarded(no);

        await Assert.ThrowsAsync<EngineException>(() => engine.RunToolAsync(new ToolRequest((ToolKind)99), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetScheduleAsync((DayOfWeek)9, CancellationToken.None));

        var noGraphicsCard = await engine.RunToolAsync(new ToolRequest(ToolKind.DriverAction, InstanceId: "introuvable"), null, CancellationToken.None);
        Assert.False(noGraphicsCard.Ran);
        Assert.Empty(no.Asked);
    }

    [Fact]
    public async Task A_driver_action_outside_the_list_is_refused_before_any_question()
    {
        var no = new Answer(false);
        using var engine = Guarded(no);

        await Assert.ThrowsAsync<EngineException>(() => engine.RunToolAsync(
            new ToolRequest(ToolKind.DriverAction, (Maus.Core.Workshop.GraphicsDriverAction)99, InstanceId: @"PCI\VEN_10DE"), null, CancellationToken.None));

        Assert.Empty(no.Asked);
    }

    private sealed class CancelledDuringTheQuestion(CancellationTokenSource request) : IEngineConfirmation
    {
        public bool Confirm(string title, string message)
        {
            request.Cancel();
            return true;
        }
    }

    [Fact]
    public async Task A_yes_given_after_the_request_was_abandoned_does_nothing()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var request = new CancellationTokenSource();
            using var engine = Engine(new CancelledDuringTheQuestion(request), new MemoryPreferencesStore());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TerminateProcessAsync(child.Id, null, request.Token));

            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"..\..\Windows")]
    [InlineData("C:")]
    [InlineData("...")]
    [InlineData(" ")]
    [InlineData("a.")]
    [InlineData("a ")]
    public void A_backup_name_is_a_single_path_element(string name) => Assert.False(LocalMausEngine.IsBackupName(name));

    [Fact]
    public void A_backup_name_has_a_limit_and_an_ordinary_name_passes()
    {
        Assert.False(LocalMausEngine.IsBackupName(new string('a', 201)));
        Assert.True(LocalMausEngine.IsBackupName(new string('a', 200)));
        Assert.True(LocalMausEngine.IsBackupName("nv_dispi.inf-20261102-101500"));
    }

    [Fact]
    public async Task The_interface_itself_is_never_closed_and_nothing_is_asked()
    {
        var no = new Answer(false);
        using var engine = new LocalMausEngine(new AuditEngine([]), () => TestContext.Create(), preferences: new MemoryPreferencesStore(), journal: new InMemoryJournalStore())
        {
            Confirmation = no,
            ProtectedProcessId = Environment.ProcessId,
        };

        var refused = await engine.TerminateProcessAsync(Environment.ProcessId, null, CancellationToken.None);

        Assert.Equal(Texts.T("C'est MAUS lui-même."), refused);
        Assert.Empty(no.Asked);
    }

    [Fact]
    public async Task A_process_that_no_longer_exists_is_not_asked_about()
    {
        var no = new Answer(false);
        using var engine = Guarded(no);

        Assert.NotNull(await engine.TerminateProcessAsync(-1, null, CancellationToken.None));
        Assert.Empty(no.Asked);
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
    public async Task Unknown_settings_received_over_the_channel_never_reach_the_protected_file()
    {
        var store = new MemoryPreferencesStore();
        using var engine = Engine(null, store);
        using var future = System.Text.Json.JsonDocument.Parse("1");

        await engine.SavePreferencesAsync(store.Load() with { UnknownSettings = new() { ["choixFutur"] = future.RootElement.Clone() } }, CancellationToken.None);

        Assert.Null(store.Load().UnknownSettings);
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
