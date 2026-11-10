using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Maus.Core.Fixes;
using Maus.Core.Follow;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Platform;

/// <summary>
/// Fichiers de MAUS abîmés par l'utilisateur ou par un programme. Trois exigences : jamais de plantage, jamais un affichage faux
/// présenté comme mesuré, jamais un fichier seulement illisible écrasé sans copie.
/// </summary>
public sealed class DamagedFilesTests : IDisposable
{
    private const string GoodDate = "2026-06-01T12:00:00+00:00";
    private const string LaterDate = "2026-06-02T12:00:00+00:00";

    private static readonly DateTimeOffset Day = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Stores = ["audit", "health", "card", "benchmark", "measures"];

    private static readonly string[] BrokenContents =
    [
        "",
        "   \r\n",
        "\0\0\0\0",
        "[{\"At\":\"2026",
        "[{\"Kind\":\"x\",",
        "{",
        "[",
        "{}",
        "42",
        "-1e999",
        "true",
        "null",
        "\"texte\"",
        "NaN",
        "[NaN]",
        "[1,2,3]",
        "[[]]",
        "[\"x\"]",
        "{\"a\":[1]}",
        "[{\"At\":\"2026-13-45T99:00:00\",\"Date\":\"2026-13-45T99:00:00\",\"date\":\"2026-13-45T99:00:00\",\"UpdatedAt\":\"2026-13-45T99:00:00\"}]",
        "[{\"At\":\"\\ud800\",\"Kind\":\"\\ud800\",\"date\":\"\\ud800\",\"UpdatedAt\":\"\\ud800\"}]",
    ];

    private readonly string _folder = Directory.CreateTempSubdirectory("maus-abime-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    public static TheoryData<string, string> BrokenFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var store in Stores)
        {
            foreach (var content in BrokenContents)
            {
                data.Add(store, content);
            }
        }

        return data;
    }

    public static TheoryData<string> AllStores() => [.. Stores];

    private static void NoWait(TimeSpan delay)
    {
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    private string[] Copies() => Directory.GetFiles(_folder, "*.illisible-*");

    private static int Count(string store, string path) => store switch
    {
        "audit" => new AuditSnapshotStore(path, NoWait).Load().Count,
        "health" => new HealthLogStore(path, wait: NoWait).Load().Count,
        "card" => new PartsCardStore(path, NoWait).Load() is null ? 0 : 1,
        "benchmark" => new BenchmarkHistoryStore(path, NoWait).Load().Count,
        _ => new BenchmarkHistory(path, NoWait, 2000).Load().Count,
    };

    private static void Add(string store, string path)
    {
        switch (store)
        {
            case "audit":
                new AuditSnapshotStore(path, NoWait).Add(new AuditSnapshot(Day, "26300.1", 80, []));
                break;
            case "health":
                new HealthLogStore(path, wait: NoWait).AddCooling(new CoolingResult(30, 60, 10), Day);
                break;
            case "card":
                new PartsCardStore(path, NoWait).Update([new HardwarePart(PartKind.Screen, "A", "A")], Day);
                break;
            case "benchmark":
                new BenchmarkHistoryStore(path, NoWait).Add(new BenchmarkReport { Date = Day, Completed = true });
                break;
            default:
                new BenchmarkHistory(path, NoWait, 2000).Add(new BenchmarkEntry("measure:x:wear", 1, true, Day));
                break;
        }
    }

    /// <summary>Ni infini, ni « NaN », ni date impossible, ni texte sans fin, ni caractère de contrôle dans ce qui est montré.</summary>
    private static void AssertHonest(IEnumerable<string?> shown)
    {
        foreach (var text in shown.OfType<string>())
        {
            Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
            Assert.DoesNotContain("0001", text, StringComparison.Ordinal);
            Assert.DoesNotContain("9999", text, StringComparison.Ordinal);
            Assert.DoesNotContain("2090", text, StringComparison.Ordinal);
            Assert.True(text.Length < 1000, "Texte trop long : " + text.Length);
            AssertPrintable(text);
        }
    }

    private static void AssertPrintable(string text) =>
        Assert.DoesNotContain(text, c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);

    private static IEnumerable<string?> Shown(HealthLogSummary summary) =>
        summary.Rows.SelectMany(r => new[] { r.Label, r.Current, r.First, r.Range, r.Variation, r.Unread })
            .Concat(summary.Notes)
            .Append(summary.LastLine);

    private static string Json(string text) => JsonSerializer.Serialize(text);

    private static readonly string Dirty = new string('x', 100_000) + "\u0000\u0007\u001b\u202e\n";

    // --- Tous les fichiers d'historique ---

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public void A_broken_file_shows_nothing_and_is_copied_before_being_replaced(string store, string content)
    {
        var path = PathOf(store + ".json");
        File.WriteAllText(path, content);

        Assert.Equal(0, Count(store, path));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Empty(Copies());

        Add(store, path);

        Assert.Equal(1, Count(store, path));
        Assert.Equal(content, File.ReadAllText(Assert.Single(Copies())));
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public void A_locked_file_is_never_overwritten(string store)
    {
        var path = PathOf(store + ".json");
        File.WriteAllText(path, "[]");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, Count(store, path));
            Assert.Throws<IOException>(() => Add(store, path));
        }

        Assert.Equal("[]", File.ReadAllText(path));
        Assert.Empty(Copies());
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public void A_file_far_too_big_is_not_read_and_not_replaced(string store)
    {
        var path = PathOf(store + ".json");
        const long size = 65L * 1024 * 1024;
        using (var file = File.Create(path))
        {
            file.SetLength(size);
        }

        Assert.Equal(0, Count(store, path));
        Assert.Throws<IOException>(() => Add(store, path));

        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Empty(Copies());
    }

    [Fact]
    public void A_giant_list_of_measures_gives_only_what_MAUS_keeps_and_leaves_the_file_alone()
    {
        var path = PathOf("measures-history.json");
        var text = new StringBuilder("[");
        for (var i = 0; i < 200_000; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $$"""{"Kind":"measure:x:wear","Score":{{i % 90}},"Stable":true,"At":"{{GoodDate}}"},""");
        }

        text.Length--;
        text.Append(']');
        File.WriteAllText(path, text.ToString());

        var loaded = new BenchmarkHistory(path, NoWait, 2000).Load();

        Assert.Equal(2000, loaded.Count);
        Assert.Equal(text.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void A_giant_health_log_stays_within_what_MAUS_keeps()
    {
        var path = PathOf("health-log.json");
        var lines = Enumerable.Range(0, 20_000).Select(i =>
            $$"""{"At":"{{Day.AddMinutes(i):O}}","Batteries":[{"Id":"0","CapacityPercent":90}]}""");
        File.WriteAllText(path, "[" + string.Join(',', lines) + "]");

        var entries = new HealthLogStore(path, wait: NoWait).Load();

        Assert.InRange(entries.Count, 1, 401);
        Assert.NotEmpty(HealthLogSummary.Build(entries).Rows);
    }

    [Fact]
    public void A_health_log_where_every_line_starts_a_series_stays_bounded()
    {
        var path = PathOf("health-log.json");
        var lines = Enumerable.Range(0, 5_000).Select(i =>
            $$"""{"At":"{{Day.AddMinutes(i):O}}","Batteries":[{"Id":"{{i}}","CapacityPercent":90}]}""");
        File.WriteAllText(path, "[" + string.Join(',', lines) + "]");

        Assert.InRange(new HealthLogStore(path, wait: NoWait).Load().Count, 1, 656);
    }

    [Fact]
    public void A_line_of_the_health_log_with_endless_lists_stays_bounded()
    {
        var path = PathOf("health-log.json");
        var disks = string.Join(',', Enumerable.Range(0, 1_000).Select(i => $$"""{"Id":"{{i}}","Name":"SSD","Source":"nvme","WearPercent":5}"""));
        var batteries = string.Join(',', Enumerable.Range(0, 1_000).Select(i => $$"""{"Id":"{{i}}","CapacityPercent":90}"""));
        var boots = string.Join(',', Enumerable.Range(0, 1_000).Select(i => $$"""{"At":"{{Day.AddMinutes(i):O}}","Seconds":30}"""));
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Disks":[{{disks}}],"Batteries":[{{batteries}}],"Boots":[{{boots}}]}]""");

        var entry = Assert.Single(new HealthLogStore(path, wait: NoWait).Load());

        Assert.Equal(64, entry.Disks!.Count);
        Assert.Equal(64, entry.Batteries!.Count);
        Assert.Equal(60, entry.Boots!.Count);
    }

    [Fact]
    public void A_summary_with_endless_lists_stays_bounded_and_a_truncated_list_is_not_compared()
    {
        var path = PathOf("audit-snapshots.json");
        var findings = string.Join(',', Enumerable.Range(0, 3_000).Select(i => $$"""{"Id":"M01.x{{i}}","ModuleId":"M01","Title":"t","Status":"Ok"}"""));
        var tasks = string.Join(',', Enumerable.Range(0, 501).Select(i => $$"""{"Name":"tâche {{i}}"}"""));
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Build":"b","Score":80,"Findings":[{{findings}}],"Tasks":[{{tasks}}],"Drivers":[{"Name":"pilote"}]}]""");

        var kept = Assert.Single(new AuditSnapshotStore(path, NoWait).Load());

        Assert.Equal(2_000, kept.Findings.Count);
        Assert.Null(kept.Tasks);
        Assert.Single(kept.Drivers!);
    }

