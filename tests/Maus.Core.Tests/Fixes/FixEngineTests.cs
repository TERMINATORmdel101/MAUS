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
    public void Force_after_a_plain_revert_still_restores_the_value()
    {
        var (engine, registry, _, _, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 5);
        engine.Revert(applied.Session!.Id);

        var forced = engine.Revert(applied.Session!.Id, force: true);

        Assert.Equal(RevertStatus.Reverted, forced.Entries.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
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
    public void Unknown_management_blocks_every_change_and_says_what_to_do()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var audit = TestContext.Create(registry, elevated: true, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, ManagementUnknown = true });
        var journal = new InMemoryJournalStore();
        var engine = new FixEngine(TestFixContext.Create(audit, registry, new FakeSystemParameters(), journal, new FakeSystemRestore(), new FakeNotifier(), false));

        var result = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.True(result.Blocked);
        Assert.Contains("Relancez l'audit", result.BlockedReason, StringComparison.Ordinal);
        Assert.Empty(registry.Writes);
        Assert.Empty(journal.List());
    }

    [Fact]
    public void A_normal_pc_read_correctly_is_not_blocked()
    {
        var (engine, _, _, _, _, _) = Setup();

        Assert.Null(engine.GetBlockingReason());
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
    public void Second_request_without_a_point_never_creates_one()
    {
        var (engine, _, _, _, restore, _) = Setup();
        restore.SilentlySkip = true;
        PlannedChange[] changes = [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))];

        Assert.True(engine.Apply(changes, new ApplyOptions()).Blocked);
        var callsAfterRefusal = restore.CreateCalls;
        Assert.True(callsAfterRefusal > 0);

        var accepted = engine.Apply(changes, new ApplyOptions { CreateRestorePoint = false, ProceedWithoutRestorePoint = true });

        Assert.False(accepted.Blocked);
        Assert.Equal(ChangeStatus.Applied, accepted.Changes.Single().Status);
        Assert.Equal(callsAfterRefusal, restore.CreateCalls);
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

    [Theory]
    [InlineData("io")]
    [InlineData("argument")]
    public void Unexpected_windows_error_rolls_the_change_back_instead_of_escaping(string kind)
    {
        var (engine, registry, _, journal, _, _) = Setup();
        Exception failure = kind == "io"
            ? new IOException("Opération non conforme sur une clé de Registre marquée pour suppression.")
            : new ArgumentException("Valeur trop longue.");
        registry.FailWrite(RegistryHive.LocalMachine, Policy, failure);

        var result = engine.Apply(
            [Change("M06.pair", new SettingWrite(TaskView, SettingValue.Dword(0)), new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        Assert.Equal(ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.All(journal.Load(result.Session!.Id)!.Entries, e => Assert.Equal(EntryState.Failed, e.State));
    }

    [Fact]
    public void Failed_rollback_stays_visible_and_undo_retries_it()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        // La première écriture passe, puis Windows refuse tout sous cette clé : le retour arrière échouera aussi.
        registry.AfterWrite = (hive, path) =>
        {
            if (hive == RegistryHive.CurrentUser && path == Advanced)
            {
                registry.DenyWrite(RegistryHive.CurrentUser, Advanced);
            }
        };
        registry.FailWrite(RegistryHive.LocalMachine, Policy, new IOException("clé marquée pour suppression"));

        var result = engine.Apply(
            [Change("M06.pair", new SettingWrite(TaskView, SettingValue.Dword(0)), new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        // Jamais rangée avec les échecs ordinaires : la modification est encore en place et la séance garde « Annuler ».
        Assert.Equal(ChangeStatus.Failed, result.Changes.Single().Status);
        Assert.True(result.Changes.Single().Message.Contains("retour arrière a échoué", StringComparison.Ordinal), result.Changes.Single().Message);
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        var session = journal.Load(result.Session!.Id)!;
        Assert.True(session.CanRevert);
        Assert.True(session.HasFailedRollback);
        Assert.Equal(EntryState.RollbackFailed, session.Entries.Single(e => e.Key.Identity == TaskView.Identity).State);
        Assert.Equal(EntryState.Failed, session.Entries.Single(e => e.Key.Identity == Widgets.Identity).State);

        // Windows accepte de nouveau : « Annuler » remet la valeur d'origine.
        registry.AfterWrite = null;
        registry.AllowWrite(RegistryHive.CurrentUser, Advanced);
        var revert = engine.Revert(result.Session.Id);

        Assert.Contains(revert.Entries, o => o.Status == RevertStatus.Reverted);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.False(journal.Load(result.Session.Id)!.CanRevert);
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

    private sealed class UnsafeJournal : IJournalStore
    {
        public void Save(JournalSession session) => throw new JournalUnsafeException("dossier non sûr");

        public JournalSession? Load(string id) => throw new JournalUnsafeException("dossier non sûr");

        public IReadOnlyList<JournalSession> List() => throw new JournalUnsafeException("dossier non sûr");
    }

    [Fact]
    public void Undoing_is_refused_with_the_reason_when_the_journal_folder_is_not_safe()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 0);
        var parameters = new FakeSystemParameters();
        var context = TestFixContext.Create(TestContext.Create(registry, parameters: parameters, elevated: true), registry, parameters, new UnsafeJournal(), new FakeSystemRestore(), new FakeNotifier(), false);

        var result = new FixEngine(context).Revert("20261102-100000-abcdef");

        Assert.Equal("dossier non sûr", result.Error);
        Assert.Empty(result.Entries);
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
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

    [Fact]
    public void Interrupted_write_stays_revertible()
    {
        // MAUS fermé (ou coupure) juste après l'écriture : le journal est resté à « en attente ».
        var (engine, registry, _, journal, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var saved = journal.Load(applied.Session!.Id)!;
        saved.Entries.Single().State = EntryState.Pending;
        journal.Save(saved);
        Assert.True(journal.Load(applied.Session.Id)!.CanRevert);

        var reverted = engine.Revert(applied.Session.Id);

        Assert.Equal(RevertStatus.Reverted, reverted.Entries.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.False(reverted.Session!.CanRevert);
    }

    [Fact]
    public void Interrupted_before_the_write_has_nothing_to_undo()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var saved = journal.Load(applied.Session!.Id)!;
        saved.Entries.Single().State = EntryState.Pending;
        journal.Save(saved);
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);

        var reverted = engine.Revert(applied.Session.Id);

        Assert.Equal(RevertStatus.Skipped, reverted.Entries.Single().Status);
        Assert.Equal(EntryState.Failed, reverted.Session!.Entries.Single().State);
        Assert.False(reverted.Session.CanRevert);
    }

    [Fact]
    public void Applied_value_already_back_to_its_origin_counts_as_reverted()
    {
        var (engine, registry, _, _, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        registry.Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);

        var reverted = engine.Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.Skipped, reverted.Entries.Single().Status);
        Assert.True(reverted.Completed);
        Assert.Equal(EntryState.Reverted, reverted.Session!.Entries.Single().State);
        Assert.False(reverted.Session.CanRevert);
    }

    [Fact]
    public void Value_of_another_profile_is_not_a_completed_revert()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var audit = TestContext.Create(registry, elevated: true, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var journal = new InMemoryJournalStore();
        var applied = new FixEngine(TestFixContext.Create(audit, registry, journal: journal))
            .Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        var reverted = new FixEngine(TestFixContext.Create(audit, registry, journal: journal, elevatedAsAnotherUser: true)).Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.WrongAccount, reverted.Entries.Single().Status);
        Assert.False(reverted.Completed);
        Assert.True(reverted.Session!.CanRevert);
    }

    [Fact]
    public void Key_created_by_the_session_goes_away_whatever_the_undo_order()
    {
        const string Cloud = @"Software\Policies\Microsoft\Windows\CloudContent";
        var (engine, registry, _, _, _, _) = Setup();
        var applied = engine.Apply(
            [
                Change("a", new SettingWrite(SettingKey.Registry("HKCU", Cloud, "Un"), SettingValue.Dword(1))),
                Change("b", new SettingWrite(SettingKey.Registry("HKCU", Cloud, "Deux"), SettingValue.Dword(1))),
            ],
            new ApplyOptions { CreateRestorePoint = false });

        Assert.True(engine.Revert(applied.Session!.Id, changeId: "a").Completed);
        Assert.True(registry.KeyExists(RegistryHive.CurrentUser, Cloud));
        Assert.True(engine.Revert(applied.Session.Id, changeId: "b").Completed);

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Cloud));
    }

    [Fact]
    public void Journal_failure_at_the_end_of_a_revert_is_reported_and_windows_is_told()
    {
        var (engine, registry, _, journal, _, notifier) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var broadcasts = notifier.Broadcasts;
        journal.Unsafe = true;

        var reverted = engine.Revert(applied.Session!.Id);

        Assert.NotNull(reverted.Error);
        Assert.False(reverted.Completed);
        Assert.Equal(RevertStatus.Reverted, reverted.Entries.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.Equal(broadcasts + 1, notifier.Broadcasts);
    }

    public static TheoryData<Exception> JournalFailures => new()
    {
        new JournalUnsafeException("dossier non sûr"),
        new IOException("disque plein"),
        new UnauthorizedAccessException("refusé"),
    };

    [Theory]
    [MemberData(nameof(JournalFailures))]
    public void Journal_that_fails_midway_undoes_what_was_written_whatever_the_error(Exception failure)
    {
        var (engine, registry, _, journal, _, _) = Setup();
        // 1er et 2e enregistrements : ceux de la 1re correction ; le 3e est celui de la 2e, avant son écriture.
        journal.FailsFrom = (3, failure);

        var result = engine.Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        Assert.True(result.Blocked);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, Policy, "AllowNewsAndInterests"));
        Assert.DoesNotContain(result.Changes, c => c.Status == ChangeStatus.Applied);
        Assert.Equal(0, result.AppliedCount);
    }

    [Fact]
    public void Unreadable_original_value_skips_only_that_change()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        registry.Deny(RegistryHive.CurrentUser, Advanced);

        var result = engine.Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        var skipped = result.Changes.Single(c => c.ChangeId == "M06.taskview");
        Assert.Equal(ChangeStatus.Skipped, skipped.Status);
        Assert.Contains("illisible", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(registry.Writes, w => w.Contains("ShowTaskViewButton", StringComparison.Ordinal));
        Assert.Equal(ChangeStatus.Applied, result.Changes.Single(c => c.ChangeId == "M06.widgets").Status);
        Assert.DoesNotContain(journal.Load(result.Session!.Id)!.Entries, e => e.ChangeId == "M06.taskview");
    }

    [Fact]
    public void Undo_refused_by_windows_is_reported_and_can_be_retried()
    {
        var (engine, registry, _, journal, _, notifier) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var broadcasts = notifier.Broadcasts;
        registry.DenyWrite(RegistryHive.CurrentUser, Advanced);

        var refused = engine.Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.Failed, refused.Entries.Single().Status);
        Assert.False(refused.Completed);
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        var saved = journal.Load(applied.Session.Id)!;
        Assert.Equal(EntryState.Applied, saved.Entries.Single().State);
        Assert.NotNull(saved.Entries.Single().Error);
        Assert.True(saved.CanRevert);
        Assert.Null(saved.RevertedAt);
        Assert.Equal(broadcasts, notifier.Broadcasts);

        registry.AllowWrite(RegistryHive.CurrentUser, Advanced);
        var retried = engine.Revert(applied.Session.Id);

        Assert.Equal(RevertStatus.Reverted, retried.Entries.Single().Status);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Undo_not_kept_by_windows_is_not_a_success()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        registry.IgnoreWrites(RegistryHive.CurrentUser, Advanced);

        var result = engine.Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.Failed, result.Entries.Single().Status);
        Assert.Contains("n'a pas pu être relue", result.Entries.Single().Message, StringComparison.Ordinal);
        Assert.True(journal.Load(applied.Session.Id)!.CanRevert);
    }

    [Fact]
    public void Undo_with_an_unreadable_value_writes_nothing()
    {
        var (engine, registry, _, journal, _, _) = Setup();
        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var writes = registry.Writes.Count;
        registry.Deny(RegistryHive.CurrentUser, Advanced);

        var result = engine.Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.Failed, result.Entries.Single().Status);
        Assert.Contains("illisible", result.Entries.Single().Message, StringComparison.Ordinal);
        Assert.Equal(writes, registry.Writes.Count);
        Assert.True(journal.Load(applied.Session.Id)!.CanRevert);
    }

    [Fact]
    public void Undo_without_administrator_rights_writes_nothing()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var journal = new InMemoryJournalStore();
        var admin = TestContext.Create(registry, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var applied = new FixEngine(TestFixContext.Create(admin, registry, journal: journal))
            .Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var writes = registry.Writes.Count;
        var limited = TestContext.Create(registry, elevated: false, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });

        var result = new FixEngine(TestFixContext.Create(limited, registry, journal: journal)).Revert(applied.Session!.Id);

        Assert.NotNull(result.Error);
        Assert.False(result.Completed);
        Assert.Equal(writes, registry.Writes.Count);
    }

    [Fact]
    public void Undo_elevated_as_another_user_keeps_the_profile_values_for_later()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var audit = TestContext.Create(registry, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var journal = new InMemoryJournalStore();
        var applied = new FixEngine(TestFixContext.Create(audit, registry, journal: journal)).Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        var result = new FixEngine(TestFixContext.Create(audit, registry, journal: journal, elevatedAsAnotherUser: true)).Revert(applied.Session!.Id);

        Assert.Equal(RevertStatus.WrongAccount, result.Entries.Single(e => e.ChangeId == "M06.taskview").Status);
        Assert.Equal(RevertStatus.Reverted, result.Entries.Single(e => e.ChangeId == "M06.widgets").Status);
        Assert.True(result.Session!.CanRevert);
    }

    private const string Alice = "S-1-5-21-1-1001";
    private const string Bob = "S-1-5-21-1-1002";

    private static (FixEngine Alice, FixEngine Bob, FakeRegistry Registry, InMemoryJournalStore Journal) TwoAccounts()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var audit = TestContext.Create(registry, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var journal = new InMemoryJournalStore();
        return (new FixEngine(TestFixContext.Create(audit, registry, journal: journal, userSid: Alice)),
            new FixEngine(TestFixContext.Create(audit, registry, journal: journal, userSid: Bob)), registry, journal);
    }

    [Fact]
    public void A_session_notes_the_account_that_made_it()
    {
        var (alice, _, _, journal) = TwoAccounts();

        var applied = alice.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        var saved = journal.Load(applied.Session!.Id)!;
        Assert.Equal(Alice, saved.UserSid);
        Assert.False(saved.IsFromAnotherAccount(Alice));
        Assert.True(saved.IsFromAnotherAccount(Bob));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Another_account_cannot_undo_a_profile_value_even_by_force(bool force)
    {
        var (alice, bob, registry, journal) = TwoAccounts();
        var applied = alice.Apply(
            [Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0))), Change("M06.widgets", new SettingWrite(Widgets, SettingValue.Dword(0)))],
            new ApplyOptions());

        var result = bob.Revert(applied.Session!.Id, force);

        // La valeur du profil n'est ni touchée ni marquée ; le réglage commun au PC est remis.
        var profile = result.Entries.Single(e => e.ChangeId == "M06.taskview");
        Assert.Equal(RevertStatus.WrongAccount, profile.Status);
        Assert.Contains("autre compte", profile.Message, StringComparison.Ordinal);
        Assert.Equal(RevertStatus.Reverted, result.Entries.Single(e => e.ChangeId == "M06.widgets").Status);
        Assert.False(result.Completed);
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        var after = journal.Load(applied.Session.Id)!;
        Assert.Equal(EntryState.Applied, after.Entries.Single(e => e.ChangeId == "M06.taskview").State);
        Assert.Null(after.RevertedAt);
        Assert.True(after.CanRevert);
        Assert.False(after.CanRevertAs(Bob));
        Assert.True(after.CanRevertAs(Alice));

        // Le compte de la séance, lui, peut encore tout finir.
        Assert.True(alice.Revert(applied.Session.Id).Completed);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.False(journal.Load(applied.Session.Id)!.CanRevert);
    }

    [Fact]
    public void A_session_without_an_account_is_undone_as_before()
    {
        var (alice, bob, registry, journal) = TwoAccounts();
        var applied = alice.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());
        var old = journal.Load(applied.Session!.Id)!;
        var written = new JournalSession { Id = old.Id, CreatedAt = old.CreatedAt, WindowsBuild = old.WindowsBuild, Entries = old.Entries };
        journal.Save(written);

        Assert.False(written.IsFromAnotherAccount(Bob));
        Assert.True(bob.Revert(old.Id).Completed);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
    }

    [Fact]
    public void Journal_dates_use_the_real_clock_not_the_audit_time()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 1);
        var audit = TestContext.Create(registry, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var later = audit.Now.AddHours(9);
        var engine = new FixEngine(TestFixContext.Create(audit, registry, clock: () => later));

        var applied = engine.Apply([Change("M06.taskview", new SettingWrite(TaskView, SettingValue.Dword(0)))], new ApplyOptions());

        Assert.Equal(later, applied.Session!.CreatedAt);
        Assert.Equal(later, applied.Session.Entries.Single().AppliedAt);
    }
}
