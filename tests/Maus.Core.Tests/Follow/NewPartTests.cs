using Maus.Core.Follow;
using Maus.Core.Hardware;
using Maus.Core.Modules.M14Display;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Follow;

public sealed class NewPartTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private static readonly DateTimeOffset Day3 = Day1.AddDays(2);

    private readonly string _folder = Directory.CreateTempSubdirectory("maus-part-").FullName;

    private string FilePath => Path.Combine(_folder, "hardware-card.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HardwarePart Screen(string name) => new(PartKind.Screen, name, name);

    private static HardwarePart Disk(string name) => new(PartKind.Disk, name, name, 1L << 40);

    [Fact]
    public void The_first_reading_creates_the_card_and_announces_nothing()
    {
        var card = PartsCard.Apply(null, [Screen("A"), Disk("D")], Day1)!;

        Assert.Empty(card.Changes);
        Assert.Equal(2, card.Parts.Count);
        Assert.Equal(Day1, card.CreatedAt);
        Assert.Contains(PartKind.Memory, card.UnreadKinds);
        Assert.Empty(NewPartView.Build(card, [], null));
    }

    [Fact]
    public void Nothing_read_and_no_card_stays_no_card() => Assert.Null(PartsCard.Apply(null, [], Day1));

    [Fact]
    public void A_new_part_is_announced_with_its_date()
    {
        var card = PartsCard.Apply(PartsCard.Apply(null, [Screen("A")], Day1), [Screen("A"), Screen("B")], Day2)!;

        var change = Assert.Single(card.Changes);
        Assert.Equal((PartChangeKind.Added, "B", Day2), (change.Change, change.Name, change.At));
    }

    [Fact]
    public void An_empty_reading_is_not_a_removal()
    {
        var first = PartsCard.Apply(null, [Screen("A"), Disk("D")], Day1);

        var card = PartsCard.Apply(PartsCard.Apply(first, [], Day2), [Disk("D")], Day3)!;

        Assert.Empty(card.Changes);
        Assert.Contains(card.Parts, p => p.Name == "A" && p.MissingSince is null);
        Assert.Equal([PartKind.Screen, PartKind.Memory, PartKind.Graphics], card.UnreadKinds);
    }

    [Fact]
    public void The_first_reading_of_a_kind_announces_nothing_even_on_an_old_card()
    {
        var card = PartsCard.Apply(PartsCard.Apply(null, [Screen("A")], Day1), [Screen("A"), Disk("D")], Day2)!;

        Assert.Empty(card.Changes);
    }

    [Fact]
    public void A_part_missing_once_is_not_announced_and_twice_is_dated_from_the_first_time()
    {
        var first = PartsCard.Apply(null, [Screen("A"), Screen("B")], Day1);

        var once = PartsCard.Apply(first, [Screen("A")], Day2)!;
        Assert.Empty(once.Changes);

        var twice = PartsCard.Apply(once, [Screen("A")], Day3)!;
        var change = Assert.Single(twice.Changes);
        Assert.Equal((PartChangeKind.NoLongerSeen, Day2), (change.Change, change.At));

        var view = Assert.Single(NewPartView.Build(twice, [], null));
        Assert.False(view.NeedsAudit);
        Assert.Contains("n'est pas un problème", view.When, StringComparison.Ordinal);

        // Un écran encore manquant n'est pas annoncé une seconde fois.
        Assert.Single(PartsCard.Apply(twice, [Screen("A")], Day3.AddDays(1))!.Changes);
    }

    [Fact]
    public void A_part_that_comes_back_is_neither_new_nor_missing()
    {
        var card = PartsCard.Apply(null, [Screen("A"), Screen("B")], Day1);
        card = PartsCard.Apply(card, [Screen("A")], Day2);
        card = PartsCard.Apply(card, [Screen("A")], Day3);
        Assert.Single(card!.Changes);

        card = PartsCard.Apply(card, [Screen("A"), Screen("B")], Day3.AddDays(1))!;

        Assert.Empty(card.Changes);
        Assert.All(card.Parts, p => Assert.Null(p.MissingSince));
    }

    [Fact]
    public void A_count_change_is_announced_and_going_back_cancels_it()
    {
        var card = PartsCard.Apply(null, [Screen("A"), Screen("A")], Day1);
        card = PartsCard.Apply(card, [Screen("A")], Day2)!;

        var change = Assert.Single(card.Changes);
        Assert.Equal((PartChangeKind.CountChanged, 2, 1), (change.Change, change.CountBefore!.Value, change.CountNow!.Value));

        Assert.Empty(PartsCard.Apply(card, [Screen("A"), Screen("A")], Day3)!.Changes);
    }

    [Fact]
    public void The_card_stays_bounded()
    {
        var card = PartsCard.Apply(null, [Screen("A")], Day1);
        for (var i = 0; i < 100; i++)
        {
            card = PartsCard.Apply(card, [Screen("A"), Screen($"E{i}")], Day1.AddHours(i + 1));
            card = PartsCard.Apply(card, [Screen("A")], Day1.AddHours(i + 1).AddMinutes(1));
            card = PartsCard.Apply(card, [Screen("A")], Day1.AddHours(i + 1).AddMinutes(2));
        }

        Assert.True(card!.Parts.Count <= PartsCard.MaxParts);
        Assert.True(card.Changes.Count <= PartsCard.MaxChanges);
        Assert.Contains(card.Parts, p => p.Name == "A");

        var longName = new string('x', 500);
        Assert.Equal(120, PartsCard.Apply(null, [Screen(longName)], Day1)!.Parts[0].Name.Length);
    }

    [Fact]
    public void Advice_only_comes_from_existing_findings_read_after_the_change()
    {
        var checks = new List<PartCheck>
        {
            new("M14.refresh.display-1", "M14", "Fréquence d'actualisation : ZX27", FindingStatus.Improvable, "60 Hz", "165 Hz (maximum en 2560×1440)"),
            new("M14.refresh.display-2", "M14", "Fréquence d'actualisation : QW32", FindingStatus.Ok, "144 Hz"),
            new("M14.hdr.display-1", "M14", "HDR : ZX27", FindingStatus.Warning),
            new("M10.xmp", "M10", "XMP / EXPO probablement désactivé", FindingStatus.Improvable, "4800 MT/s"),
            new("M09.driver.0", "M09", "Pilote à jour : RTX", FindingStatus.Ok, "560"),
            new("M09.basic-display-driver", "M09", "Pilote de base", FindingStatus.Ok),
            new("M11.disk-1-health", "M11", "Santé du disque SN850", FindingStatus.Unknown),
            new("M11.disk-2-health", "M11", "Santé du disque QVO", FindingStatus.Ok),
        };

        var screen = Assert.Single(NewPartView.Select(PartKind.Screen, "ZX27", checks));
        Assert.Equal("M14.refresh.display-1", screen.Id);
        Assert.Equal("M10.xmp", Assert.Single(NewPartView.Select(PartKind.Memory, "X", checks)).Id);
        Assert.Equal("M09.driver.0", Assert.Single(NewPartView.Select(PartKind.Graphics, "RTX", checks)).Id);
        Assert.Equal("M11.disk-1-health", Assert.Single(NewPartView.Select(PartKind.Disk, "SN850", checks)).Id);

        var card = PartsCard.Apply(PartsCard.Apply(null, [Screen("QW32")], Day1), [Screen("QW32"), Screen("ZX27")], Day2);

        // Un audit plus ancien que le changement ne dit rien de la nouvelle pièce.
        var before = Assert.Single(NewPartView.Build(card, checks, Day1));
        Assert.True(before.NeedsAudit);
        Assert.Empty(before.Checks);

        var after = Assert.Single(NewPartView.Build(card, checks, Day2));
        Assert.False(after.NeedsAudit);
        Assert.Contains("60 Hz", Assert.Single(after.Checks), StringComparison.Ordinal);
        Assert.Contains("165 Hz", after.Checks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_memory_guide_says_the_profile_is_set_in_the_bios_and_not_by_maus()
    {
        var memory = new HardwarePart(PartKind.Memory, "K", "Corsair K", 16L << 30);
        var card = PartsCard.Apply(PartsCard.Apply(null, [new HardwarePart(PartKind.Memory, "J", "J", 16L << 30)], Day1), [memory], Day2);

        var added = NewPartView.Build(card, [], null).Single(c => c.Title.Contains("Corsair", StringComparison.Ordinal));

        Assert.Contains("(16 Go)", added.Title, StringComparison.Ordinal);
        Assert.Contains("BIOS", added.Guide, StringComparison.Ordinal);
        Assert.Contains("MAUS ne le fait pas", added.Guide, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stick_without_a_reference_is_still_named_and_its_key_ignores_the_language()
    {
        var unnamed = new MemoryModuleInfo("A1", 8L << 30, null, null, null, null, null, null, null, null);
        var part = Assert.Single(PartsReader.Build([unnamed], [], [], []));

        Assert.Equal(string.Empty, part.Name);
        Assert.DoesNotContain("barrette", part.Key, StringComparison.OrdinalIgnoreCase);

        var card = PartsCard.Apply(PartsCard.Apply(null, [new HardwarePart(PartKind.Memory, "J", "J", 8L << 30)], Day1), [part], Day2);
        Assert.Contains(NewPartView.Build(card, [], null), c => c.Title.Contains("barrette de 8 Go", StringComparison.Ordinal));
    }

    [Fact]
    public void Removable_disks_and_unnamed_disks_are_not_parts()
    {
        var parts = PartsReader.Build([], [], [
            new DiskIdentity("Samsung 990", "NVMe", "SSD", 1L << 40, null, null, null, null),
            new DiskIdentity("Clé USB", "USB", null, 1L << 33, null, null, null, null, ComesAndGoes: true),
            new DiskIdentity("?", null, null, null, null, null, null, null),
        ], []);

        Assert.Equal("Samsung 990", Assert.Single(parts).Name);
    }

    [Fact]
    public void A_card_on_the_basic_driver_is_not_read_as_a_new_card()
    {
        static GpuInfo Gpu(string name, string? inf = null) => new(name, HardwareVendor.Nvidia, null, null, @"PCI\VEN_10DE&DEV_2204&SUBSYS_1&REV_A1\4&ABC", false, inf);

        Assert.Empty(PartsReader.Build([], [Gpu("Microsoft Basic Display Adapter")], [], []));
        Assert.Empty(PartsReader.Build([], [Gpu("Anything", "display.inf")], [], []));

        // Le pilote renomme la carte : c'est la même.
        var card = PartsCard.Apply(null, PartsReader.Build([], [Gpu("RTX 3090")], [], []), Day1);
        var renamed = PartsCard.Apply(card, PartsReader.Build([], [Gpu("NVIDIA GeForce RTX 3090")], [], []), Day2)!;
        Assert.Empty(renamed.Changes);
        Assert.Equal("NVIDIA GeForce RTX 3090", Assert.Single(renamed.Parts).Name);
        Assert.Equal("VEN_10DE&DEV_2204", renamed.Parts[0].Key);
    }

    [Fact]
    public void A_failed_or_denied_reading_gives_no_part_and_nothing_asks_for_a_serial_number()
    {
        var cim = new FakeCim().Throw(HardwareInventoryReader.MemoryQuery, new MausAccessDeniedException("non"));

        Assert.Empty(PartsReader.Read(cim, new Displays(null)));
        Assert.DoesNotContain("Serial", HardwareInventoryReader.MemoryQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Serial", HardwareInventoryReader.DiskQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Screens_keep_their_model_name_and_virtual_ones_are_ignored()
    {
        var parts = PartsReader.Read(new FakeCim(), new Displays([
            new DisplayPath { MonitorName = " DELL U2720Q ", Output = OutputTechnology.Hdmi },
            new DisplayPath { MonitorName = "Virtuel", Output = OutputTechnology.IndirectVirtual },
            new DisplayPath { MonitorName = string.Empty, Output = OutputTechnology.Hdmi },
        ]));

        Assert.Equal("DELL U2720Q", Assert.Single(parts).Name);
    }

    [Fact]
    public void The_store_round_trips_and_keeps_a_single_card()
    {
        var store = new PartsCardStore(FilePath);
        Assert.Null(store.Load());

        store.Update([Screen("A")], Day1);
        var card = store.Update([Screen("A"), Screen("B")], Day2)!;

        Assert.Single(card.Changes);
        var read = new PartsCardStore(FilePath).Load()!;
        Assert.Equal(card.UpdatedAt, read.UpdatedAt);
        Assert.Equal("B", Assert.Single(read.Changes).Name);
        Assert.Equal(PartChangeKind.Added, read.Changes[0].Change);
        Assert.Contains("\"Added\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clock_set_back_does_not_lose_the_latest_reading()
    {
        var store = new PartsCardStore(FilePath);
        store.Update([Screen("A")], Day2);
        store.Update([Screen("A"), Screen("B")], Day1);

        Assert.Equal(2, store.Load()!.Parts.Count);
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("[{}]")]
    [InlineData("[{\"CreatedAt\":\"2026-10-01T12:00:00+00:00\",\"UpdatedAt\":\"2026-10-01T12:00:00+00:00\",\"Parts\":[{\"Kind\":\"Screen\",\"Count\":0}],\"Changes\":[],\"UnreadKinds\":[]}]")]
    public void A_damaged_file_shows_nothing_and_the_card_starts_again_without_announcing(string content)
    {
        File.WriteAllText(FilePath, content);
        var store = new PartsCardStore(FilePath, _ => { });

        Assert.Null(store.Load());

        var card = store.Update([Screen("A")], Day1)!;
        Assert.Empty(card.Changes);
        Assert.Single(Directory.GetFiles(_folder), f => f != FilePath);
    }

    [Fact]
    public void An_old_kept_audit_only_gives_titles_and_states()
    {
        var snapshot = new AuditSnapshot(Day2, "26300", 80, [new SnapshotFinding("M10.xmp", "M10", "XMP / EXPO probablement désactivé", FindingStatus.Improvable)]);
        var card = PartsCard.Apply(PartsCard.Apply(null, [new HardwarePart(PartKind.Memory, "J", "J")], Day1), [new HardwarePart(PartKind.Memory, "J", "J"), new HardwarePart(PartKind.Memory, "K", "K")], Day2);

        var view = Assert.Single(NewPartView.Build(card, PartCheck.From(snapshot).ToList(), snapshot.At));

        Assert.Equal("À voir : XMP / EXPO probablement désactivé.", Assert.Single(view.Checks));
    }

    private sealed class Displays(IReadOnlyList<DisplayPath>? paths) : IDisplayConfigReader
    {
        public IReadOnlyList<DisplayPath>? ReadActivePaths() => paths;
    }
}
