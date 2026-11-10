using Maus.Cli;
using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Microsoft.Win32;

namespace Maus.Core.Tests.Cli;

/// <summary>
/// La ligne de commande applique et annule de vraies corrections : ses codes de sortie sont son interface pour un script, et
/// sa confirmation est la seule protection de celui qui la lance.
/// </summary>
/// <summary>Les tests qui remplacent l'entrée de la console ne tournent jamais en même temps : elle est commune à tout le processus.</summary>
[CollectionDefinition(nameof(ConsoleInput), DisableParallelization = true)]
public sealed class ConsoleInput;

[Collection(nameof(ConsoleInput))]
public sealed class FixCommandsTests
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "ShowTaskViewButton";

    private static PlannedChange Change() => new()
    {
        Id = "M06.taskview",
        ModuleId = "M06",
        Title = "Masquer la vue des tâches",
        Description = "Explication",
        Writes = [new SettingWrite(SettingKey.Registry("HKCU", Advanced, ValueName), SettingValue.Dword(0))],
    };

    private static (FixContext Context, FakeRegistry Registry, FakeSystemRestore Restore) Setup(bool elevated = true, bool managed = false)
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, ValueName, 1);
        var audit = TestContext.Create(registry, elevated: elevated, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = managed });
        var restore = new FakeSystemRestore();
        return (TestFixContext.Create(audit, registry, restore: restore), registry, restore);
    }

    /// <summary>Répond à la question posée sur la console, le temps de l'appel.</summary>
    private static T WithAnswer<T>(string? answer, Func<T> run)
    {
        var input = Console.In;
        Console.SetIn(new StringReader(answer is null ? string.Empty : answer + Environment.NewLine));
        try
        {
            return run();
        }
        finally
        {
            Console.SetIn(input);
        }
    }

    [Fact]
    public void A_change_already_in_place_is_not_an_error_for_a_script()
    {
        var (context, registry, _) = Setup();
        registry.Set(RegistryHive.CurrentUser, Advanced, ValueName, 0);

        var (code, result) = FixCommands.Apply(context, [Change()], new ApplyOptions { CreateRestorePoint = false }, assumeYes: true, new StringWriter());

        Assert.Equal(ChangeStatus.Skipped, result!.Changes.Single().Status);
        Assert.False(result.Changes.Single().NotAllowed);
        Assert.Equal(0, code);
    }

    [Fact]
    public void A_refused_change_gives_the_error_code()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Advanced, ValueName, 1);
        var audit = TestContext.Create(registry, elevated: true, hardware: new HardwareProfile { FormFactor = FormFactor.Desktop });
        var refused = TestFixContext.Create(audit, registry, elevatedAsAnotherUser: true);

        var (code, result) = FixCommands.Apply(refused, [Change()], new ApplyOptions { CreateRestorePoint = false }, assumeYes: true, new StringWriter());

        Assert.Equal(ChangeStatus.Skipped, result!.Changes.Single().Status);
        Assert.True(result.Changes.Single().NotAllowed);
        Assert.Equal(2, code);
    }

    [Fact]
    public void Undoing_without_rights_is_refused_before_the_question()
    {
        var (context, _, _) = Setup(elevated: false);
        var output = new StringWriter();

        // Aucune réponse prévue : la question ne doit pas être posée.
        Assert.Equal(3, WithAnswer(null, () => FixCommands.Revert(context, "seance", null, force: false, assumeYes: false, output)));
        Assert.Contains("droits administrateur", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Annulé", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_shows_each_change_with_its_warning_its_risk_and_what_it_writes()
    {
        var output = new StringWriter();

        FixCommands.PrintPlan([Change() with { Recommended = true, Warning = "Lisez ceci", Risk = "Risque connu", Effect = ChangeEffect.Restart }], output);

        var text = output.ToString();
        Assert.Contains("M06.taskview", text, StringComparison.Ordinal);
        Assert.Contains("[recommandé]", text, StringComparison.Ordinal);
        Assert.Contains("ATTENTION : Lisez ceci", text, StringComparison.Ordinal);
        Assert.Contains("Risque : Risque connu", text, StringComparison.Ordinal);
        Assert.Contains(ValueName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_masks_the_profile_and_the_sid_but_never_the_identifier_that_apply_expects()
    {
        const string Sid = "S-1-5-21-111111111-222222222-333333333-1001";
        const string Id = "M12.task-s-1-5-21-111111111";
        var output = new StringWriter();
        var change = Change() with
        {
            Id = Id,
            Title = $"Désactiver la tâche : Mise à jour {Sid}",
            Description = @"Lance C:\Users\Alex\AppData\outil.exe",
            Writes = [new SettingWrite(SettingKey.ScheduledTask(@"\", $"Mise à jour {Sid}"), null)],
        };

        FixCommands.PrintPlan([change], output, new PrivacyFilter("Alex", "PC-1", @"C:\Users\Alex"));

        var text = output.ToString();
        Assert.Contains(Id, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Sid, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex", text, StringComparison.Ordinal);
        Assert.Contains(@"%USERPROFILE%\AppData\outil.exe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_tells_apart_two_changes_with_the_same_title()
    {
        const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var output = new StringWriter();

        FixCommands.PrintPlan(
            [
                Change() with { Id = "M12.discord", Title = "Discord", Writes = [new SettingWrite(SettingKey.Registry("HKCU", Path, "Discord"), null)] },
                Change() with { Id = "M12.canary", Title = "Discord", Writes = [new SettingWrite(SettingKey.Registry("HKCU", Path, "DiscordCanary"), null)] },
                Change() with { Id = "M12.seul", Title = "Steam", Writes = [new SettingWrite(SettingKey.Registry("HKCU", Path, "Steam"), null)] },
            ],
            output);

        var text = output.ToString();
        Assert.Contains("Discord (Discord)", text, StringComparison.Ordinal);
        Assert.Contains("Discord (DiscordCanary)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Steam (", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_falls_back_on_the_identifier_when_the_written_names_do_not_differ()
    {
        var output = new StringWriter();

        FixCommands.PrintPlan([Change() with { Id = "M06.a" }, Change() with { Id = "M06.b" }], output);

        Assert.Contains($"{Change().Title} (M06.a)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{Change().Title} (M06.b)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Advanced_changes_are_labelled_as_such()
    {
        var output = new StringWriter();

        FixCommands.PrintPlan([Change() with { Recommended = true, Advanced = true }], output);

        Assert.Contains("[avancé]", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_plan_says_so()
    {
        var output = new StringWriter();

        FixCommands.PrintPlan([], output);

        Assert.Contains("Aucune correction", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Without_rights_or_on_a_managed_pc_nothing_is_written(bool elevated, bool managed)
    {
        var (context, registry, _) = Setup(elevated, managed);
        var output = new StringWriter();

        var (code, result) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, output);

        Assert.Equal(3, code);
        Assert.Null(result);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
    }

    [Fact]
    public void An_empty_selection_writes_nothing_and_is_an_error_for_a_script()
    {
        var (context, _, _) = Setup();

        var (code, result) = FixCommands.Apply(context, [], new ApplyOptions(), assumeYes: true, new StringWriter());

        Assert.Equal(1, code);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("non")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_yes_writes_nothing(string? answer)
    {
        var (context, registry, _) = Setup();
        var output = new StringWriter();

        var (code, result) = WithAnswer(answer, () => FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: false, output));

        Assert.Equal(1, code);
        Assert.Null(result);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
        Assert.Contains("rien n'a été modifié", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Yes_writes_and_gives_the_command_to_undo()
    {
        var (context, registry, _) = Setup();
        var output = new StringWriter();

        var (code, result) = WithAnswer("oui", () => FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: false, output));

        Assert.Equal(0, code);
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
        Assert.Contains("maus --revert " + result!.Session!.Id, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_system_protection_stops_everything_and_names_both_ways_out()
    {
        var (context, registry, restore) = Setup();
        restore.Protection = ProtectionState.Disabled;
        var output = new StringWriter();

        var (code, result) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, output);

        Assert.Equal(3, code);
        Assert.True(result!.Blocked);
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
        Assert.Contains("--enable-protection", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--without-restore-point", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_change_is_an_error_for_a_script()
    {
        var (context, registry, _) = Setup();
        registry.DenyWrite(RegistryHive.CurrentUser, Advanced);

        var (code, _) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, new StringWriter());

        Assert.Equal(2, code);
    }

    [Fact]
    public void Revert_of_an_unknown_session_is_refused()
    {
        var (context, _, _) = Setup();

        Assert.Equal(3, FixCommands.Revert(context, "inconnue", null, force: false, assumeYes: true, new StringWriter()));
    }

    [Fact]
    public void Revert_puts_the_original_value_back_only_after_yes()
    {
        var (context, registry, _) = Setup();
        var (_, result) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, new StringWriter());
        var session = result!.Session!.Id;

        Assert.Equal(1, WithAnswer("n", () => FixCommands.Revert(context, session, null, force: false, assumeYes: false, new StringWriter())));
        Assert.Equal(0, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));

        Assert.Equal(0, WithAnswer("o", () => FixCommands.Revert(context, session, null, force: false, assumeYes: false, new StringWriter())));
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
    }

    [Fact]
    public void A_value_changed_since_is_left_alone_unless_forced()
    {
        var (context, registry, _) = Setup();
        var (_, result) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, new StringWriter());
        var session = result!.Session!.Id;
        registry.Set(RegistryHive.CurrentUser, Advanced, ValueName, 5);
        var output = new StringWriter();

        FixCommands.Revert(context, session, null, force: false, assumeYes: true, output);

        Assert.Equal(5, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
        Assert.Contains("--force", output.ToString(), StringComparison.Ordinal);

        FixCommands.Revert(context, session, null, force: true, assumeYes: true, new StringWriter());

        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, ValueName));
    }

    [Fact]
    public void Journal_lists_sessions_and_says_when_it_is_empty()
    {
        var (context, _, _) = Setup();
        var empty = new StringWriter();
        Assert.Equal(0, FixCommands.PrintJournal(context.Journal, empty));
        Assert.Contains("Journal vide", empty.ToString(), StringComparison.Ordinal);

        var (_, result) = FixCommands.Apply(context, [Change()], new ApplyOptions(), assumeYes: true, new StringWriter());
        var output = new StringWriter();

        Assert.Equal(0, FixCommands.PrintJournal(context.Journal, output));
        Assert.Contains(result!.Session!.Id, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("M06.taskview", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_says_when_a_change_had_no_effect()
    {
        var applied = new ChangeOutcome("M06.taskview", "Titre", ChangeStatus.Applied, "fait");
        var failed = new ChangeOutcome("M06.autre", "Autre", ChangeStatus.Failed, "échec");
        var output = new StringWriter();

        FixCommands.PrintVerification([new(applied, EffectCheck.NoEffect, "toujours pareil"), new(failed, EffectCheck.NoEffect, "pas appliquée")], output);

        Assert.Contains("[SANS EFFET] Titre : toujours pareil", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pas appliquée", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("o", true)]
    [InlineData("oui", true)]
    [InlineData("OUI", true)]
    [InlineData(" oui ", true)]
    [InlineData("y", true)]
    [InlineData("yes", true)]
    [InlineData("s", true)]
    [InlineData("si", true)]
    [InlineData("sí", true)]
    [InlineData("n", false)]
    [InlineData("non", false)]
    [InlineData("ok", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_clear_yes_confirms(string? answer, bool confirmed) =>
        Assert.Equal(confirmed, WithAnswer(answer, () => FixCommands.Confirm("Question ? ")));
}
