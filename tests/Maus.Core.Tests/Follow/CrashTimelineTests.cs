using Maus.Core.Fixes;
using Maus.Core.Follow;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Modules.M03Updates;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Follow;

public class CrashTimelineTests
{
    private const string SystemLog = "System";

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));

    private static DateTime DaysAgo(double days) => Now.LocalDateTime.AddDays(-days);

    private static readonly CrashTimelineJournal EmptyJournal = new([]);

    private static CrashTimeline Read(IEventLogReader? logs = null, FakeCim? cim = null, CrashTimelineJournal? journal = null) =>
        CrashTimelineReader.Read(logs ?? new FakeEventLogs(), cim ?? new FakeCim(), journal ?? EmptyJournal, Now);

    private static FakeEventLogs Crash(FakeEventLogs logs, DateTime when, string? bugcheck = null, string? powerButton = null)
    {
        var data = new Dictionary<string, string> { ["BugcheckCode"] = bugcheck ?? "0" };
        if (powerButton is not null)
        {
            data["PowerButtonTimestamp"] = powerButton;
        }

        return logs.Add(SystemLog, WindowsHealthModule.KernelPowerProvider, 41, when, data);
    }

    private static FakeEventLogs Update(FakeEventLogs logs, DateTime when, string? title) =>
        logs.Add(SystemLog, CrashTimelineReader.UpdateProvider, 19, when, title is null ? [] : new Dictionary<string, string> { ["updateTitle"] = title });

    private static Dictionary<string, object?> Qfe(string id, DateTime installedOn) =>
        new() { ["HotFixID"] = id, ["InstalledOn"] = installedOn.ToString("M/d/yyyy", System.Globalization.CultureInfo.InvariantCulture) };

    private sealed class FailingProvider(IEventLogReader inner, string provider) : IEventLogReader
    {
        public IReadOnlyList<EventRecordInfo> Query(string logName, string? asked, IReadOnlyCollection<int> eventIds, DateTime since, int maxEvents = 200, bool includeMessage = false) =>
            asked == provider
                ? throw new DataSourceUnavailableException("Journal illisible")
                : inner.Query(logName, asked, eventIds, since, maxEvents, includeMessage);
    }

    // ----- Rien à montrer -----

    [Fact]
    public void An_empty_pc_says_none_found_for_every_section()
    {
        var timeline = Read();

        Assert.Empty(timeline.Lines);
        Assert.Equal(5, timeline.Sections.Count);
        Assert.All(timeline.Sections, s => Assert.EndsWith("aucun trouvé", s.Text, StringComparison.Ordinal));
        Assert.StartsWith("Rien trouvé sur 30 jours", timeline.Summary, StringComparison.Ordinal);
        Assert.Contains("aucun trouvé", timeline.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_denied_log_is_said_not_read_and_never_passes_for_empty()
    {
        var timeline = Read(new FakeEventLogs().Deny(SystemLog));

        Assert.Empty(timeline.Lines);
        Assert.Contains(timeline.Sections, s => s.Text == "Écrans bleus, bouton et arrêts non expliqués : non lu sans droits administrateur");
        Assert.Contains(timeline.Sections, s => s.Text == "Arrêts et redémarrages demandés : non lu sans droits administrateur");
        Assert.Contains(timeline.Sections, s => s.Text == "Mises à jour installées : non lu sans droits administrateur");
        Assert.Contains(timeline.Sections, s => s.Text == "Pilotes installés par Windows Update : non lu sans droits administrateur");
        Assert.StartsWith("Rien trouvé dans ce qui a pu être lu", timeline.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("aucun trouvé", timeline.Sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partly_unreadable_source_says_what_it_found_anyway()
    {
        var logs = Crash(new FakeEventLogs(), DaysAgo(1), "59");

        var timeline = Read(new FailingProvider(logs, CrashTimelineReader.ShutdownProvider));

        Assert.Single(timeline.Lines);
        Assert.Equal(CrashTimelineState.NotRead, timeline.Sections[0].State);
        Assert.Contains("lecture incomplète, 1 trouvé", timeline.Sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("incomplète", timeline.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Failing_hotfix_reading_leaves_updates_not_read_but_drivers_read()
    {
        var cim = new FakeCim().Throw(WindowsUpdateModule.QfeQuery, new DataSourceUnavailableException("WMI"));

        var timeline = Read(cim: cim);

        Assert.Equal("Mises à jour installées : non lu", timeline.Sections[2].Text);
        Assert.Equal(CrashTimelineState.None, timeline.Sections[3].State);
    }

    // ----- Plantages -----

    [Fact]
    public void One_crash_leaves_three_traces_and_makes_one_line()
    {
        var at = DaysAgo(2);
        var logs = Crash(new FakeEventLogs(), at, "10")
            .Add(SystemLog, WindowsHealthModule.WerProvider, 1001, at.AddMinutes(2), new Dictionary<string, string> { ["param1"] = "0x0000000a (0x1, 0x2, 0x3, 0x4)", ["param2"] = @"C:\Windows\Minidump\a.dmp" })
            .Add(SystemLog, CrashTimelineReader.ShutdownProvider, 6008, at.AddSeconds(-5));

        var line = Assert.Single(Read(logs).Lines);

        Assert.Equal(CrashTimelineKind.BlueScreen, line.Kind);
        Assert.Equal(at.AddSeconds(-5), line.When);
        Assert.Contains("0x0000000A", line.What, StringComparison.Ordinal);
        Assert.DoesNotContain("Minidump", line.What, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_close_crashes_stay_two_lines()
    {
        var logs = Crash(Crash(new FakeEventLogs(), DaysAgo(2), "59"), DaysAgo(2).AddMinutes(3), "59");

        Assert.Equal(2, Read(logs).Lines.Count);
    }

    [Fact]
    public void A_shutdown_without_a_code_is_not_called_a_blue_screen()
    {
        var logs = Crash(new FakeEventLogs(), DaysAgo(3));
        Crash(logs, DaysAgo(2), powerButton: "133000000000000000");
        logs.Add(SystemLog, CrashTimelineReader.ShutdownProvider, 6008, DaysAgo(1));

        var lines = Read(logs).Lines;

        Assert.Equal([CrashTimelineKind.UnexpectedShutdown, CrashTimelineKind.PowerButton, CrashTimelineKind.UnexpectedShutdown], lines.Select(l => l.Kind));
        Assert.All(lines, l => Assert.True(l.IsCrash));
        Assert.Equal("Bouton d'alimentation", lines[1].KindText);
        Assert.Equal("Arrêt non expliqué", lines[0].KindText);
        Assert.Contains("n'était pas prévu", lines[0].What, StringComparison.Ordinal);
        Assert.Contains("appui long", lines[1].What, StringComparison.Ordinal);
        Assert.Contains("pas éteint proprement", lines[2].What, StringComparison.Ordinal);
    }

    // ----- Arrêts et redémarrages demandés (événement 1074) -----

    private static FakeEventLogs Request(FakeEventLogs logs, DateTime when, string program, string reason, string code, string type) =>
        logs.Add(SystemLog, "User32", 1074, when, new Dictionary<string, string>
        {
            ["param1"] = program,
            ["param2"] = "PC-DE-PAUL",
            ["param3"] = reason,
            ["param4"] = code,
            ["param5"] = type,
            ["param6"] = "Demandé par Paul Martin",
            ["param7"] = @"PC-DE-PAUL\Paul",
        });

    [Fact]
    public void A_request_says_the_program_the_reason_and_the_type_without_the_account_or_the_comment()
    {
        var logs = Request(new FakeEventLogs(), DaysAgo(1), @"C:\Program Files (x86)\Outil\maj.exe (PC-DE-PAUL)", "Autre (non planifié)", "0x0", "se mettre hors tension.");

        var timeline = Read(logs);
        var line = Assert.Single(timeline.Lines);

        Assert.Equal(CrashTimelineKind.Requested, line.Kind);
        Assert.False(line.IsCrash);
        Assert.Equal("Demandé par maj.exe, motif : Autre (non planifié) ; type : se mettre hors tension", line.What);
        Assert.Equal("Arrêts et redémarrages demandés : 1 trouvé", timeline.Sections[1].Text);
        Assert.DoesNotContain("Paul", timeline.ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x80020010", CrashTimelineKind.UpdateRestart)]
    [InlineData("0x80020003", CrashTimelineKind.UpdateRestart)]
    [InlineData("0x80020011", CrashTimelineKind.UpdateRestart)]
    [InlineData("0x20012", CrashTimelineKind.UpdateRestart)]
    [InlineData("0x80020018", CrashTimelineKind.UpdateRestart)]
    [InlineData("0x0", CrashTimelineKind.Requested)]
    [InlineData("0x500ff", CrashTimelineKind.Requested)]
    [InlineData("0x800000ff", CrashTimelineKind.Requested)]
    [InlineData("0x20004", CrashTimelineKind.Requested)]
    [InlineData("0x80040002", CrashTimelineKind.Requested)]
    [InlineData("0xC0020010", CrashTimelineKind.Requested)]
    [InlineData("n'importe quoi", CrashTimelineKind.Requested)]
    public void Only_the_reason_code_makes_a_request_an_update(string code, CrashTimelineKind expected)
    {
        var logs = Request(new FakeEventLogs(), DaysAgo(1), @"C:\Windows\servicing\TrustedInstaller.exe (PC)", "Update", code, "restart");

        Assert.Equal(expected, Assert.Single(Read(logs).Lines).Kind);
    }

    [Theory]
    [InlineData("(PC-DE-PAUL)")]
    [InlineData("  (PC-DE-PAUL)  ")]
    [InlineData(@"C:\Outil\ (PC-DE-PAUL)")]
    public void A_request_noted_with_only_the_pc_name_never_shows_it(string program)
    {
        var logs = Request(new FakeEventLogs(), DaysAgo(1), program, "Autre (non planifié)", "0x0", "redémarrer");

        var line = Assert.Single(Read(logs).Lines);

        Assert.StartsWith("Demandé par un programme non noté,", line.What, StringComparison.Ordinal);
        Assert.DoesNotContain("PC-DE-PAUL", line.What, StringComparison.Ordinal);
        Assert.Equal("un programme non noté", RecentEvents.Name(program));
    }

    [Fact]
    public void A_request_without_details_invents_nothing()
    {
        var logs = new FakeEventLogs().Add(SystemLog, "User32", 1074, DaysAgo(1));

        Assert.Equal("Demandé par un programme non noté, motif : non noté", Assert.Single(Read(logs).Lines).What);
    }

    [Fact]
    public void Requests_never_blame_or_say_without_asking()
    {
        var logs = Request(new FakeEventLogs(), DaysAgo(1), @"C:\Windows\servicing\TrustedInstaller.exe (PC)", "Service pack", "0x80020010", "restart");

        var text = Read(logs).ToText();

        Assert.Contains("Demandé par TrustedInstaller.exe, motif : Service pack", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sans vous demander", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coupable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CrashTimeline.RequestNote, text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_request_stays_out_and_a_refused_read_is_said()
    {
        var old = Request(new FakeEventLogs(), DaysAgo(40), "a.exe (PC)", "r", "0x0", "t");

        Assert.Empty(Read(old).Lines);

        var failed = Read(new FailingProvider(new FakeEventLogs(), "User32"));

        Assert.Equal(CrashTimelineState.NotRead, failed.Sections[1].State);
    }

    [Fact]
    public void Many_ordinary_requests_never_hide_an_older_crash_in_the_first_lines()
    {
        var logs = Crash(new FakeEventLogs(), DaysAgo(20), "59");
        for (var i = 0; i < 5; i++)
        {
            Request(logs, DaysAgo(1 + i), "a.exe (PC)", "r", "0x0", "t");
        }

        var first = Read(logs).FirstLines(3);

        Assert.Equal(3, first.Count);
        Assert.Equal(CrashTimelineKind.Requested, first[0].Kind);
        Assert.Equal(DaysAgo(1), first[0].When);
        Assert.Equal(DaysAgo(2), first[1].When);
        Assert.Equal(CrashTimelineKind.BlueScreen, first[2].Kind);
        Assert.Equal(6, Read(logs).FirstLines(int.MaxValue).Count);
    }

    // ----- Date de début du journal -----

    [Fact]
    public void A_short_log_never_claims_thirty_days()
    {
        var start = DaysAgo(4);
        var timeline = Read(new FakeEventLogs { Oldest = start });

        Assert.Equal(start, timeline.LogStart);
        Assert.StartsWith("Rien trouvé dans ce que Windows a gardé.", timeline.Summary, StringComparison.Ordinal);
        Assert.Contains("que depuis le " + start.ToString("d", Maus.Core.Localization.Texts.Culture), timeline.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("30 jours", timeline.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("effacé", timeline.Summary, StringComparison.Ordinal);
        Assert.Contains("que depuis le", timeline.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_enough_log_says_the_thirty_days_are_covered()
    {
        var start = DaysAgo(45);
        var timeline = Read(new FakeEventLogs { Oldest = start });

        Assert.StartsWith("Rien trouvé sur 30 jours.", timeline.Summary, StringComparison.Ordinal);
        Assert.Contains("remonte au " + start.ToString("d", Maus.Core.Localization.Texts.Culture), timeline.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_log_is_said_even_when_lines_were_found()
    {
        var logs = Crash(new FakeEventLogs { Oldest = DaysAgo(4) }, DaysAgo(2), "59");

        Assert.Contains("que depuis le", Read(logs).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unread_log_start_is_said_and_a_denied_log_does_not_stop_the_reading()
    {
        Assert.Contains("n'a pas pu être lue", Read().Summary, StringComparison.Ordinal);

        var denied = Read(new FakeEventLogs { Oldest = DaysAgo(45) }.Deny(SystemLog));

        Assert.Null(denied.LogStart);
    }

    [Fact]
    public void A_blue_screen_with_a_damaged_code_stays_a_blue_screen()
    {
        var logs = new FakeEventLogs().Add(SystemLog, WindowsHealthModule.WerProvider, 1001, DaysAgo(1), new Dictionary<string, string> { ["param1"] = "n'importe quoi" });

        var line = Assert.Single(Read(logs).Lines);

        Assert.Equal(CrashTimelineKind.BlueScreen, line.Kind);
        Assert.Contains("illisible", line.What, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reading_ceiling_becomes_at_least()
    {
        var logs = new FakeEventLogs();
        for (var i = 0; i < SystemLogQuery.MaxEvents; i++)
        {
            Crash(logs, DaysAgo(1).AddMinutes(-i));
        }

        Assert.StartsWith($"Écrans bleus, bouton et arrêts non expliqués : au moins {SystemLogQuery.MaxEvents} trouvés", Read(logs).Sections[0].Text, StringComparison.Ordinal);
    }

    // ----- Mises à jour et pilotes -----

    [Theory]
    [InlineData("INTEL - System - 10.1.16.7")]
    [InlineData("Dell Inc. - Monitor - 1.0.0.0")]
    [InlineData("Intel - System - 9/19/2017 12:00:00 AM - 11.7.0.1000")]
    [InlineData("NVIDIA Display Driver Update (32.0.16.1088)")]
    public void Driver_titles_seen_on_a_real_pc_are_drivers(string title)
    {
        var line = Assert.Single(Read(Update(new FakeEventLogs(), DaysAgo(1), title)).Lines);

        Assert.Equal(CrashTimelineKind.Driver, line.Kind);
        Assert.Equal(title, line.What);
    }

    [Theory]
    [InlineData("Mise à jour cumulative 2026-10 pour Windows 11 Version 25H2 (KB5127694)")]
    [InlineData("Outil de suppression de logiciels malveillants - 2026-10")]
    public void Other_titles_stay_updates(string title)
    {
        Assert.Equal(CrashTimelineKind.Update, Assert.Single(Read(Update(new FakeEventLogs(), DaysAgo(1), title)).Lines).Kind);
    }

    [Fact]
    public void Store_apps_and_defender_definitions_are_counted_not_listed()
    {
        var logs = new FakeEventLogs();
        Update(logs, DaysAgo(1), "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2");
        Update(logs, DaysAgo(2), "Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.83.0) – Canal actuel (large)");

        var timeline = Read(logs);

        Assert.Empty(timeline.Lines);
        Assert.Equal(CrashTimelineState.None, timeline.Sections[2].State);
        Assert.Contains("2 mises à jour d'applications du Microsoft Store", timeline.Sections[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_update_on_the_same_day_makes_one_line()
    {
        var logs = new FakeEventLogs();
        Update(logs, DaysAgo(1), "INTEL - System - 10.1.16.7");
        Update(logs, DaysAgo(1).AddMinutes(1), "INTEL - System - 10.1.16.7");
        Update(logs, DaysAgo(3), "INTEL - System - 10.1.16.7");

        var lines = Read(logs).Lines;

        Assert.Equal(2, lines.Count);
        Assert.Equal("INTEL - System - 10.1.16.7 (2 fois ce jour-là)", lines[0].What);
    }

    [Fact]
    public void An_update_without_a_title_is_still_shown()
    {
        Assert.Equal("sans nom", Assert.Single(Read(Update(new FakeEventLogs(), DaysAgo(1), null)).Lines).What);
    }

    [Fact]
    public void A_hotfix_makes_a_line_without_an_hour_unless_windows_update_already_named_it()
    {
        var logs = Update(new FakeEventLogs(), DaysAgo(1), "Mise à jour – 5127694 Ko");
        var cim = new FakeCim().Answer(WindowsUpdateModule.QfeQuery, Qfe("KB5127694", DaysAgo(1)), Qfe("KB5121794", DaysAgo(2)), Qfe("KB1", DaysAgo(40)));

        var lines = Read(logs, cim).Lines;

        Assert.Equal(2, lines.Count);
        var hotfix = lines[1];
        Assert.False(hotfix.HasTime);
        Assert.Contains("KB5121794", hotfix.What, StringComparison.Ordinal);
        Assert.Equal(DaysAgo(2).ToString("d", Maus.Core.Localization.Texts.Culture), hotfix.WhenText);
    }

    // ----- Corrections de MAUS -----

    private static JournalSession Session(DateTime created, params JournalEntry[] entries) => new()
    {
        Id = "s",
        CreatedAt = new DateTimeOffset(created),
        Entries = [.. entries],
    };

    private static JournalEntry Entry(string title, DateTime? applied, DateTime? reverted = null) => new()
    {
        ChangeId = title,
        ModuleId = "M06",
        ChangeTitle = title,
        Key = SettingKey.Registry("HKCU", @"Software\A", "a"),
        AppliedAt = applied is { } at ? new DateTimeOffset(at) : null,
        RevertedAt = reverted is { } back ? new DateTimeOffset(back) : null,
    };

    [Fact]
    public void Without_rights_the_journal_is_said_not_read()
    {
        var section = Read(journal: CrashTimelineJournal.WithoutRights).Sections[4];

        Assert.Equal(CrashTimelineState.NoRights, section.State);
        Assert.StartsWith("Corrections de MAUS : non lu sans droits", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_that_failed_says_why()
    {
        var section = Read(journal: CrashTimelineJournal.Failed("occupé")).Sections[4];

        Assert.Equal("Corrections de MAUS : non lu. occupé", section.Text);
    }

    [Fact]
    public void Sessions_left_unread_make_the_section_incomplete()
    {
        var session = Session(DaysAgo(2), Entry("Effets visuels", DaysAgo(2)));

        var section = Read(journal: new CrashTimelineJournal([session], Unreadable: 2)).Sections[4];

        Assert.Equal(CrashTimelineState.NotRead, section.State);
        Assert.Contains("2 séances", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Applied_and_reverted_fixes_are_lines_and_a_failed_or_old_one_is_not()
    {
        var journal = new CrashTimelineJournal(
        [
            Session(DaysAgo(5), Entry("Effets visuels", DaysAgo(5), DaysAgo(1)), Entry("Effets visuels", DaysAgo(5)), Entry("Échec", null)),
            Session(DaysAgo(60), Entry("Trop ancien", DaysAgo(60))),
        ]);

        var lines = Read(journal: journal).Lines;

        Assert.Equal(2, lines.Count);
        Assert.Equal("Annulée : Effets visuels", lines[0].What);
        Assert.Equal("Appliquée : Effets visuels", lines[1].What);
        Assert.All(lines, l => Assert.Equal(CrashTimelineKind.MausFix, l.Kind));
    }

    // ----- La liste et sa copie -----

    [Fact]
    public void Lines_go_from_the_most_recent_to_the_oldest_and_name_no_culprit()
    {
        var logs = Crash(new FakeEventLogs(), DaysAgo(2), "59");
        Update(logs, DaysAgo(2).AddMinutes(-20), "NVIDIA Display Driver Update (32.0.16.1088)");
        Update(logs, DaysAgo(1), "Mise à jour cumulative (KB5127694)");

        var timeline = Read(logs);

        Assert.Equal([CrashTimelineKind.Update, CrashTimelineKind.BlueScreen, CrashTimelineKind.Driver], timeline.Lines.Select(l => l.Kind));
        Assert.All(timeline.Lines, line =>
        {
            Assert.DoesNotContain("cause", line.What, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("probable", line.What, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("cela ne prouve pas", CrashTimeline.Caution, StringComparison.Ordinal);
    }

    [Fact]
    public void The_copied_text_says_what_was_read_and_every_line()
    {
        var logs = Crash(new FakeEventLogs(), DaysAgo(2), "59");

        var text = Read(logs, journal: CrashTimelineJournal.WithoutRights).ToText();

        Assert.Contains("Écrans bleus, bouton et arrêts non expliqués : 1 trouvé", text, StringComparison.Ordinal);
        Assert.Contains("Corrections de MAUS : non lu sans droits", text, StringComparison.Ordinal);
        Assert.Contains("0x0000003B", text, StringComparison.Ordinal);
        Assert.Contains(CrashTimeline.Caution, text, StringComparison.Ordinal);
    }
}
