using Maus.Core.Follow;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Follow;

/// <summary>Carnet de santé : rien d'inventé (vide reste vide), un fichier abîmé ne plante pas, ce PC n'est comparé qu'à lui-même.</summary>
public sealed class HealthLogTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("maus-carnet-").FullName;

    private string LogPath => Path.Combine(_folder, "health-log.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Finding Measured(string id, Dictionary<string, double>? measures, string? item = null) =>
        new() { Id = id, Title = id, Status = FindingStatus.Info, Explanation = "x", Measures = measures, MeasuredItem = item };

    private static ModuleResult Audit(params Finding[] findings) => new("M", "M", findings, TimeSpan.Zero);

    private static ModuleResult Full(double wear, double errors, double battery, params (DateTime At, double? Seconds)[] boots) => Audit(
        Measured("M11.disk-0-nvme", new() { [MeasureHistory.Wear] = wear, [MeasureHistory.Errors] = errors, [MeasureHistory.Temperature] = 40 }, "SSD A"),
        Measured("M16.battery.0", new() { [MeasureHistory.Health] = battery }),
        Measured("M12.boot-time", HealthLog.BootMeasures(boots)));

    private static HealthLogEntry Boots(DateTimeOffset at, params double[] seconds) => new(at)
    {
        Elevated = true,
        Boots = [.. seconds.Select((s, i) => new DatedBootTime(at.AddDays(i), s))],
    };

    [Fact]
    public void A_retouched_line_with_an_empty_item_does_not_stop_the_next_writing()
    {
        File.WriteAllText(LogPath, """[{"At":"2026-06-01T12:00:00+00:00","Disks":[null],"Batteries":[null]}]""");

        Assert.NotNull(new HealthLogStore(LogPath).AddAudit([Full(1, 0, 90)], Day.AddDays(1), weekly: false, elevated: true));

        Assert.Contains(new HealthLogStore(LogPath).Load(), e => e.At == Day.AddDays(1));
    }

    [Fact]
    public void Two_writers_at_the_same_moment_do_not_erase_each_other()
    {
        Parallel.For(0, 24, i => new HealthLogStore(LogPath).AddCooling(new CoolingResult(40, 70 + i, null), Day.AddMinutes(i)));

        Assert.Equal(24, new HealthLogStore(LogPath).Load().Count(e => e.Cooling is not null));
    }

    [Fact]
    public void A_line_notes_what_was_read_and_leaves_the_rest_empty()
    {
        var entry = HealthLog.FromAudit([Audit(Measured("M16.battery.0", new() { [MeasureHistory.Health] = 91 }))], [], Day, weekly: true, elevated: false);

        Assert.True(entry!.Weekly);
        Assert.False(entry.Elevated);
        Assert.Null(entry.Disks);
        Assert.Null(entry.Boots);
        Assert.Equal(91, Assert.Single(entry.Batteries!).CapacityPercent);
    }

    [Fact]
    public void An_audit_that_read_nothing_writes_no_line()
    {
        var unread = Audit(Finding.AdminRequired("M11.disk-reliability", "x"), Finding.AdminRequired("M12.boot-time", "x"));

        var store = new HealthLogStore(LogPath);

        Assert.Null(store.AddAudit([unread], Day, weekly: true, elevated: false));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void The_nvme_log_comes_first_and_zero_errors_is_a_real_value()
    {
        var entry = HealthLog.FromAudit([Audit(
            Measured("M11.disk-0-reliability", new() { [MeasureHistory.Wear] = 9, [MeasureHistory.Errors] = 7 }, "SSD A"),
            Measured("M11.disk-0-nvme", new() { [MeasureHistory.Wear] = 3, [MeasureHistory.Errors] = 0 }, "SSD A"),
            Measured("M11.disk-1-reliability", new() { [MeasureHistory.Temperature] = 30 }, "HDD"))], [], Day, weekly: false, elevated: true);

        var disk = Assert.Single(entry!.Disks!);
        Assert.Equal(HealthLog.SourceNvme, disk.Source);
        Assert.Equal(3, disk.WearPercent);
        Assert.Equal(0, disk.Errors);
    }

    [Fact]
    public void A_boot_counts_once_even_when_several_audits_read_it()
    {
        var store = new HealthLogStore(LogPath);
        var first = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Local);
        var second = first.AddDays(1);

        store.AddAudit([Full(1, 0, 90, (first, 30))], Day, false, true);
        store.AddAudit([Full(1, 0, 90, (first, 30))], Day.AddHours(1), false, true);
        store.AddAudit([Full(1, 0, 90, (first, 30), (second, 40))], Day.AddDays(2), false, true);

        var boots = store.BootTimes();
        Assert.Equal([30d, 40d], boots.Select(b => b.Seconds));
        Assert.Equal(new DateTimeOffset(first), boots[0].At);
        Assert.Equal(3, store.Load().Count);
    }

    [Fact]
    public void Boot_times_are_empty_rather_than_invented()
    {
        Assert.Empty(new HealthLogStore(LogPath).BootTimes());
        Assert.Null(HealthLog.BootMeasures([(DateTime.Now, null), (DateTime.Now, 0), (DateTime.MinValue, 12)]));
    }

    [Fact]
    public void A_damaged_file_never_throws_and_shows_nothing_false()
    {
        File.WriteAllText(LogPath, "{ abîmé");
        var store = new HealthLogStore(LogPath);

        Assert.False(store.TryLoad(out var entries));
        Assert.Empty(entries);
        Assert.Empty(store.Load());

        // Le prochain audit repart d'une ligne ; la copie du fichier abîmé reste à côté.
        store.AddAudit([Full(1, 0, 90)], Day, false, true);
        Assert.Single(store.Load());
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void Impossible_values_in_the_file_are_not_read()
    {
        File.WriteAllText(LogPath, """
            [{"At":"2026-06-01T12:00:00+00:00","Disks":[{"Id":"0","Name":"A","Source":"nvme","WearPercent":-4,"Errors":2},{"Id":null,"Name":"B","Source":"nvme"}],
              "Batteries":[{"Id":"0","CapacityPercent":-1}],"Boots":[{"At":"2026-06-01T08:00:00+00:00","Seconds":-3}]},
             {"Disks":[{"Id":"1","Name":"C","Source":"nvme","WearPercent":5}]}, null]
            """);

        var entry = Assert.Single(new HealthLogStore(LogPath).Load());

        var disk = Assert.Single(entry.Disks!);
        Assert.Null(disk.WearPercent);
        Assert.Equal(2, disk.Errors);
        Assert.Empty(entry.Batteries!);
        Assert.Empty(entry.Boots!);
    }

    [Fact]
    public void The_old_cooling_figure_comes_in_with_the_date_of_its_file()
    {
        var old = Path.Combine(_folder, "cooling.txt");
        File.WriteAllText(old, "41.5");
        var date = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(old, date);
        var store = new HealthLogStore(LogPath, old);

        var seen = store.LastCooling()!;
        Assert.Equal(41.5, seen.Cooling!.RiseC);
        Assert.True(seen.Cooling.DatedByFile);
        Assert.Equal(new DateTimeOffset(date), seen.At);

        store.AddCooling(new CoolingResult(40, 85, 50), Day);

        Assert.Equal([41.5, 45], store.Load().Select(e => e.Cooling!.RiseC));
        Assert.False(store.LastCooling()!.Cooling!.DatedByFile);
    }

    [Theory]
    [InlineData("pas un nombre")]
    [InlineData("")]
    public void A_damaged_old_cooling_file_is_ignored(string text)
    {
        var old = Path.Combine(_folder, "cooling.txt");
        File.WriteAllText(old, text);

        Assert.Null(new HealthLogStore(LogPath, old).LastCooling());
    }

    [Fact]
    public void The_log_stays_bounded_but_keeps_where_each_series_starts()
    {
        var store = new HealthLogStore(LogPath);
        for (var i = 0; i < 405; i++)
        {
            var finding = i == 0
                ? Measured("M16.battery.0", new() { [MeasureHistory.Health] = 99 })
                : Measured("M11.disk-0-nvme", new() { [MeasureHistory.Errors] = i }, "SSD A");
            store.AddAudit([Audit(finding)], Day.AddDays(i - 405), false, true);
        }

        var kept = store.Load();
        Assert.Equal(402, kept.Count);
        Assert.Equal(99, kept[0].Batteries![0].CapacityPercent);
        Assert.Equal(1, kept[1].Disks![0].Errors);
        Assert.Equal(404, kept[^1].Disks![0].Errors);
    }

    [Fact]
    public void Zero_entries_gives_no_row_and_nothing_to_compare()
    {
        var summary = HealthLogSummary.Build([]);

        Assert.Empty(summary.Rows);
        Assert.Empty(summary.LastLine);
        Assert.Single(summary.Notes); // le test de refroidissement, à lancer soi-même
    }

    [Fact]
    public void Unread_without_rights_is_said_and_never_shown_as_zero()
    {
        var entry = HealthLog.FromAudit([Audit(Measured("M16.battery.0", new() { [MeasureHistory.Health] = 91 }))], [], Day, weekly: true, elevated: false)!;

        var summary = HealthLogSummary.Build([entry]);

        var row = Assert.Single(summary.Rows);
        Assert.Contains("91", row.Current, StringComparison.Ordinal);
        Assert.False(row.HasCurve);
        Assert.False(row.HasFirst);
        Assert.Contains(summary.Notes, n => n.Contains("disques", StringComparison.Ordinal) && n.Contains("sans droits", StringComparison.Ordinal));
        Assert.Contains(summary.Notes, n => n.Contains("démarrage", StringComparison.Ordinal) && n.Contains("sans droits", StringComparison.Ordinal));
    }

    [Fact]
    public void What_an_audit_without_rights_could_not_read_is_said_once_and_a_desktop_hears_nothing_of_batteries()
    {
        var entry = new HealthLogEntry(Day) { Disks = [], Boots = [] };

        var desktop = Assert.Single(HealthLogSummary.Build([entry]).Notes, n => n.Contains("Lancez un audit", StringComparison.Ordinal));
        Assert.Contains("disques", desktop, StringComparison.Ordinal);
        Assert.Contains("démarrage", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("batterie", desktop, StringComparison.Ordinal);

        var laptop = Assert.Single(HealthLogSummary.Build([entry], hasBattery: true).Notes, n => n.Contains("Lancez un audit", StringComparison.Ordinal));
        Assert.Contains("batterie", laptop, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_the_last_audit_could_not_read_keeps_its_own_date_and_says_so()
    {
        var withRights = new HealthLogEntry(Day) { Elevated = true, Disks = [new("0", "SSD A", HealthLog.SourceNvme, 4, 0)] };
        var without = new HealthLogEntry(Day.AddDays(7)) { Batteries = [new("0", 90)] };

        var rows = HealthLogSummary.Build([withRights, without]).Rows;

        Assert.All(rows.Where(r => r.Label.Contains("SSD A", StringComparison.Ordinal)), r => Assert.Contains("sans droits", r.Unread, StringComparison.Ordinal));
        Assert.Null(rows.Single(r => r.Label.Contains("batterie", StringComparison.Ordinal)).Unread);
    }

    [Fact]
    public void A_disk_number_taken_by_another_model_starts_another_curve()
    {
        var rows = HealthLogSummary.Build([
            new HealthLogEntry(Day) { Disks = [new("0", "SSD A", HealthLog.SourceNvme, 40)] },
            new HealthLogEntry(Day.AddDays(1)) { Disks = [new("0", "SSD B", HealthLog.SourceNvme, 1)] },
        ]).Rows;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Single(r.Values));
    }

    [Theory]
    [InlineData(new double[] { 90, 90 }, "Aucun changement")]
    [InlineData(new double[] { 90, 89 }, "Petite variation")] // le plus petit écart que la mesure donne
    [InlineData(new double[] { 90, 84, 87 }, "Petite variation")] // déjà vu sur ce PC
    [InlineData(new double[] { 90, 88, 84 }, "-6 point")]
    public void A_small_variation_is_called_small_without_any_health_threshold(double[] values, string expected)
    {
        var entries = values.Select((v, i) => new HealthLogEntry(Day.AddDays(i)) { Batteries = [new("0", v)] }).ToList();

        var row = Assert.Single(HealthLogSummary.Build(entries).Rows);

        Assert.Contains(expected, row.Variation, StringComparison.Ordinal);
        Assert.DoesNotContain("panne", row.Variation, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_error_counts_and_a_rise_is_said()
    {
        var entries = new[] { 0d, 0, 2 }.Select((v, i) => new HealthLogEntry(Day.AddDays(i)) { Disks = [new("0", "SSD A", HealthLog.SourceNvme, null, v)] }).ToList();

        var row = Assert.Single(HealthLogSummary.Build(entries).Rows);

        Assert.Contains("2 de plus", row.Variation, StringComparison.Ordinal);
        Assert.Equal([0d, 0, 2], row.Values);
    }

    [Fact]
    public void Boot_time_shows_a_median_and_a_range_rather_than_one_figure()
    {
        var row = Assert.Single(HealthLogSummary.Build([Boots(Day, 30, 50, 40, 44, 36, 42)]).Rows);

        Assert.Contains("En général 41", row.Range, StringComparison.Ordinal);
        Assert.Contains("de 30 à 50", row.Range, StringComparison.Ordinal);
        Assert.Contains("probablement", row.Variation, StringComparison.Ordinal);
        Assert.Equal(6, row.Values.Count);
    }

    [Fact]
    public void A_boot_time_out_of_its_early_range_is_stated_without_a_culprit()
    {
        var row = Assert.Single(HealthLogSummary.Build([Boots(Day, 30, 31, 32, 60, 62, 64)]).Rows);

        Assert.Contains("hors de l'écart", row.Variation, StringComparison.Ordinal);
        Assert.Contains("sans en connaître la cause", row.Variation, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_few_boots_are_not_compared()
    {
        var row = Assert.Single(HealthLogSummary.Build([Boots(Day, 30, 90)]).Rows);

        Assert.Contains("encore peu", row.Variation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cooling_test_gets_its_date_and_its_first_value()
    {
        var entries = new[]
        {
            new HealthLogEntry(Day) { Cooling = new(41.5, DatedByFile: true) },
            new HealthLogEntry(Day.AddDays(30)) { Cooling = new(45, 40, 85, 50) },
        };

        var row = Assert.Single(HealthLogSummary.Build(entries).Rows);

        Assert.Contains("pic à 85", row.Current, StringComparison.Ordinal);
        Assert.Contains("date du fichier", row.First, StringComparison.Ordinal);
        Assert.Contains("+3,5 °C", row.Variation, StringComparison.Ordinal);
        Assert.True(row.HasCurve);
    }
}