    [Fact]
    public void Lines_out_of_order_are_read_from_oldest_to_newest()
    {
        var path = PathOf("audit-snapshots.json");
        File.WriteAllText(path, $$"""[{"At":"{{LaterDate}}","Build":"b","Score":80,"Findings":[]},{"At":"{{GoodDate}}","Build":"a","Score":70,"Findings":[]}]""");

        var kept = new AuditSnapshotStore(path).Load();

        Assert.Equal(["a", "b"], kept.Select(s => s.Build));
        Assert.Equal("b", WindowsUndid.LastAudit(kept)!.Build);
    }

    // --- Carnet de santé ---

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("1e308")]
    [InlineData("-1000")]
    public void An_impossible_number_in_the_health_log_is_not_shown(string number)
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Elevated":true,
              "Disks":[{"Id":"0","Name":"SSD","Source":"nvme","WearPercent":{{number}},"Errors":{{number}}}],
              "Batteries":[{"Id":"0","CapacityPercent":{{number}}}],
              "Boots":[{"At":"{{GoodDate}}","Seconds":{{number}} }]},
             {"At":"{{LaterDate}}","Cooling":{"RiseC":{{number}},"IdleC":{{number}},"PeakC":{{number}},"BackInSeconds":{{number}} } }]
            """);

        var summary = HealthLogSummary.Build(new HealthLogStore(path).Load());

        Assert.Empty(summary.Rows);
        AssertHonest(Shown(summary));
    }

    [Fact]
    public void An_infinite_error_count_never_reads_as_no_change()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Disks":[{"Id":"0","Name":"SSD","Source":"nvme","Errors":1e999}]},
             {"At":"{{LaterDate}}","Disks":[{"Id":"0","Name":"SSD","Source":"nvme","Errors":1e999}]}]
            """);

        var summary = HealthLogSummary.Build(new HealthLogStore(path).Load());

        Assert.DoesNotContain(summary.Rows, r => r.Variation.Contains("Aucun changement", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_written_as_text_makes_the_log_unreadable_not_empty()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Batteries":[{"Id":"0","CapacityPercent":"NaN"}]}]""");

        Assert.False(new HealthLogStore(path).TryLoad(out var entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("1999-12-31T00:00:00+00:00")]
    [InlineData("2090-01-01T00:00:00+00:00")]
    [InlineData("9999-12-31T23:59:59+00:00")]
    public void An_impossible_date_never_hides_the_real_readings(string date)
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Batteries":[{"Id":"0","CapacityPercent":90}],"Boots":[{"At":"{{date}}","Seconds":10}]},
             {"At":"{{date}}","Batteries":[{"Id":"0","CapacityPercent":1}],"Boots":[{"At":"{{GoodDate}}","Seconds":20}]}]
            """);

        var summary = HealthLogSummary.Build(new HealthLogStore(path).Load());

        var row = Assert.Single(summary.Rows);
        Assert.StartsWith("Actuel : 90", row.Current, StringComparison.Ordinal);
        AssertHonest(Shown(summary));
    }

    [Fact]
    public void A_line_with_an_impossible_date_stays_in_the_file()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, """[{"At":"2090-01-01T00:00:00+00:00","Batteries":[{"Id":"7","CapacityPercent":90}]}]""");

        new HealthLogStore(path).AddCooling(new CoolingResult(30, 60, 10), Day);

        Assert.Contains("2090-01-01", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Copies());
    }

    [Fact]
    public void Very_long_names_and_control_characters_do_not_reach_the_health_log_panel()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Disks":[{"Id":{{Json(Dirty)}},"Name":{{Json(Dirty)}},"Source":"nvme","WearPercent":1}],
              "Batteries":[{"Id":{{Json(Dirty)}},"CapacityPercent":90},{"Id":"b","CapacityPercent":80}]}]
            """);

        var summary = HealthLogSummary.Build(new HealthLogStore(path).Load());

        Assert.Equal(3, summary.Rows.Count);
        AssertHonest(Shown(summary));
    }

    [Fact]
    public void Unknown_keys_null_items_and_unknown_sources_are_left_out()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Zzz":{"a":[1,2]},"Disks":[null,{"Id":"0","Name":"SSD","Source":"zzz","Errors":3},{"Id":null,"Name":"x","Source":"nvme","Errors":1}],
              "Batteries":[null,{"Id":null,"CapacityPercent":90}],"Boots":[null,{"At":"{{GoodDate}}","Seconds":10}]}]
            """);

        var summary = HealthLogSummary.Build(new HealthLogStore(path).Load());

        var row = Assert.Single(summary.Rows);
        Assert.Equal("Temps de démarrage", row.Label);
    }

    [Fact]
    public void A_test_without_its_temperatures_is_not_called_an_old_test()
    {
        var path = PathOf("health-log.json");
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Cooling":{"RiseC":12} }]""");

        var row = Assert.Single(HealthLogSummary.Build(new HealthLogStore(path).Load()).Rows);

        Assert.DoesNotContain("ancien test", row.Current, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("1e308")]
    [InlineData("-1e308")]
    [InlineData("∞")]
    public void An_impossible_old_cooling_figure_is_not_taken_up(string content)
    {
        var old = PathOf("cooling.txt");
        File.WriteAllText(old, content);

        Assert.Empty(new HealthLogStore(PathOf("health-log.json"), old).Load());
    }

    // --- Résumés d'audit ---

    [Theory]
    [InlineData("999")]
    [InlineData("-5")]
    [InlineData("2147483647")]
    public void An_impossible_score_is_not_a_score(string score)
    {
        var path = PathOf("audit-snapshots.json");
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Build":"b","Score":{{score}},"Weekly":true,"Findings":[]},{"At":"{{LaterDate}}","Build":"c","Score":{{score}},"Findings":[]}]""");

        // Seul un audit de la semaine peut ne pas avoir de score ; un audit lancé à la main sans score valable n'est pas montré.
        var kept = Assert.Single(new AuditSnapshotStore(path).Load());

        Assert.Equal("b", kept.Build);
        Assert.False(kept.HasScore);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public void An_unknown_status_makes_the_summaries_damaged(string status)
    {
        var path = PathOf("audit-snapshots.json");
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Build":"b","Score":80,"Findings":[{"Id":"M01.a","ModuleId":"M01","Title":"t","Status":{{status}} }]}]""");

        Assert.Empty(new AuditSnapshotStore(path).Load());
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("2090-01-01T00:00:00+00:00")]
    [InlineData("9999-12-31T23:59:59+00:00")]
    public void A_summary_with_an_impossible_date_never_counts_as_the_last_audit(string date)
    {
        var path = PathOf("audit-snapshots.json");
        File.WriteAllText(path, $$"""[{"At":"{{GoodDate}}","Build":"vrai","Score":80,"Findings":[]},{"At":"{{date}}","Build":"faux","Score":100,"Findings":[]}]""");

        var kept = new AuditSnapshotStore(path).Load();

        Assert.Equal("vrai", Assert.Single(kept).Build);
    }

    [Fact]
    public void Summaries_with_empty_values_can_be_compared_and_followed()
    {
        var path = PathOf("audit-snapshots.json");
        File.WriteAllText(path, $$"""
            [{"At":"{{GoodDate}}","Build":null,"Score":80,"Weekly":true,"Zzz":1,"Tasks":[null,{"Name":null}],"Drivers":[null],
              "Findings":[{"Id":"M09.driver.a","ModuleId":"M09","Title":null,"Status":"Warning"},{"Id":"M12.source-x","ModuleId":"M12","Title":null,"Status":"Unknown"}]},
             {"At":"{{LaterDate}}","Build":null,"Score":80,"Weekly":true,"Tasks":[{"Name":{{Json(Dirty)}},"Version":{{Json(Dirty)}} }],
              "Findings":[{"Id":"M09.driver.b","ModuleId":"M09","Title":{{Json(Dirty)}},"Status":"Problem"},{"Id":"M11.disk-0","ModuleId":"M11","Title":null,"Status":"Problem"}]}]
            """);

        var kept = new AuditSnapshotStore(path).Load();

        Assert.Equal(2, kept.Count);
        Assert.All(kept.SelectMany(s => s.Findings), f =>
        {
            Assert.NotNull(f.Title);
            Assert.True(f.Title.Length < 1000);
            AssertPrintable(f.Title);
        });
        var report = WhatChanged.Compare(kept, kept[0], kept[1]);
        AssertHonest(report.Changes.Select(c => c.Name).Concat(report.NotComparable.Select(n => n.Name)));
        Assert.NotNull(QuietWeek.Build(kept, null, null, Day.AddDays(3)));
        var card = PartsCard.Apply(PartsCard.Apply(null, [new HardwarePart(PartKind.Graphics, "A", "A")], Day), [new HardwarePart(PartKind.Graphics, "A", "A"), new HardwarePart(PartKind.Graphics, "B", "B")], Day);
        Assert.NotEmpty(NewPartView.Build(card, PartCheck.From(kept[1]).Concat(PartCheck.From(kept[0])).ToList(), Day.AddDays(3)));
    }

    // --- Fiche du matériel ---

    private static string Card(string updated = GoodDate, string parts = """[{"Kind":"Screen","Key":"A","Name":"A","Count":1,"FirstSeen":"2026-06-01T12:00:00+00:00"}]""", string changes = "[]") =>
        $$"""[{"CreatedAt":"{{GoodDate}}","UpdatedAt":"{{updated}}","Parts":{{parts}},"Changes":{{changes}},"UnreadKinds":[]}]""";

    [Fact]
    public void The_last_possible_date_does_not_stop_the_card_from_being_written()
    {
        var path = PathOf("hardware-card.json");
        File.WriteAllText(path, Card("9999-12-31T23:59:59.9999999+00:00"));
        var store = new PartsCardStore(path, NoWait);

        Assert.Null(store.Load());

        Assert.NotNull(store.Update([new HardwarePart(PartKind.Screen, "A", "A")], Day));
        Assert.Single(Copies());
    }

    [Theory]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","Count":1,"FirstSeen":"2026-06-01T12:00:00+00:00","MissedReadings":2147483647}]""")]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","Count":1,"FirstSeen":"2026-06-01T12:00:00+00:00","MissedReadings":-1}]""")]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","Count":2147483647,"FirstSeen":"2026-06-01T12:00:00+00:00"}]""")]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","SizeBytes":-1,"Count":1,"FirstSeen":"2026-06-01T12:00:00+00:00"}]""")]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","Count":1,"FirstSeen":"0001-01-01T00:00:00+00:00"}]""")]
    [InlineData("""[{"Kind":"Screen","Key":"A","Name":"A","Count":1,"FirstSeen":"2026-06-01T12:00:00+00:00","MissingSince":"9999-12-31T00:00:00+00:00"}]""")]
    [InlineData("""[null]""")]
    public void Parts_that_MAUS_cannot_have_written_make_the_card_damaged(string parts)
    {
        var path = PathOf("hardware-card.json");
        File.WriteAllText(path, Card(parts: parts));

        Assert.Null(new PartsCardStore(path, NoWait).Load());

        Assert.NotNull(new PartsCardStore(path, NoWait).Update([new HardwarePart(PartKind.Screen, "A", "A"), new HardwarePart(PartKind.Disk, "B", "B")], Day));
        Assert.Single(Copies());
    }

    [Fact]
    public void A_count_change_without_its_counts_is_not_shown_as_zero_instead_of_zero()
    {
        var path = PathOf("hardware-card.json");
        File.WriteAllText(path, Card(changes: $$"""[{"Change":"CountChanged","Kind":"Screen","Key":"A","Name":"A","At":"{{GoodDate}}"}]"""));

        var card = new PartsCardStore(path, NoWait).Load();

        Assert.Empty(NewPartView.Build(card, [], null));
    }

    [Fact]
    public void A_change_dated_in_the_future_is_not_shown()
    {
        var path = PathOf("hardware-card.json");
        File.WriteAllText(path, Card(changes: """[{"Change":"Added","Kind":"Screen","Key":"A","Name":"A","At":"2090-01-01T00:00:00+00:00"}]"""));

        Assert.Empty(NewPartView.Build(new PartsCardStore(path, NoWait).Load(), [], null));
    }

    [Fact]
    public void A_card_with_too_many_parts_is_not_read()
    {
        var path = PathOf("hardware-card.json");
        var parts = Enumerable.Range(0, 100_000).Select(i => $$"""{"Kind":"Disk","Key":"K{{i}}","Name":"N","Count":1,"FirstSeen":"{{GoodDate}}"}""");
        File.WriteAllText(path, Card(parts: "[" + string.Join(',', parts) + "]"));

        Assert.Null(new PartsCardStore(path, NoWait).Load());
    }

    [Fact]
    public void Long_names_and_control_characters_are_cleaned_on_the_card()
    {
        var path = PathOf("hardware-card.json");
        var part = $$"""{"Kind":"Screen","Key":"A","Name":{{Json(Dirty)}},"Count":1,"FirstSeen":"{{GoodDate}}"}""";
        var change = $$"""{"Change":"Added","Kind":"Screen","Key":"A","Name":{{Json(Dirty)}},"At":"{{GoodDate}}"}""";
        File.WriteAllText(path, Card(parts: "[" + part + "]", changes: "[" + change + "]"));

        var card = new PartsCardStore(path, NoWait).Load();

        var view = Assert.Single(NewPartView.Build(card, [], null));
        Assert.True(view.Title.Length < 300);
        AssertPrintable(view.Title);
    }

    [Fact]
    public void A_control_character_read_from_Windows_does_not_damage_the_next_reading()
    {
        var path = PathOf("hardware-card.json");
        var store = new PartsCardStore(path, NoWait);

        store.Update([new HardwarePart(PartKind.Screen, "A\u0007", "Écran\u0007 A")], Day);

        Assert.Equal("Écran A", Assert.Single(store.Load()!.Parts).Name.Replace("  ", " ", StringComparison.Ordinal));
        Assert.Empty(Copies());
    }

    [Fact]
    public void The_most_recent_card_wins_whatever_the_order_in_the_file()
    {
        var path = PathOf("hardware-card.json");
        var newer = Card(LaterDate)[1..^1];
        var older = Card(GoodDate)[1..^1];
        File.WriteAllText(path, "[" + newer + "," + older + "]");

        Assert.Equal(DateTimeOffset.Parse(LaterDate, CultureInfo.InvariantCulture), new PartsCardStore(path, NoWait).Load()!.UpdatedAt);
    }

    // --- Passes du benchmark ---

    [Fact]
    public void A_benchmark_pass_with_empty_values_can_be_compared_and_shared()
    {
        var path = PathOf("benchmark-history.json");
        File.WriteAllText(path, $$"""
            [{"date":"{{GoodDate}}","version":null,"api":null,"gpu":null,"cpu":null,"renderResolution":null,"displayResolution":null,"tests":null,"completed":true,"gpuScore":100,"cpuScore":100,"overallScore":100,"zzz":{"a":1} },
             {"date":"{{LaterDate}}","version":"zzz","tests":[null,{"id":null,"device":null,"unit":null,"value":1,"score":1},{"id":"a","device":"gpu","unit":"fps","value":1,"score":10}],"completed":true,"overallScore":50}]
            """);

        var runs = new BenchmarkHistoryStore(path, NoWait).Load();

        Assert.Equal(2, runs.Count);
        Assert.Single(runs[1].Tests);
        Assert.NotEmpty(BenchmarkComparison.Compare(runs[0], runs[1]));
        Assert.NotEmpty(BenchmarkComparison.Differences(runs[0], runs[1]));
        Assert.Equal(2, BenchmarkComparison.Ranking(runs).Count);
        Assert.NotNull(BenchmarkShareCode.Decode(BenchmarkShareCode.Encode(runs[0])));
        Assert.Equal(0, BenchmarkScoring.EditionOf(runs[0]));
    }

    [Theory]
    [InlineData("\"overallScore\":1e999")]
    [InlineData("\"gpuScore\":-1e999")]
    [InlineData("\"cpuScore\":1e308")]
    [InlineData("\"rayTracingScore\":1e999")]
    [InlineData("\"overallScore\":-5")]
    [InlineData("\"threads\":-1")]
    [InlineData("\"edition\":2147483647")]
    [InlineData("\"tests\":[{\"id\":\"a\",\"device\":\"gpu\",\"unit\":\"fps\",\"value\":1e999,\"score\":1}]")]
    [InlineData("\"tests\":[{\"id\":\"a\",\"device\":\"gpu\",\"unit\":\"fps\",\"value\":1,\"score\":1e999}]")]
    [InlineData("\"tests\":[{\"id\":\"a\",\"device\":\"gpu\",\"unit\":\"fps\",\"value\":1,\"score\":1,\"low1\":1e999}]")]
    public void A_benchmark_pass_with_an_impossible_number_is_not_shown(string field)
    {
        var path = PathOf("benchmark-history.json");
        File.WriteAllText(path, $$"""[{"date":"{{GoodDate}}","completed":true,{{field}} }]""");

        Assert.Empty(new BenchmarkHistoryStore(path, NoWait).Load());
    }

    [Fact]
    public void An_impossible_sensor_reading_is_dropped_but_the_pass_stays()
    {
        var path = PathOf("benchmark-history.json");
        File.WriteAllText(path, $$"""[{"date":"{{GoodDate}}","completed":true,"overallScore":10,"tests":[{"id":"a","device":"gpu","unit":"fps","value":1,"score":1,"sensors":{"samples":3,"maxTemperatureC":1e999,"averageClockMhz":-1e999,"averagePowerWatts":1e308,"thermalSlowdownPercent":1e999,"hardwareBrakePercent":1e999} }]}]""");

        var sensors = Assert.Single(Assert.Single(new BenchmarkHistoryStore(path, NoWait).Load()).Tests).Sensors!;

        Assert.Null(sensors.MaxTemperatureC);
        Assert.Null(sensors.AverageClockMhz);
        Assert.Null(sensors.AveragePowerWatts);
        Assert.Null(sensors.ThermalSlowdownPercent);
        Assert.Null(sensors.HardwareBrakePercent);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("2090-01-01T00:00:00+00:00")]
    public void A_benchmark_pass_with_an_impossible_date_is_not_ranked(string date)
    {
        var path = PathOf("benchmark-history.json");
        File.WriteAllText(path, $$"""[{"date":"{{date}}","completed":true,"overallScore":99999}]""");

        Assert.Empty(new BenchmarkHistoryStore(path, NoWait).Load());
    }

    [Fact]
    public void Benchmark_texts_are_cleaned_when_read()
    {
        var path = PathOf("benchmark-history.json");
        File.WriteAllText(path, $$"""[{"date":"{{GoodDate}}","completed":true,"gpu":{{Json(Dirty)}},"cpu":{{Json(Dirty)}},"error":{{Json(Dirty)}} }]""");

        var run = Assert.Single(new BenchmarkHistoryStore(path, NoWait).Load());

        foreach (var text in new[] { run.Gpu, run.Cpu, run.Error! })
        {
            Assert.True(text.Length < 1000);
            AssertPrintable(text);
        }
    }

    [Fact]
    public void The_image_of_a_pass_hidden_for_its_date_is_not_deleted()
    {
        var store = new BenchmarkHistoryStore(PathOf("benchmark-history.json"), NoWait);
        Directory.CreateDirectory(store.ImagesFolder);
        var image = Path.Combine(store.ImagesFolder, "MAUS-benchmark-1.png");
        File.WriteAllText(image, "x");
        File.WriteAllText(store.FilePath, $$"""[{"date":"2090-01-01T00:00:00+00:00","completed":true,"image":{{Json(image)}} }]""");

        store.PruneImages();

        Assert.True(File.Exists(image));
    }

    [Fact]
    public void An_invalid_image_path_is_neither_opened_nor_fatal()
    {
        var store = new BenchmarkHistoryStore(PathOf("benchmark-history.json"), NoWait);
        Directory.CreateDirectory(store.ImagesFolder);
        File.WriteAllText(store.FilePath, $$"""[{"date":"{{GoodDate}}","completed":true,"image":{{Json("a\0b" + new string('x', 70_000))}}}]""");

        store.PruneImages();

        Assert.False(store.IsOwnImage(Assert.Single(store.Load()).Image!));
    }

    // --- Scores et mesures gardés ---

    [Theory]
    [InlineData("""{"Kind":null,"Score":1,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":1e999,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":-1e999,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":1e308,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":-3,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"health-v2","Score":150,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"measure:x:wear","Score":5000,"Stable":true,"At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":1,"Stable":true,"At":"0001-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":1,"Stable":true,"At":"2090-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"Kind":"ram","Score":1,"Stable":true}""")]
    [InlineData("""{}""")]
    public void An_impossible_score_or_measure_is_not_shown(string line)
    {
        var path = PathOf("benchmarks.json");
        File.WriteAllText(path, "[" + line + "]");

        Assert.Empty(new BenchmarkHistory(path, NoWait).Load());
    }

    [Fact]
    public void A_cleaned_history_still_feeds_the_curves()
    {
        var path = PathOf("benchmarks.json");
        File.WriteAllText(path, $$"""
            [null,{"Kind":"ram","Score":10,"Stable":true,"At":"{{GoodDate}}","Detail":{{Json(Dirty)}},"Zzz":[1]},
             {"Kind":"ram","Score":1e999,"Stable":true,"At":"{{LaterDate}}"},
             {"Kind":"ram","Score":20,"Stable":true,"At":"{{LaterDate}}"}]
            """);

        var entries = new BenchmarkHistory(path, NoWait).Load();
        var series = ScoreTrends.Series(entries, "ram");

        Assert.Equal(2, series.Count);
        AssertPrintable(series[0].Detail!);
        Assert.True(series[0].Detail!.Length < 1000);
        AssertHonest([ScoreTrends.Describe(series)]);
        Assert.Equal(100, BenchmarkHistory.CompareToPrevious(entries, new BenchmarkEntry("ram", 30, true, Day.AddDays(5))));
    }

    // --- Codes de partage ---

    private static string ShareCode(string json)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(json));
        }

        return BenchmarkShareCode.Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"tests":null}""")]
    [InlineData("""{"tests":{"a":1}}""")]
    [InlineData("""{"tests":[null]}""")]
    [InlineData("""{"tests":[{"id":"a","device":"zzz"}]}""")]
    [InlineData("""{"tests":[{"id":null,"device":"gpu"}]}""")]
    [InlineData("""{"tests":[],"gpuScore":1e999}""")]
    [InlineData("""{"tests":[],"cpuScore":-1}""")]
    [InlineData("""{"tests":[],"overallScore":1e308}""")]
    [InlineData("""{"tests":[],"threads":-1}""")]
    [InlineData("""{"tests":[],"rayTracingScore":1e999}""")]
    [InlineData("""{"tests":[{"id":"a","device":"gpu","value":1e999,"score":1}]}""")]
    [InlineData("""{"tests":[],"date":"2026-13-45"}""")]
    [InlineData("""{"tests":[],"gpu":"\ud800"}""")]
    [InlineData("""{"tests":[],"gpu":{"a":1}}""")]
    public void A_damaged_share_code_is_refused(string json)
    {
        Assert.Null(BenchmarkShareCode.Decode("Salut ! " + ShareCode(json) + " à bientôt"));
    }

    [Theory]
    [InlineData("MAUS-BENCH-1:")]
    [InlineData("MAUS-BENCH-1:AAAA")]
    [InlineData("MAUS-BENCH-1:!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("MAUS-BENCH-1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("MAUS-BENCH-1:A-A_A-A_A-A_A-A_A")]
    [InlineData("\0\0\0")]
    public void A_share_code_that_is_not_one_is_refused(string text)
    {
        Assert.Null(BenchmarkShareCode.Decode(text));
    }

    [Fact]
    public void A_truncated_share_code_is_refused()
    {
        var code = BenchmarkShareCode.Encode(new BenchmarkReport { Date = Day, Gpu = "RTX", Cpu = "i7", Completed = true, GpuScore = 1000, CpuScore = 1000 });

        foreach (var length in new[] { code.Length / 2, BenchmarkShareCode.Prefix.Length + 16 })
        {
            Assert.Null(BenchmarkShareCode.Decode(code[..length]));
        }
    }

    [Fact]
    public void A_share_code_that_inflates_too_much_is_refused()
    {
        Assert.Null(BenchmarkShareCode.Decode(ShareCode("""{"tests":[],"zzz":" """ + new string(' ', 1_000_000) + "\"}")));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("9999-12-31T23:59:59+00:00")]
    public void A_share_code_with_an_impossible_date_is_refused(string date)
    {
        Assert.Null(BenchmarkShareCode.Decode(ShareCode($$"""{"tests":[],"date":"{{date}}","gpuScore":1000,"cpuScore":1000,"overallScore":1000,"completed":true}""")));
    }

    [Fact]
    public void A_share_code_without_a_date_is_refused()
    {
        Assert.Null(BenchmarkShareCode.Decode(ShareCode("""{"tests":[],"gpuScore":1000,"cpuScore":1000,"overallScore":1000,"completed":true}""")));
    }

    [Fact]
    public void Share_code_texts_lose_control_and_direction_characters()
    {
        var dirty = Json(new string('x', 5000) + "\u0000\u202e");
        var code = ShareCode($$"""{"tests":[],"date":"{{GoodDate}}","version":{{dirty}},"gpu":"RTX\u202Eevil\u0007","cpu":{{dirty}},"api":null,"renderResolution":null,"zzz":[null],"completed":true}""");

        var other = BenchmarkShareCode.Decode(code)!;

        Assert.Equal("RTXevil", other.Gpu);
        Assert.True(other.Cpu.Length <= 200);
        AssertPrintable(other.Cpu);
        AssertPrintable(other.Version);
        var mine = new BenchmarkReport { Date = Day, Completed = true, GpuScore = 1 };
        Assert.NotEmpty(BenchmarkComparison.Compare(mine, other));
        Assert.NotEmpty(BenchmarkComparison.Differences(mine, other));
    }

    // --- Choix de l'utilisateur ---

    private sealed class Protector : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => Directory.CreateDirectory(directory);

        public bool IsTrusted(string file) => true;

        public void ProtectFile(string file)
        {
        }
    }

    private FilePreferencesStore Preferences() => new(_folder, new Protector(), NoWait);

    private string PreferencesPath => PathOf("preferences.json");

    private static readonly Finding AFinding = new() { Id = "M01.a", Title = "t", Status = FindingStatus.Warning, Explanation = "x", Current = "v" };

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\0\0")]
    [InlineData("{\"theme\":")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"texte\"")]
    [InlineData("{\"acknowledged\":{\"a\":1}}")]
    [InlineData("{\"skippedModules\":42}")]
    [InlineData("{\"uiZoom\":\"grand\"}")]
    [InlineData("{\"lastFollowVisit\":\"2026-13-45\"}")]
    [InlineData("{\"language\":\"\\ud800\"}")]
    public void Broken_preferences_give_the_defaults_and_are_copied_before_being_replaced(string content)
    {
        File.WriteAllText(PreferencesPath, content);
        var store = Preferences();

        Assert.Equal(UserPreferences.Default, store.Load());
        Assert.Equal(PreferencesFileState.Damaged, store.State);
        Assert.Equal(content, File.ReadAllText(PreferencesPath));

        store.Save(UserPreferences.Default with { MonitorOnTop = true });

        Assert.Equal(content, File.ReadAllText(Assert.Single(Copies())));
        Assert.True(store.Load().MonitorOnTop);
    }

    [Fact]
    public void Extreme_numbers_in_the_preferences_stay_within_what_is_used()
    {
        File.WriteAllText(PreferencesPath, """
            {"uiZoom":1e999,"overlayScale":-1e999,"overlayOpacity":1e308,"refreshMilliseconds":-1,"lastTab":2147483647,"lastWorkshopSection":-2147483648,"gameBarProfile":999,"disclaimerAccepted":-1,
             "mainWindow":{"left":1e999,"top":-1e999,"width":1e308,"height":-1,"maximized":true},"Zzz":[null,1e999]}
            """);

        var preferences = Preferences().Load();

        Assert.Equal(1, preferences.UiZoomUsed);
        Assert.Equal(1, preferences.OverlayScaleUsed);
        Assert.Equal(1, preferences.OverlayOpacityUsed);
        Assert.Equal(TimeSpan.FromSeconds(1), preferences.RefreshInterval);
        Assert.Equal(800, WindowPlacement.FitSize(-1, 800, 1920));
        Assert.Equal(1920, WindowPlacement.FitSize(preferences.MainWindow!.Width, 800, 1920));
    }

    [Theory]
    [InlineData("{\"theme\":999}")]
    [InlineData("{\"theme\":\"Zzz\"}")]
    [InlineData("{\"laptopPower\":-1}")]
    [InlineData("{\"theme\":{\"a\":1}}")]
    public void An_unknown_choice_takes_its_default_and_keeps_the_rest(string content)
    {
        File.WriteAllText(PreferencesPath, content.Insert(1, "\"monitorOnTop\":true,"));
        var store = Preferences();

        var preferences = store.Load();

        Assert.True(preferences.MonitorOnTop);
        Assert.True(Enum.IsDefined(preferences.Theme));
        Assert.True(Enum.IsDefined(preferences.LaptopPower));
        Assert.Equal(PreferencesFileState.Damaged, store.State);
    }

    [Fact]
    public void An_unknown_choice_beside_an_emptied_list_does_not_bring_the_empty_list_back()
    {
        File.WriteAllText(PreferencesPath, """{"theme":999,"acknowledged":null,"skippedModules":[null],"keptRefreshRates":null}""");
        var store = Preferences();

        var preferences = store.Load();

        Assert.Equal(PreferencesFileState.Damaged, store.State);
        Assert.Empty(preferences.Acknowledged);
        Assert.Empty(preferences.SkippedModules);
        Assert.Empty(preferences.KeptRefreshRates);
        Assert.Null(preferences.AcknowledgementFor(AFinding));
    }

    [Theory]
    [InlineData("""{"findingId":null,"current":"v","at":"2026-06-01T12:00:00+00:00","language":"zz"}""")]
    [InlineData("""{"findingId":"","current":"v","at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"Current":"v","At":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"findingId":"M01.a","current":"v","at":"0001-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"findingId":"M01.a","current":"v","at":"9999-12-31T00:00:00+00:00"}""")]
    [InlineData("""{"findingId":"M01.a","current":"v"}""")]
    [InlineData("""null""")]
    public void A_broken_mark_is_left_out_and_never_hides_a_finding(string mark)
    {
        File.WriteAllText(PreferencesPath, "{\"acknowledged\":[" + mark + "]}");
        var store = Preferences();

        var preferences = store.Load();

        Assert.Equal(PreferencesFileState.Damaged, store.State);
        Assert.Empty(preferences.Acknowledged);
        Assert.Null(preferences.AcknowledgementFor(AFinding));
        Assert.Empty(preferences.Unacknowledge("M01.a").Acknowledged);
        Assert.Same(preferences, preferences.RebindLanguage([AFinding]));
        Assert.Equal(FindingStatus.Warning, Assert.Single(Acknowledgements.Apply(new ModuleResult("M01", "M", [AFinding], TimeSpan.Zero), preferences).Findings).Status);

        store.Save(preferences);
        Assert.Single(Copies());
    }

    [Theory]
    [InlineData("""{"sourceName":null,"label":"L","width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":null,"width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L","width":-1,"height":1080,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L","width":1920,"height":2147483647,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L","width":1920,"height":1080,"fromHz":0,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L","width":1920,"height":1080,"fromHz":60,"toHz":999999,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L","width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"0001-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S\u0007","label":"L","width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""{"sourceName":"S","label":"L\u202E","width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"2026-06-01T12:00:00+00:00"}""")]
    [InlineData("""null""")]
    public void A_broken_refresh_rate_cannot_be_undone(string kept)
    {
        File.WriteAllText(PreferencesPath, "{\"keptRefreshRates\":[" + kept + "]}");
        var store = Preferences();

        Assert.Empty(store.Load().KeptRefreshRates);
        Assert.Equal(PreferencesFileState.Damaged, store.State);
    }

    [Fact]
    public void A_very_long_refresh_label_is_not_kept()
    {
        File.WriteAllText(PreferencesPath, $$"""{"keptRefreshRates":[{"sourceName":"S","label":{{Json(new string('x', 100_000))}},"width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"{{GoodDate}}" }]}""");

        Assert.Empty(Preferences().Load().KeptRefreshRates);
    }

    [Fact]
    public void Good_marks_and_refresh_rates_stay_beside_broken_ones()
    {
        File.WriteAllText(PreferencesPath, $$"""
            {"acknowledged":[null,{"findingId":null,"at":"{{GoodDate}}"},{"findingId":"M01.a","current":"v","at":"{{GoodDate}}"}],
             "skippedModules":[null,"M02"],
             "keptRefreshRates":[{"sourceName":"S","label":"L","width":1920,"height":1080,"fromHz":60,"toHz":144,"at":"{{GoodDate}}"},{"sourceName":null}]}
            """);

        var preferences = Preferences().Load();

        Assert.NotNull(preferences.AcknowledgementFor(AFinding));
        Assert.Equal(["M02"], preferences.SkippedModules);
        Assert.Equal("S", Assert.Single(preferences.KeptRefreshRates).SourceName);
    }

    [Fact]
    public void Unknown_keys_are_kept_without_a_copy()
    {
        File.WriteAllText(PreferencesPath, """{"Zzz":{"a":[1,null,"x"]},"monitorOnTop":true}""");
        var store = Preferences();

        var preferences = store.Load();
        Assert.Equal(PreferencesFileState.Read, store.State);

        store.Save(preferences);

        Assert.Empty(Copies());
        Assert.Contains("\"Zzz\"", File.ReadAllText(PreferencesPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_far_too_big_are_not_read_and_not_replaced()
    {
        const long size = 65L * 1024 * 1024;
        using (var file = File.Create(PreferencesPath))
        {
            file.SetLength(size);
        }

        var store = Preferences();

        Assert.Equal(UserPreferences.Default, store.Load());
        Assert.Equal(PreferencesFileState.Unavailable, store.State);
        Assert.Throws<PreferencesNotSavedException>(() => store.Save(UserPreferences.Default));
        Assert.Equal(size, new FileInfo(PreferencesPath).Length);
    }

    [Fact]
    public void Locked_preferences_are_not_replaced()
    {
        File.WriteAllText(PreferencesPath, "{\"monitorOnTop\":true}");
        var store = Preferences();

        using (new FileStream(PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(UserPreferences.Default, store.Load());
            Assert.Equal(PreferencesFileState.Unavailable, store.State);
            Assert.Throws<PreferencesNotSavedException>(() => store.Save(UserPreferences.Default));
        }

        Assert.Equal("{\"monitorOnTop\":true}", File.ReadAllText(PreferencesPath));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("2090-01-01T00:00:00+00:00")]
    public void An_impossible_last_visit_counts_as_no_visit(string date)
    {
        File.WriteAllText(PreferencesPath, "{\"lastFollowVisit\":\"" + date + "\"}");
        var visit = Preferences().Load().LastFollowVisit;
        var weekly = new AuditSnapshot(Day, "b", 80, [], Weekly: true);

        var summary = QuietWeek.Build([weekly], visit, new Maus.Core.Engine.Broker.ScheduleState(DayOfWeek.Monday, null), Day.AddDays(1));

        Assert.DoesNotContain("dernière visite", summary.Headline, StringComparison.Ordinal);
    }
}
