using Maus.Core.Engine;
using Maus.Core.Fixes;
using Maus.Core.Modules.M06Visual;
using Maus.Core.Preferences;
using Microsoft.Win32;

namespace Maus.Core.Tests.Preferences;

public sealed class PreferencesTests : IDisposable
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "maus-prefs-" + Guid.NewGuid().ToString("N"));

    private sealed class Protector(bool trusted = true) : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => Directory.CreateDirectory(directory);

        public bool IsTrusted(string file) => trusted;

        public void ProtectFile(string file)
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class UnsafeFolder : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => throw new JournalUnsafeException("dossier non sûr");

        public bool IsTrusted(string file) => throw new InvalidOperationException("rien ne doit être lu");

        public void ProtectFile(string file)
        {
        }
    }

    [Fact]
    public void Choices_are_not_read_from_a_folder_that_cannot_be_protected_and_the_reason_is_given()
    {
        var finding = new Finding { Id = "M01.defender", Title = "t", Status = FindingStatus.Problem, Current = "coupé", Explanation = "e" };
        new FilePreferencesStore(_directory, new Protector()).Save(UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now));
        var store = new FilePreferencesStore(_directory, new UnsafeFolder());

        var loaded = store.Load();

        Assert.Empty(loaded.Acknowledged);
        Assert.Equal(PreferencesFileState.Untrusted, store.State);
        Assert.Contains("dossier non sûr", store.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_round_trip_and_untrusted_file_is_ignored()
    {
        var finding = new Finding { Id = "M06.taskview", Title = "t", Status = FindingStatus.Improvable, Current = "1", Explanation = "e" };
        var preferences = UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now) with
        {
            GameBarProfile = 2,
            LaptopPower = LaptopPowerChoice.Battery,
        };

        new FilePreferencesStore(_directory, new Protector()).Save(preferences);
        var loaded = new FilePreferencesStore(_directory, new Protector()).Load();

        Assert.Equal(2, loaded.GameBarProfile);
        Assert.Equal(LaptopPowerChoice.Battery, loaded.LaptopPower);
        Assert.Equal("M06.taskview", loaded.Acknowledged.Single().FindingId);
        Assert.Same(UserPreferences.Default, new FilePreferencesStore(_directory, new Protector(trusted: false)).Load());
    }

    [Fact]
    public void A_file_written_by_0_8_5_is_still_read()
    {
        // Fichier recopié tel quel : ce qui est sur le disque des utilisateurs doit rester lisible.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "preferences.json"), """
            {
              "gameBarProfile": 2,
              "laptopPower": "Battery",
              "acknowledged": [
                { "findingId": "M06.taskview", "current": "1", "at": "2026-10-30T10:00:00+00:00", "language": "fr" }
              ],
              "skippedModules": [ "M20" ]
            }
            """);

        var store = new FilePreferencesStore(_directory, new Protector());
        var loaded = store.Load();

        Assert.Equal(2, loaded.GameBarProfile);
        Assert.Equal(LaptopPowerChoice.Battery, loaded.LaptopPower);
        Assert.Equal("1", loaded.Acknowledged.Single().Current);
        Assert.Equal("M06.taskview", loaded.Acknowledged.Single().FindingId);
        Assert.Equal(["M20"], loaded.SkippedModules);
        Assert.Null(store.Notice);
    }

    [Fact]
    public async Task Wanted_finding_becomes_information_and_gets_no_correction()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var context = TestContext.Create(registry);
        var engine = new AuditEngine([new VisualEffectsModule()]);
        var raw = await engine.RunAsync(context);
        var taskView = raw.Single().Findings.Single(f => f.Id == "M06.taskview");
        Assert.Contains(FixEngine.Plan(engine, raw, context), c => c.Id == "M06.taskview");

        var wanted = context.WithPreferences(UserPreferences.Default.Acknowledge(taskView, context.Now));
        var results = await engine.RunAsync(wanted);

        var finding = results.Single().Findings.Single(f => f.Id == "M06.taskview");
        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.Equal(FindingStatus.Improvable, finding.AcknowledgedFrom);
        Assert.Contains("voulu", finding.Advice, StringComparison.Ordinal);
        Assert.DoesNotContain(FixEngine.Plan(engine, results, wanted), c => c.Id == "M06.taskview");
    }

    [Fact]
    public async Task Wanted_mark_expires_when_the_situation_changes()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var context = TestContext.Create(registry);
        var engine = new AuditEngine([new VisualEffectsModule()]);
        var taskView = (await engine.RunAsync(context)).Single().Findings.Single(f => f.Id == "M06.taskview");
        var wanted = context.WithPreferences(UserPreferences.Default.Acknowledge(taskView, context.Now));

        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 2);
        var finding = (await engine.RunAsync(wanted)).Single().Findings.Single(f => f.Id == "M06.taskview");

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Null(finding.AcknowledgedFrom);
    }

    /// <summary>La 0.8.5 gardait la valeur sans limite : une seule marque longue ne doit pas faire refuser tous les choix par le moteur.</summary>
    [Fact]
    public void A_long_mark_of_0_8_5_is_bounded_at_read_and_stays_valid_for_the_same_value()
    {
        var value = string.Join(", ", Enumerable.Range(0, 60).Select(i => "Peripherique desactive " + i + " avec un nom assez long"));
        Assert.True(value.Length > Acknowledgement.MaxCurrentLength);
        var store = new FilePreferencesStore(_directory, new Protector());
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, $$"""{ "acknowledged": [ { "findingId": "M17.disabled", "current": "{{value}}", "at": "2026-10-26T12:00:00+00:00" } ] }""");
        Finding Seen(string current) => new() { Id = "M17.disabled", Title = "t", Status = FindingStatus.Warning, Current = current, Explanation = "e" };

        var read = store.Load();

        Assert.Equal(PreferencesFileState.Read, store.State);
        Assert.Null(store.Notice);
        Assert.True(Assert.Single(read.Acknowledged).Current!.Length <= Acknowledgement.MaxCurrentLength);
        Assert.Same(read, Maus.Core.Engine.Broker.EngineServer.CheckedPreferences(read));
        Assert.NotNull(read.AcknowledgementFor(Seen(value)));
        Assert.Null(read.AcknowledgementFor(Seen(value + ", un de plus")));

        store.Update(p => p with { MonitorOnTop = true });

        Assert.Null(store.Notice);
        Assert.NotNull(new FilePreferencesStore(_directory, new Protector()).Load().AcknowledgementFor(Seen(value)));
    }

    [Fact]
    public void A_setting_of_a_newer_version_survives_a_save_by_this_one()
    {
        var store = new FilePreferencesStore(_directory, new Protector());
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, """{ "theme": "Dark", "choixFutur": { "niveau": 3 } }""");

        store.Update(p => p with { MonitorOnTop = true });

        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("choixFutur", text, StringComparison.Ordinal);
        Assert.Equal(PreferencesFileState.Read, store.State);
        Assert.Equal(3, store.Load().UnknownSettings!["choixFutur"].GetProperty("niveau").GetInt32());
    }

    [Fact]
    public void Settings_removed_in_0_8_6_are_read_without_error_and_kept_in_the_file()
    {
        var store = new FilePreferencesStore(_directory, new Protector());
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, """
            {
              "theme": "Dark",
              "homeComplete": true,
              "micaBackdrop": true,
              "monitorAutoBand": false,
              "bandOpacity": 0.5,
              "bandPlacements": [ { "screens": "0,0,1920x1080", "left": 10, "top": 10 } ],
              "dramVoltage": { "board": "b", "hardware": "h", "sensor": "s", "factor": 2, "chosenVolts": 1.35 }
            }
            """);

        var read = store.Load();

        Assert.Equal(ThemeChoice.Dark, read.Theme);
        Assert.Equal(PreferencesFileState.Read, store.State);
        Assert.False(read.TechnicianMode);

        // Rien de ce que l'utilisateur avait écrit n'est effacé : retour possible à une version plus ancienne.
        store.Update(p => p with { MonitorOnTop = true });
        var text = File.ReadAllText(store.FilePath);
        foreach (var name in new[] { "homeComplete", "micaBackdrop", "monitorAutoBand", "bandOpacity", "bandPlacements", "dramVoltage" })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.85, 0.85)]
    [InlineData(0.1, 0.8)]
    [InlineData(9, 1.5)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void A_hand_edited_zoom_cannot_make_the_window_unreadable(double saved, double used) =>
        Assert.Equal(used, (UserPreferences.Default with { UiZoom = saved }).UiZoomUsed);

    [Fact]
    public void Unacknowledge_removes_the_mark()
    {
        var finding = new Finding { Id = "x", Title = "t", Status = FindingStatus.Problem, Explanation = "e" };
        var preferences = UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now).Unacknowledge("x");

        Assert.Empty(preferences.Acknowledged);
    }
}
