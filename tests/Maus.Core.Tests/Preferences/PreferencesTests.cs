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

    [Fact]
    public void Unacknowledge_removes_the_mark()
    {
        var finding = new Finding { Id = "x", Title = "t", Status = FindingStatus.Problem, Explanation = "e" };
        var preferences = UserPreferences.Default.Acknowledge(finding, DateTimeOffset.Now).Unacknowledge("x");

        Assert.Empty(preferences.Acknowledged);
    }
}
