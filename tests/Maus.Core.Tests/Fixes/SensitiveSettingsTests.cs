using Maus.Cli;
using Maus.Core.Engine;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Modules.M12Startup;
using Maus.Core.Preferences;
using Maus.Core.Tests.Modules.M12Startup;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public class SensitiveSettingsTests
{
    private const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string SessionId = "20261102-100000-abcdef";
    private const string Original = "programme-d-origine.exe";
    private const string Corrected = "explorer.exe";

    private sealed class Answer(bool yes) : IEngineConfirmation
    {
        public List<string> Asked { get; } = [];

        public bool Confirm(string title, string message)
        {
            Asked.Add(title + " | " + message);
            return yes;
        }
    }

    [Theory]
    [InlineData("HKLM", Winlogon, "Shell")]
    [InlineData("HKLM", Winlogon, "Userinit")]
    [InlineData("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit_DLLs")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows", "LoadAppInit_DLLs")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\app.exe", "Debugger")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\WindowsFirewall\DomainProfile", "EnableFirewall")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUStatusServer")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DoNotConnectToWindowsUpdateInternetLocations")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\UsoSvc", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\WaaSMedicSvc", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\BITS", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\CryptSvc", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\TrustedInstaller", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask")]
    public void What_starts_a_program_or_carries_a_security_policy_is_sensitive(string hive, string path, string name) =>
        Assert.True(SensitiveSettings.IsSensitive(SettingKey.Registry(hive, path, name)));

    [Theory]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\WSearch", "Start")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Steam")]
    public void An_ordinary_setting_is_not_sensitive(string hive, string path, string name) =>
        Assert.False(SensitiveSettings.IsSensitive(SettingKey.Registry(hive, path, name)));

    [Fact]
    public void Removing_a_value_that_maus_added_puts_nothing_back()
    {
        var entry = new JournalEntry { ChangeId = "c", ModuleId = "M01", ChangeTitle = "t", Key = SettingKey.Registry("HKLM", Winlogon, "Shell"), Before = null, After = SettingValue.Text(Corrected) };

        Assert.False(SensitiveSettings.IsSensitive(entry));
    }

    [Fact]
    public void A_value_read_on_the_pc_is_shown_on_one_line_and_within_limits()
    {
        Assert.Equal("a  b", SensitiveSettings.Shown("a\r\nb"));
        Assert.DoesNotContain('\u202E', SensitiveSettings.Shown("a\u202Eb"));
        Assert.Equal(301, SensitiveSettings.Shown(new string('x', 5000)).Length);
    }

    private static JournalEntry Entry(string changeId, SettingKey key, SettingValue? before, SettingValue? after) => new()
    {
        ChangeId = changeId,
        ModuleId = "M01",
        ChangeTitle = changeId,
        Key = key,
        Before = before,
        After = after,
        ContainerExisted = true,
        State = EntryState.Applied,
    };

    [Fact]
    public void A_group_undo_leaves_out_the_update_and_kernel_protections()
    {
        var session = new JournalSession
        {
            Id = SessionId,
            CreatedAt = DateTimeOffset.Now,
            Entries =
            {
                Entry("M01.wu-server", SettingKey.Registry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer"), SettingValue.Text("serveur"), null),
                Entry("M01.wu-services.BITS", SettingKey.Registry("HKLM", @"SYSTEM\CurrentControlSet\Services\BITS", "Start"), SettingValue.Dword(4), SettingValue.Dword(3)),
                Entry("M13.blocklist", SettingKey.Registry("HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable"), SettingValue.Dword(0), SettingValue.Dword(1)),
                Entry("M06.taskview", SettingKey.Registry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton"), SettingValue.Dword(1), SettingValue.Dword(0)),
            },
        };

        Assert.Equal(["M06.taskview"], SensitiveSettings.OrdinaryChanges(session));
        Assert.Equal(3, SensitiveSettings.Pending(session).Count);
    }

    private static JournalEntry SensitiveEntry() =>
        Entry("M01.winlogon", SettingKey.Registry("HKLM", Winlogon, "Shell"), SettingValue.Text(Original), SettingValue.Text(Corrected));

    private static (LocalMausEngine Engine, FakeRegistry Registry, InMemoryJournalStore Journal) Local(IEngineConfirmation? confirmation, string current = Corrected)
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, Winlogon, "Shell", current);
        var journal = new InMemoryJournalStore();
        journal.Save(new JournalSession { Id = SessionId, CreatedAt = DateTimeOffset.Now, Entries = { SensitiveEntry() } });
        var engine = new LocalMausEngine(
            new AuditEngine([]),
            () => TestContext.Create(registry, elevated: true),
            context => TestFixContext.Create(context, registry, journal: journal),
            new MemoryPreferencesStore(),
            journal)
        {
            Confirmation = confirmation,
        };
        return (engine, registry, journal);
    }

    [Fact]
    public async Task A_sensitive_value_is_not_restored_when_the_engine_window_says_no()
    {
        var no = new Answer(false);
        var (engine, registry, _) = Local(no);
        using var _1 = engine;

        var result = await engine.RevertAsync(SessionId, null, false, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Entries);
        Assert.Equal(Corrected, registry.GetValue(RegistryHive.LocalMachine, Winlogon, "Shell"));
        var question = Assert.Single(no.Asked);
        Assert.Contains(Original, question, StringComparison.Ordinal);
        Assert.Contains(Winlogon, question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensitive_value_is_restored_after_yes()
    {
        var yes = new Answer(true);
        var (engine, registry, _) = Local(yes);
        using var _1 = engine;

        var result = await engine.RevertAsync(SessionId, "M01.winlogon", false, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Single(yes.Asked);
        Assert.Equal(Original, registry.GetValue(RegistryHive.LocalMachine, Winlogon, "Shell"));
    }

    [Fact]
    public async Task Nothing_is_asked_when_the_value_would_not_be_written()
    {
        var no = new Answer(false);
        var (engine, registry, _) = Local(no, current: "autre-valeur.exe");
        using var _1 = engine;

        var result = await engine.RevertAsync(SessionId, null, false, CancellationToken.None);

        Assert.Empty(no.Asked);
        Assert.Equal(RevertStatus.ChangedSince, Assert.Single(result.Entries).Status);
        Assert.Equal("autre-valeur.exe", registry.GetValue(RegistryHive.LocalMachine, Winlogon, "Shell"));
    }

    [Fact]
    public async Task Undoing_an_ordinary_fix_asks_nothing_more()
    {
        const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        var no = new Answer(false);
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 0);
        var journal = new InMemoryJournalStore();
        journal.Save(new JournalSession
        {
            Id = SessionId,
            CreatedAt = DateTimeOffset.Now,
            Entries = { Entry("M06.taskview", SettingKey.Registry("HKCU", Advanced, "ShowTaskViewButton"), SettingValue.Dword(1), SettingValue.Dword(0)) },
        });
        using var engine = new LocalMausEngine(new AuditEngine([]), () => TestContext.Create(registry, elevated: true), context => TestFixContext.Create(context, registry, journal: journal), new MemoryPreferencesStore(), journal)
        {
            Confirmation = no,
        };

        var result = await engine.RevertAsync(SessionId, null, false, CancellationToken.None);

        Assert.Empty(no.Asked);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task A_window_started_as_administrator_has_no_second_window()
    {
        var (engine, registry, _) = Local(null);
        using var _1 = engine;

        var result = await engine.RevertAsync(SessionId, null, false, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(Original, registry.GetValue(RegistryHive.LocalMachine, Winlogon, "Shell"));
    }

    [Fact]
    public void The_command_line_tool_keeps_yes_and_prints_the_value()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, Winlogon, "Shell", Corrected);
        var journal = new InMemoryJournalStore();
        journal.Save(new JournalSession { Id = SessionId, CreatedAt = DateTimeOffset.Now, Entries = { SensitiveEntry() } });
        using var output = new StringWriter();

        var code = FixCommands.Revert(TestFixContext.Create(TestContext.Create(registry, elevated: true), registry, journal: journal), SessionId, null, false, assumeYes: true, output);

        Assert.Equal(0, code);
        Assert.Contains(Original, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Original, registry.GetValue(RegistryHive.LocalMachine, Winlogon, "Shell"));
    }

    private const string StartupRun = StartupAppsModule.RunPath;
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string HealthExe = @"C:\Program Files\Essai\SecurityHealthSystray.exe";

    private static (LocalMausEngine Engine, FakeRegistry Registry, InMemoryJournalStore Journal) Startup(IEngineConfirmation confirmation, MemoryPreferencesStore? preferences = null)
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, StartupRun, "Steam", $"\"{SteamExe}\" -silent")
            .Set(RegistryHive.CurrentUser, StartupRun, "SecurityHealth", $"\"{HealthExe}\"");
        var files = new FakeFiles().AddFile(SteamExe).SetVersionInfo(SteamExe, "Valve Corporation", "Steam").AddFile(HealthExe);
        var journal = new InMemoryJournalStore();
        var engine = new LocalMausEngine(
            new AuditEngine([new StartupAppsModule(new FakeStartupEnvironment())]),
            () => TestContext.Create(registry, files: files, elevated: true),
            context => TestFixContext.Create(context, registry, journal: journal),
            preferences ?? new MemoryPreferencesStore(),
            journal)
        {
            Confirmation = confirmation,
        };
        return (engine, registry, journal);
    }

    private static readonly ApplyOptions NoRestorePoint = new() { CreateRestorePoint = false };

    [Fact]
    public async Task Touching_a_security_program_at_startup_waits_for_the_engine_window()
    {
        var no = new Answer(false);
        var (engine, registry, journal) = Startup(no);
        using var _1 = engine;
        await engine.RunAuditAsync(null, CancellationToken.None);
        var plan = await engine.PlanAsync(CancellationToken.None);
        var change = plan.Single(c => c.EngineQuestion is not null);

        var result = await engine.ApplyAsync([change.Id], NoRestorePoint, CancellationToken.None);

        Assert.True(result.Blocked);
        Assert.Single(no.Asked);
        Assert.Empty(registry.Writes);
        Assert.Empty(journal.List());
    }

    [Fact]
    public async Task An_ordinary_startup_fix_asks_nothing_more()
    {
        var no = new Answer(false);
        var (engine, registry, _) = Startup(no);
        using var _1 = engine;
        await engine.RunAuditAsync(null, CancellationToken.None);

        var result = await engine.ApplyAsync(["M12.hkcu-run-steam"], NoRestorePoint, CancellationToken.None);

        Assert.Empty(no.Asked);
        Assert.Equal(1, result.AppliedCount);
        Assert.NotEmpty(registry.Writes);
    }

    [Fact]
    public async Task The_question_comes_from_the_plan_of_the_engine_and_an_unknown_id_asks_nothing()
    {
        var no = new Answer(false);
        var (engine, _, _) = Startup(no);
        using var _1 = engine;
        await engine.RunAuditAsync(null, CancellationToken.None);

        var result = await engine.ApplyAsync(["M12.inconnu"], NoRestorePoint, CancellationToken.None);

        Assert.Empty(no.Asked);
        Assert.True(result.Blocked);
    }

    private static readonly Finding Audited = new() { Id = "M01.defender", Title = "t", Status = FindingStatus.Info, Current = "valeur lue", Explanation = "e" };

    /// <summary>Une marque reprise dans la langue de l'audit du moteur après un changement de langue.</summary>
    [Fact]
    public void A_mark_taken_over_after_a_language_change_must_match_the_audit_of_the_engine()
    {
        var old = new Acknowledgement("M01.defender", "ancien texte", DateTimeOffset.UnixEpoch, "fr");
        var taken = old with { Current = "valeur lue", Language = "en" };

        Assert.True(LocalMausEngine.IsKnownAcknowledgement(taken, [old], [Audited], "en"));
        Assert.False(LocalMausEngine.IsKnownAcknowledgement(taken with { Current = "autre valeur" }, [old], [Audited], "en"));
    }

    [Fact]
    public void A_mark_taken_over_without_an_audit_of_the_engine_is_asked()
    {
        var old = new Acknowledgement("M01.defender", "ancien texte", DateTimeOffset.UnixEpoch, "fr");
        var taken = old with { Current = "valeur lue", Language = "en" };

        Assert.False(LocalMausEngine.IsKnownAcknowledgement(taken, [old], [], null));
        Assert.False(LocalMausEngine.IsKnownAcknowledgement(taken, [old], [Audited], null));
        Assert.False(LocalMausEngine.IsKnownAcknowledgement(taken, [old], [], "en"));
    }

    [Fact]
    public void Changing_only_the_language_of_a_mark_is_asked()
    {
        var old = new Acknowledgement("M01.defender", "valeur lue", DateTimeOffset.UnixEpoch, "fr");

        Assert.False(LocalMausEngine.IsKnownAcknowledgement(old with { Language = "en" }, [old], [Audited], "fr"));
        Assert.False(LocalMausEngine.IsKnownAcknowledgement(old with { Language = null }, [old], [Audited], "fr"));
    }

    [Fact]
    public void A_mark_without_a_language_is_never_taken_over()
    {
        var old = new Acknowledgement("M01.defender", "ancien texte", DateTimeOffset.UnixEpoch);

        Assert.False(LocalMausEngine.IsKnownAcknowledgement(old with { Current = "valeur lue", Language = "en" }, [old], [Audited], "en"));
    }

    [Fact]
    public void A_mark_kept_as_it_was_asks_nothing()
    {
        var old = new Acknowledgement("M01.defender", "ancien texte", DateTimeOffset.UnixEpoch, "fr");

        Assert.True(LocalMausEngine.IsKnownAcknowledgement(old, [old], [], null));
    }

    /// <summary>Le parcours normal : audit du moteur dans la nouvelle langue, puis marque réécrite, sans aucune question.</summary>
    [Fact]
    public async Task Rewriting_a_mark_after_the_audit_of_the_engine_asks_nothing()
    {
        var no = new Answer(false);
        var store = new MemoryPreferencesStore();
        var (engine, _, _) = Startup(no, store);
        using var _1 = engine;
        var language = Maus.Core.Localization.Texts.Language;
        var read = (await engine.RunAuditAsync(null, CancellationToken.None)).Results.SelectMany(r => r.Findings).First(f => f.Current is not null);
        store.Save(UserPreferences.Default with { Acknowledged = [new Acknowledgement(read.Id, "ancien texte", DateTimeOffset.UnixEpoch, language == "en" ? "fr" : "en")] });

        var state = await engine.SavePreferencesAsync(UserPreferences.Default with { Acknowledged = [new Acknowledgement(read.Id, read.Current, DateTimeOffset.UnixEpoch, language)] }, CancellationToken.None);

        Assert.Null(state.NotSaved);
        Assert.Empty(no.Asked);
        Assert.Equal(read.Current, Assert.Single(store.Load().Acknowledged).Current);
    }
}
