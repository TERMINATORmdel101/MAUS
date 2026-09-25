using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public class FixEngineTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Dsh";

    private static readonly SettingKey TaskView = SettingKey.Registry("HKCU", Advanced, "ShowTaskViewButton");
    private static readonly SettingKey Widgets = SettingKey.Registry("HKLM", Policy, "AllowNewsAndInterests");
    private static readonly SettingKey MenuAnimation = SettingKey.Spi(SpiGet.MenuAnimation, SpiSet.MenuAnimation);

    private static PlannedChange Change(string id, params SettingWrite[] writes) => new()
    {
        Id = id,
        ModuleId = "M06",
        Title = id,
        Description = "test",
        Writes = writes,
    };

    private static (FixEngine Engine, FakeRegistry Registry, FakeSystemParameters Parameters, InMemoryJournalStore Journal, FakeSystemRestore Restore, FakeNotifier Notifier) Setup(
        bool managed = false, bool elevated = true, bool anotherUser = false)
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var parameters = new FakeSystemParameters();
        parameters.Values[SpiGet.MenuAnimation] = true;
        var audit = TestContext.Create(registry, parameters: parameters, elevated: elevated,
            hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = managed });
        var journal = new InMemoryJournalStore();
        var restore = new FakeSystemRestore();
        var notifier = new FakeNotifier();
        var context = TestFixContext.Create(audit, registry, parameters, journal, restore, notifier, anotherUser);
        return (new FixEngine(context), registry, parameters, journal, restore, notifier);
    }

    [Fact]
    public void Apply_writes_journals_and_verifies()
    {
        var (engine, registry, parameters, journal, restore, notifier) = Setup();

        var result = engine.Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.menu", new SettingWrite(MenuAnimation, SettingValue.Bool(false)))],
            new ApplyOptions());

        Assert.False(result.Blocked);
        Assert.All(result.Changes, c => Assert.Equal(ChangeStatus.Applied, c.Status));
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.False(parameters.GetBool(SpiGet.MenuAnimation));
        Assert.Equal(1, notifier.Broadcasts);
        Assert.Equal(42, result.RestorePoint!.Point!.SequenceNumber);

        var saved = journal.Load(result.Session!.Id)!;
        Assert.Equal(42, saved.RestorePoint!.SequenceNumber);
        var entry = saved.Entries.Single(e => e.ChangeId == "M06.taskview");
        Assert.Equal(EntryState.Applied, entry.State);
        Assert.Equal("1", entry.Before!.Data);
        Assert.Equal("0", entry.After!.Data);
        Assert.True(restore.Points.Count == 2);
    }

    [Fact]
    public void Revert_restores_original_values_and_removes_created_keys()
    {
        var (engine, registry, parameters, _, _, _) = Setup();
        var applied = engine.Apply(
            [
                Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))),
                Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0))),
                Change("M06.menu", new SettingWrite(MenuAnimation, SettingValue.Bool(false))),
            ],
            new ApplyOptions());
        Assert.Equal(0, registry.GetDword(RegistryHive.LocalMachine, Policy, "AllowNewsAndInterests"));

        var reverted = engine.Revert(applied.Session!.Id);

        Assert.True(reverted.Completed);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, Policy, "AllowNewsAndInterests"));
        Assert.False(registry.KeyExists(RegistryHive.LocalMachine, Policy));
        Assert.True(parameters.GetBool(SpiGet.MenuAnimation));
        Assert.NotNull(reverted.Session!.RevertedAt);
        Assert.False(reverted.Session.CanRevert);
    }

    [Fact]
    public void Revert_leaves_values_changed_since_unless_forced()
    {
        var (engine, registry, _, _, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 5);

        var first = engine.Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.ChangedSince, first.Entries.Single().Status);
        Assert.Equal(5, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Forced_revert_overwrites_values_changed_since()
    {
        var (engine, registry, _, _, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 5);

        var forced = engine.Revert(applied.Session!.Id, force: true);

        Assert.True(forced.Completed);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Managed_pc_blocks_every_change()
    {
        var (engine, registry, _, journal, restore, _) = Setup(managed: true);

        var result = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.True(result.Blocked);
        Assert.Contains("géré", result.BlockedReason, StringComparison.Ordinal);
        Assert.Empty(registry.Writes);
        Assert.Empty(journal.List());
        Assert.Single(restore.Points);
    }

    [Fact]
    public void Non_elevated_process_is_blocked()
    {
        var (engine, registry, _, _, _, _) = Setup(elevated: false);

        var result = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.True(result.Blocked);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void Failed_restore_point_blocks_unless_user_accepts()
    {
        var (engine, registry, _, _, restore, _) = Setup();
        restore.SilentlySkip = true;
        PlannedChange[] changes = [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))];

        var blocked = engine.Apply(changes, new ApplyOptions());
        Assert.True(blocked.Blocked);
        Assert.Equal(RestorePointStatus.Failed, blocked.RestorePoint!.Status);
        Assert.DoesNotContain(registry.Writes, w => w.Contains("ShowTaskViewButton", StringComparison.Ordinal));

        var accepted = engine.Apply(changes, new ApplyOptions { ProceedWithoutRestorePoint = true });
        Assert.False(accepted.Blocked);
        Assert.Equal(ChangeStatus.Applied, accepted.Changes.Single().Status);
        Assert.NotNull(accepted.Session!.RestorePointNote);
    }

    [Fact]
    public void Change_is_all_or_nothing_when_a_write_is_denied()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        registry.DenyWrite(RegistryHive.LocalMachine, Policy);

        var result = engine.Apply(
            [Change("M06.pair", new SettingWrite(TaskView, SettingValue.Dword(0)), new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        Assert.Equal(ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.All(journal.Load(result.Session!.Id)!.Entries, e => Assert.Equal(EntryState.Failed, e.State));
    }

    [Fact]
    public void Value_not_kept_by_windows_is_rolled_back()
    {
        var (engine, registry, _, _, _, _) = Setup();
        registry.IgnoreWrites(RegistryHive.LocalMachine, Policy);

        var result = engine.Apply(
            [Change("M06.pair", new SettingWrite(TaskView, SettingValue.Dword(0)), new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        Assert.Equal(ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.Contains("pas gardé", result.Changes.Single().Message, StringComparison.Ordinal);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Rejected_system_parameter_fails_cleanly()
    {
        var (engine, _, parameters, _, _, _) = Setup();
        parameters.RejectWrites = true;

        var result = engine.Apply([Change("M06.menu", new SettingWrite(MenuAnimation, SettingValue.Bool(false)))], new ApplyOptions());

        Assert.Equal(ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.True(parameters.GetBool(SpiGet.MenuAnimation));
    }

    [Fact]
    public void Already_compliant_change_is_skipped_without_journal_entry()
    {
        var (engine, registry, _, _, _, notifier) = Setup();
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 0);

        var result = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.Equal(ChangeStatus.Skipped, result.Changes.Single().Status);
        Assert.Empty(result.Session!.Entries);
        Assert.Equal(0, notifier.Broadcasts);
    }

    [Fact]
    public void User_settings_are_skipped_when_elevated_as_another_user()
    {
        var (engine, registry, _, _, _, _) = Setup(anotherUser: true);

        var result = engine.Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        Assert.Equal(ChangeStatus.Skipped, result.Changes.Single(c => c.ChangeId == "M06.taskview").Status);
        Assert.Equal(ChangeStatus.Applied, result.Changes.Single(c => c.ChangeId == "M06.widgets").Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Unsafe_journal_blocks_before_any_write()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        journal.Unsafe = true;

        var result = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.True(result.Blocked);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Second_change_on_same_setting_is_skipped()
    {
        var (engine, _, _, _, _, _) = Setup();

        var result = engine.Apply(
            [Change("a", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("b", new SettingWrite(TaskView, SettingValue.Dword(2)))],
            new ApplyOptions());

        Assert.Equal(ChangeStatus.Applied, result.Changes[0].Status);
        Assert.Equal(ChangeStatus.Skipped, result.Changes[1].Status);
    }

    [Fact]
    public void Original_value_type_is_restored_exactly()
    {
        var (engine, registry, _, _, _, _) = Setup();
        var key = SettingKey.Registry("HKCU", @"Environment", "Chemin");
        registry.SetTyped(RegistryHive.CurrentUser, "Environment", "Chemin", "%USERPROFILE%\\x", RegistryValueKind.ExpandString);

        var applied = engine.Apply([Change("x", new SettingWrite(key, SettingValue.Text("y")))], new ApplyOptions { CreateRestorePoint = false });
        engine.Revert(applied.Session!.Id);

        Assert.Equal(RegistryValueKind.ExpandString, registry.GetValueKind(RegistryHive.CurrentUser, "Environment", "Chemin"));
        Assert.Equal("%USERPROFILE%\\x", registry.GetValue(RegistryHive.CurrentUser, "Environment", "Chemin"));
    }
}
