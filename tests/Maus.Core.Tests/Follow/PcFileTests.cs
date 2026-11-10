using Maus.Core.Follow;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Follow;

public class PcFileTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly WindowsInfo Windows = new("Windows 11 Pro", "Professional", "25H2", 26200, 1000);

    private static PrivacyFilter Privacy() => new("Alex", "BUREAU-ALEX", @"C:\Users\Alex");

    private static HardwareInventory Inventory(IReadOnlyList<DiskIdentity>? disks = null, IReadOnlyList<BatteryIdentity>? batteries = null) => new(
        new CpuIdentity("Ryzen 7 d'essai", "AMD", null, 8, 16, 3800, null, null, null, [], false, false, false),
        new BoardIdentity("Carte", "X670", "AMI", "1.20", new DateTime(2025, 1, 2)),
        [],
        [],
        disks ?? [],
        batteries ?? []);

    private static PcFileInput Input(HardwareInventory? inventory = null) => new() { At = At, Inventory = inventory ?? Inventory(), Windows = Windows };

    [Fact]
    public void The_page_says_it_is_not_certified()
    {
        var document = PcFile.Build(Input(), Privacy());

        Assert.Contains("Relevé par MAUS, non certifié", document.ToHtml(), StringComparison.Ordinal);
        Assert.Contains("Relevé par MAUS, non certifié", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_that_identifies_the_person_or_the_pc_reaches_the_page()
    {
        var disk = new DiskIdentity(@"Disque d'Alex, serial number : WD-ABC123 C:\Users\Alex alex@exemple.fr", "NVMe", "SSD", 1L << 40, "AA:BB:CC:DD:EE:FF", null, null, null);
        var battery = new BatteryIdentity("BUREAU-ALEX", "S/N: XYZ789", 50000, 40000, null);

        var document = PcFile.Build(Input(Inventory([disk], [battery])), Privacy());

        foreach (var output in new[] { document.ToHtml(), document.ToText() })
        {
            foreach (var secret in new[] { "Alex", "ALEX", "WD-ABC123", "XYZ789", "AA:BB:CC:DD:EE:FF", "exemple.fr" })
            {
                Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
            }
        }

        Assert.Contains("numéro de série", document.MaskingNote, StringComparison.Ordinal);
        Assert.Contains("adresse MAC", document.MaskingNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(DiskIdentity))]
    [InlineData(typeof(BatteryIdentity))]
    [InlineData(typeof(MemoryModuleInfo))]
    [InlineData(typeof(BoardIdentity))]
    [InlineData(typeof(NetworkAdapterIdentity))]
    [InlineData(typeof(SystemIdentity))]
    public void The_records_it_reads_carry_no_serial_number_nor_mac_address(Type record)
    {
        foreach (var property in record.GetProperties())
        {
            Assert.DoesNotContain("Serial", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mac", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("MAC : 00-1A-2B-3C-4D-5E.", "MAC : [adresse-mac].")]
    [InlineData("Numéro de série : ABC-123, modèle X", "Numéro de série : [numéro-de-série], modèle X")]
    [InlineData("SerialNumber=ABC123", "SerialNumber=[numéro-de-série]")]
    [InlineData("Ce rapport ne contient ni numéro de série.", "Ce rapport ne contient ni numéro de série.")]
    [InlineData("Micrologiciel 1A:2B", "Micrologiciel 1A:2B")]
    [InlineData("Référence CMK32GX5M2B6000C30", "Référence CMK32GX5M2B6000C30")]
    public void The_filter_masks_mac_addresses_and_announced_serial_numbers(string text, string expected) =>
        Assert.Equal(expected, new PrivacyFilter(null, null, null).Mask(text));

    [Fact]
    public void Every_value_is_encoded_in_the_page()
    {
        var disk = new DiskIdentity("<script>alert('x')</script>", null, null, null, null, null, null, null);

        var html = PcFile.Build(Input(Inventory([disk])), Privacy()).ToHtml();

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_not_read_stays_not_read()
    {
        var disk = new DiskIdentity("Disque d'essai", "SATA", "SSD", null, null, null, null, null);
        var input = new PcFileInput { At = At, Inventory = Inventory([disk]), BatteryDetected = true };

        var text = PcFile.Build(input, Privacy()).ToText();

        Assert.Contains("Usure : non lu sans droits", text, StringComparison.Ordinal);
        Assert.Contains("Version de Windows : non lu", text, StringComparison.Ordinal);
        Assert.Contains("Capacité restante : non lu sans droits", text, StringComparison.Ordinal);
        Assert.Contains("État de santé : non lu", text, StringComparison.Ordinal);
        Assert.Contains("aucun score gardé", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sain", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_rights_what_was_not_read_does_not_blame_the_rights()
    {
        var disk = new DiskIdentity("Disque d'essai", "SATA", "SSD", null, null, null, null, null);

        var text = PcFile.Build(Input(Inventory([disk])) with { ReadWithRights = true }, Privacy()).ToText();

        Assert.Contains("Usure : non lu", text, StringComparison.Ordinal);
        Assert.DoesNotContain("non lu sans droits", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spinning_disk_shows_no_wear_figure()
    {
        var disk = new DiskIdentity("Disque dur", "SATA", "HDD", null, null, null, 0, null);

        var text = PcFile.Build(Input(Inventory([disk])), Privacy()).ToText();

        Assert.Contains("Usure : sans objet pour un disque dur", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_desktop_without_a_battery_says_only_what_windows_announces()
    {
        var text = PcFile.Build(Input(), Privacy()).ToText();

        Assert.Contains("aucune annoncée par Windows", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_battery_is_shown_as_read()
    {
        var battery = new BatteryIdentity("Pile", "Fabricant", 50000, 40000, null, 321);

        var text = PcFile.Build(Input(Inventory(batteries: [battery])), Privacy()).ToText();

        Assert.Contains("80 % de la capacité d'origine", text, StringComparison.Ordinal);
        Assert.Contains("Cycles de charge : 321", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_score_keeps_its_date_and_a_run_without_rights_gives_none()
    {
        var older = new AuditSnapshot(At.AddDays(-9), "1", 72, []);
        var weekly = new AuditSnapshot(At.AddDays(-1), "1", AuditSnapshot.NoScore,
            [new("M11.disk-0-health", "M11", "Santé du disque Essai", FindingStatus.Ok)], Weekly: true);

        var text = PcFile.Build(Input() with { Audits = [older, weekly] }, Privacy()).ToText();

        Assert.Contains("72/100", text, StringComparison.Ordinal);
        Assert.Contains(older.At.ToLocalTime().ToString("d", Maus.Core.Localization.Texts.Culture), text, StringComparison.Ordinal);
        Assert.Contains("Santé du disque Essai : " + Labels.Of(FindingStatus.Ok), text, StringComparison.Ordinal);
        Assert.Contains("fait sans droits administrateur", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_score_is_not_shown()
    {
        var text = PcFile.Build(Input() with { Audits = [new AuditSnapshot(At, "1", 4000, [])] }, Privacy()).ToText();

        Assert.DoesNotContain("4000", text, StringComparison.Ordinal);
        Assert.Contains("aucun score gardé", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_could_not_be_read_stays_not_read()
    {
        var audit = new AuditSnapshot(At, "1", 80, [new("M11.disk-reliability", "M11", "Usure des disques", FindingStatus.Unknown)]);

        var text = PcFile.Build(Input() with { Audits = [audit] }, Privacy()).ToText();

        Assert.Contains("Usure des disques : non lu", text, StringComparison.Ordinal);
    }
}
