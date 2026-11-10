using Maus.Core.Fixes;
using Maus.Core.Follow;

namespace Maus.Core.Tests.Follow;

public sealed class BeforeAfterTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static JournalEntry Entry(string id, string module = "M12", SettingKey? key = null, EntryState state = EntryState.Applied, DateTimeOffset? revertedAt = null) => new()
    {
        ChangeId = id,
        ModuleId = module,
        ChangeTitle = "Titre " + id,
        Key = key ?? SettingKey.Registry("HKCU", @"Software\X", id),
        State = state,
        RevertedAt = revertedAt,
    };

    private static JournalSession Session(DateTimeOffset at, params JournalEntry[] entries) => new()
    {
        Id = "s" + at.ToUnixTimeSeconds(),
        CreatedAt = at,
        Entries = [.. entries],
    };

    /// <summary>Un démarrage par jour : les valeurs de <paramref name="before"/> finissent la veille de la séance, celles de <paramref name="after"/> commencent le lendemain.</summary>
    private static List<DatedBootTime> Boots(double[] before, double[] after) =>
    [
        .. before.Select((s, i) => new DatedBootTime(Day0.AddDays(i - before.Length), s)),
        .. after.Select((s, i) => new DatedBootTime(Day0.AddDays(i + 1), s)),
    ];

    private static BeforeAfterRow One(JournalSession session, IEnumerable<DatedBootTime> boots) => Assert.Single(BeforeAfter.Build([session], boots).Rows);

    [Fact]
    public void Overlapping_ranges_say_no_visible_effect_without_softening()
    {
        var row = One(Session(Day0, Entry("M12.a")), Boots([30, 32, 34, 36, 38], [29, 30, 31, 32, 33]));

        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
        Assert.StartsWith("Aucun effet visible sur le temps de démarrage", row.Conclusion, StringComparison.Ordinal);
        Assert.Contains("34 s en général, de 30 à 38 s, sur 5 démarrages", row.Before, StringComparison.Ordinal);
        Assert.Contains("31 s en général, de 29 à 33 s, sur 5 démarrages", row.After, StringComparison.Ordinal);
        Assert.Null(row.Caution);
    }

    [Fact]
    public void Ranges_that_touch_count_as_overlapping()
    {
        var row = One(Session(Day0, Entry("M12.a")), Boots([40, 41, 42, 43, 44], [36, 37, 38, 39, 40]));

        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
    }

    [Fact]
    public void Separate_ranges_say_probable_and_name_another_explanation()
    {
        var shorter = One(Session(Day0, Entry("M12.a")), Boots([40, 41, 42, 43, 44], [30, 31, 32, 33, 34]));
        var longer = One(Session(Day0, Entry("M12.a")), Boots([30, 31, 32, 33, 34], [40, 41, 42, 43, 44]));

        Assert.Equal(BeforeAfterVerdict.Shorter, shorter.Verdict);
        Assert.Equal(BeforeAfterVerdict.Longer, longer.Verdict);
        Assert.Contains("probable", shorter.Conclusion, StringComparison.Ordinal);
        Assert.Contains("Autre chose peut expliquer", shorter.Caution, StringComparison.Ordinal);
        Assert.DoesNotContain("grâce", shorter.Conclusion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mise à jour", longer.Caution, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_startup_session_in_the_period_is_named_as_a_possible_explanation()
    {
        var session = Session(Day0, Entry("M12.a"));
        var other = Session(Day0.AddDays(2), Entry("M12.b"));

        var rows = BeforeAfter.Build([session, other], Boots([40, 41, 42, 43, 44], [30, 31, 32, 33, 34])).Rows;

        Assert.Contains("autre séance", rows.Single(r => r.Title.Contains("M12.a", StringComparison.Ordinal)).Caution, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    [InlineData(0, 0)]
    public void Too_few_boots_on_one_side_give_no_figure(int before, int after)
    {
        var row = One(Session(Day0, Entry("M12.a")), Boots([.. Enumerable.Repeat(30d, before)], [.. Enumerable.Repeat(30d, after)]));

        Assert.Equal(BeforeAfterVerdict.NotEnough, row.Verdict);
        Assert.StartsWith("Pas encore assez de démarrages", row.Conclusion, StringComparison.Ordinal);
        Assert.Contains(BeforeAfter.MinimumBootsPerSide.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Conclusion, StringComparison.Ordinal);
        Assert.Null(row.Before);
        Assert.Null(row.After);
        Assert.False(row.HasFigures);
    }

    [Fact]
    public void A_correction_that_cannot_touch_the_boot_gets_no_figure()
    {
        var row = One(Session(Day0, Entry("M06.a", "M06")), Boots([40, 41, 42, 43, 44], [30, 31, 32, 33, 34]));

        Assert.Equal(BeforeAfterVerdict.NotMeasurable, row.Verdict);
        Assert.Equal("Cette correction ne se mesure pas ainsi.", row.Conclusion);
        Assert.False(row.HasFigures);
        Assert.Null(row.Caution);
    }

    [Fact]
    public void What_can_touch_the_boot_is_a_closed_list()
    {
        Assert.True(BeforeAfter.CanTouchBoot(Entry("M12.a")));
        Assert.True(BeforeAfter.CanTouchBoot(Entry("M05.fast-startup", "M05", SettingKey.Registry("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"))));
        Assert.True(BeforeAfter.CanTouchBoot(Entry("M01.x", "M01", SettingKey.Registry("HKLM", @"SYSTEM\CurrentControlSet\Services\Audiosrv", "Start"))));
        Assert.False(BeforeAfter.CanTouchBoot(Entry("M01.y", "M01", SettingKey.Registry("HKLM", @"SOFTWARE\X", "Start"))));
        Assert.False(BeforeAfter.CanTouchBoot(Entry("M13.z", "M13", SettingKey.Registry("HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable"))));
        Assert.False(BeforeAfter.CanTouchBoot(Entry("M04.t", "M04", SettingKey.ScheduledTask(@"\Microsoft\Windows\X\", "T"))));
    }

    [Fact]
    public void A_mixed_session_is_measured_and_says_what_is_left_out()
    {
        var row = One(
            Session(Day0, Entry("M12.a"), Entry("M06.b", "M06"), Entry("M06.c", "M06")),
            Boots([30, 32, 34, 36, 38], [29, 30, 31, 32, 33]));

        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
        Assert.Contains("M12.a", row.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("M06.b", row.Title, StringComparison.Ordinal);
        Assert.Contains("2 autres corrections", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correction_that_was_never_written_is_not_compared()
    {
        var failed = Session(Day0, Entry("M12.a", state: EntryState.Failed));
        var interrupted = Session(Day0.AddDays(1), Entry("M12.b", state: EntryState.Pending));

        Assert.Empty(BeforeAfter.Build([failed, interrupted], Boots([30, 31, 32, 33, 34], [30, 31, 32, 33, 34])).Rows);
    }

    [Fact]
    public void After_being_undone_only_the_boots_before_the_undo_count()
    {
        var undone = Day0.AddDays(5).AddHours(1);
        var session = Session(Day0, Entry("M12.a", state: EntryState.Reverted, revertedAt: undone));

        // Les cinq premiers démarrages après la séance se recouvrent avec avant ; ceux d'après l'annulation ne doivent pas compter.
        var row = One(session, Boots([30, 32, 34, 36, 38], [31, 31, 31, 31, 31, 90, 90, 90]));

        Assert.Equal(BeforeAfterVerdict.NoVisibleEffect, row.Verdict);
        Assert.Contains("de 31 à 31 s, sur 5", row.After, StringComparison.Ordinal);
        Assert.Contains("Annulée", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Undone_too_soon_does_not_promise_that_boots_will_come()
    {
        var session = Session(Day0, Entry("M12.a", state: EntryState.Reverted, revertedAt: Day0.AddHours(1)));

        var row = One(session, Boots([30, 31, 32, 33, 34], [30, 31, 32, 33, 34]));

        Assert.Equal(BeforeAfterVerdict.NotEnough, row.Verdict);
        Assert.StartsWith("Pas assez de démarrages entre la séance et son annulation", row.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_boots_closest_to_the_session_are_compared()
    {
        // Des démarrages anciens et très longs élargiraient la plage d'avant jusqu'à tout recouvrir.
        var old = Enumerable.Range(0, 5).Select(i => new DatedBootTime(Day0.AddDays(-200 - i), 100));
        var recent = Enumerable.Repeat(50d, 30).ToArray();

        var row = One(Session(Day0, Entry("M12.a")), [.. old, .. Boots(recent, [30, 31, 32, 33, 34])]);

        Assert.Equal(BeforeAfterVerdict.Shorter, row.Verdict);
        Assert.Contains("sur 30 démarrages", row.Before, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_read_gives_nothing()
    {
        var summary = BeforeAfter.Build([], []);

        Assert.Empty(summary.Rows);
        Assert.Equal(0, summary.Older);
    }

    [Fact]
    public void The_most_recent_sessions_come_first_and_the_others_are_counted()
    {
        var sessions = Enumerable.Range(0, 12).Select(i => Session(Day0.AddDays(i), Entry("M06." + i, "M06"))).ToList();

        var summary = BeforeAfter.Build(sessions, []);

        Assert.Equal(10, summary.Rows.Count);
        Assert.Equal(2, summary.Older);
        Assert.Contains("M06.11", summary.Rows[0].Title, StringComparison.Ordinal);
    }
}
