using System.Text;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Tests.Workshop;

/// <summary>
/// Microsoft documente l'option <c>/xml</c>, pas le contenu du fichier. La forme ci-dessous reprend un vrai fichier de Windows 11
/// sorti d'un PC fixe : mêmes éléments, mêmes attributs, semaines puis jours qui se chevauchent, 0 sans batterie. Les capacités et
/// l'élément <c>Battery</c> sont inventés : AUCUN vrai rapport de portable n'a été lu, il en faut un avant de sortir cette fonction.
/// </summary>
public class BatteryHistoryTests
{
    private const string Ns = "http://schemas.microsoft.com/battery/2012";

    private static string Entry(string start, string end, int design, int full, int changed = 0) =>
        $"""<HistoryEntry StartDate="{start}Z" LocalStartDate="{start}" EndDate="{end}Z" LocalEndDate="{end}" DesignCapacity="{design}" FullChargeCapacity="{full}" CycleCount="0" ActiveAcTime="PT0S" ActiveDcTime="PT0S" CsAcTime="PT0S" CsDcTime="PT0S" ActiveDcEnergy="0" CsDcEnergy="0" EstimatedDesignActiveTime="PT0S" EstimatedFullChargeActiveTime="PT0S" EstimatedDesignCsTime="PT0S" EstimatedFullChargeCsTime="PT0S" BatteryChanged="{changed}" />""";

    private static string Report(string history, string version = "1.1", string battery = "<Battery><Id>X</Id><RelativeCapacity>0</RelativeCapacity></Battery>") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="{Ns}">
          <ReportInformation>
            <ReportGuid>c0000000-0000-0000-0000-000000000000</ReportGuid>
            <ReportVersion>{version}</ReportVersion>
            <ScanTime>2026-11-02T16:47:26Z</ScanTime>
            <LocalScanTime>2026-11-02T17:47:26</LocalScanTime>
            <ReportStartTime>2026-10-26T16:47:26Z</ReportStartTime>
            <LocalReportStartTime>2026-10-26T17:47:26</LocalReportStartTime>
            <ReportDuration>7</ReportDuration>
            <UtcOffset>+1:00</UtcOffset>
          </ReportInformation>
          <SystemInformation><ComputerName>NOM-DU-PC</ComputerName></SystemInformation>
          <Batteries>{battery}</Batteries>
          <RuntimeEstimates />
          <RecentUsage />
          <History>{history}</History>
          <EnergyDrains />
        </BatteryReport>
        """;

    private static readonly string Weeks = string.Concat(
        Entry("2026-01-03T12:37:23", "2026-01-10T12:37:23", 0, 0),
        Entry("2026-01-10T12:37:23", "2026-01-17T12:37:23", 50000, 45000),
        Entry("2026-10-24T12:00:00", "2026-10-31T12:00:00", 50000, 42000),
        Entry("2026-10-31T00:00:00", "2026-10-31T23:59:59", 50000, 41900),
        Entry("2026-11-01T00:00:00", "2026-11-01T23:59:59", 50000, 41000));

    private static BatteryHistory? Parse(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return BatteryHistoryReader.Parse(stream);
    }

    [Fact]
    public void The_start_is_the_first_period_with_a_capacity_never_the_purchase()
    {
        var history = Parse(Report(Weeks));

        Assert.NotNull(history);
        Assert.Equal(4, history.Points.Count);
        Assert.Equal(new DateTime(2026, 1, 10, 12, 37, 23), history.Since);
        Assert.Equal(90, history.Points[0].Percent);
        Assert.Equal(82, history.Points[^1].Percent);
        Assert.Contains(history.Since!.Value.ToString("d", Culture), history.Describe()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_week_overlapping_the_next_day_is_the_form_windows_writes()
    {
        // Vu dans le vrai fichier : la dernière semaine finit après le début du premier jour.
        var history = Parse(Report(Entry("2026-10-17T12:37:23", "2026-10-24T12:37:23", 100, 90) + Entry("2026-10-24T00:00:00", "2026-10-24T23:59:59", 100, 90)));

        Assert.Equal(2, history?.Points.Count);
    }

    [Fact]
    public void A_pc_without_battery_gives_no_data_and_not_an_error()
    {
        var history = Parse(Report(Entry("2026-10-03T12:37:23", "2026-10-10T12:37:23", 0, 0), battery: string.Empty));

        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Null(history.Since);
        Assert.Empty(history.Curve);
        Assert.Single(history.Describe());
    }

    [Fact]
    public void A_battery_change_reported_on_an_empty_period_stays_on_the_next_reading()
    {
        var history = Parse(Report(
            Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 70)
            + Entry("2026-01-08T00:00:00", "2026-01-15T00:00:00", 0, 0, changed: 1)
            + Entry("2026-01-15T00:00:00", "2026-01-22T00:00:00", 100, 99)));

        Assert.NotNull(history);
        Assert.False(history.Points[0].BatteryChanged);
        Assert.True(history.Points[1].BatteryChanged);
        Assert.Single(history.CurveChanges);
        Assert.InRange(history.CurveChanges[0], 1, BatteryHistory.CurveLength - 1);
    }

    [Fact]
    public void The_curve_follows_time_not_the_number_of_lines()
    {
        var history = Parse(Report(Weeks));

        Assert.NotNull(history);
        Assert.Equal(BatteryHistory.CurveLength, history.Curve.Count);
        Assert.Equal(90, history.Curve[0]);
        Assert.Equal(82, history.Curve[^1]);

        // Neuf mois à 90 %, puis quelques jours plus bas : les jours ne prennent pas la moitié de la courbe.
        Assert.True(history.Curve.Count(value => value == 90) > BatteryHistory.CurveLength * 3 / 4);
    }

    [Fact]
    public void A_single_reading_draws_no_curve()
    {
        var history = Parse(Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90)));

        Assert.NotNull(history);
        Assert.Empty(history.Curve);
        Assert.Empty(history.CurveChanges);
    }

    public static TheoryData<string> Deviations => new()
    {
        { string.Empty },
        { "pas du xml" },
        { "<html><body>Rapport</body></html>" },
        { Report(Weeks).Replace(Ns, "http://schemas.microsoft.com/battery/2099", StringComparison.Ordinal) },
        { Report(Weeks).Replace("<BatteryReport", "<Report", StringComparison.Ordinal).Replace("</BatteryReport", "</Report", StringComparison.Ordinal) },
        { Report(Weeks)[..^30] },
        { "<!DOCTYPE BatteryReport [<!ENTITY a \"b\">]>" + Report(Weeks)[Report(Weeks).IndexOf("<BatteryReport", StringComparison.Ordinal)..] },
        { Report(Weeks, version: "2.0") },
        { Report(Weeks).Replace("<ReportVersion>1.1</ReportVersion>", string.Empty, StringComparison.Ordinal) },
        { Report(Weeks).Replace("<LocalScanTime>2026-11-02T17:47:26</LocalScanTime>", string.Empty, StringComparison.Ordinal) },
        { Report(Weeks).Replace("<Batteries>", "<Autre>", StringComparison.Ordinal).Replace("</Batteries>", "</Autre>", StringComparison.Ordinal) },
        { Report(Weeks).Replace("<History>", "<Historique>", StringComparison.Ordinal).Replace("</History>", "</Historique>", StringComparison.Ordinal) },
        { Report(Weeks).Replace("<EnergyDrains />", "<History />", StringComparison.Ordinal) },
        { Report(Weeks + "<Autre />") },
        { Report(Weeks, battery: "<Battery><RelativeCapacity>1</RelativeCapacity></Battery>") },
        { Report(Weeks.Replace("DesignCapacity=", "Capacite=", StringComparison.Ordinal)) },
        { Report(Weeks.Replace("FullChargeCapacity=", "Pleine=", StringComparison.Ordinal)) },
        { Report(Weeks.Replace("LocalStartDate=", "Debut=", StringComparison.Ordinal)) },
        { Report(Weeks.Replace("LocalEndDate=", "Fin=", StringComparison.Ordinal)) },
        { Report(Weeks.Replace("BatteryChanged=", "Change=", StringComparison.Ordinal)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90, changed: 2)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, -5)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90).Replace("\"90\"", "\"90,5\"", StringComparison.Ordinal)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 201)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 2000000, 90)) },
        { Report(Entry("01/01/2026", "2026-01-08T00:00:00", 100, 90)) },
        { Report(Entry("2026-01-08T00:00:00", "2026-01-01T00:00:00", 100, 90)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-11-10T00:00:00", 100, 90)) },
        { Report(Entry("2026-02-01T00:00:00", "2026-02-08T00:00:00", 100, 90) + Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90)) },
        { Report(Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90) + Entry("2026-01-01T00:00:00", "2026-01-08T00:00:00", 100, 90)) },

        // Un élément lu pour son texte qui en contient d'autres n'est jamais lu (un fichier retouché pourrait les emboîter très profond).
        { Report(Weeks, version: "<V>1.1</V>") },
        { Report(Weeks).Replace("<LocalScanTime>2026-11-02T17:47:26</LocalScanTime>", "<LocalScanTime><T>2026-11-02T17:47:26</T></LocalScanTime>", StringComparison.Ordinal) },
        { Report(Weeks, battery: "<Battery><RelativeCapacity><X>0</X></RelativeCapacity></Battery>") },
    };

    [Theory]
    [MemberData(nameof(Deviations))]
    public void Any_deviation_gives_not_read(string xml) => Assert.Null(Parse(xml));

    [Fact]
    public void A_report_dated_at_the_end_of_time_never_throws()
    {
        var xml = Report(Weeks + Entry("9999-12-31T00:00:00", "9999-12-31T23:59:59", 50000, 40000))
            .Replace("2026-11-02T17:47:26", "9999-12-31T23:59:59", StringComparison.Ordinal);

        var error = Record.Exception(() => Parse(xml));

        Assert.Null(error);
    }

    [Fact]
    public void The_size_limit_stays_far_above_a_real_report_but_small()
    {
        Assert.Equal(2 * 1024 * 1024, BatteryHistoryReader.MaxBytes);
    }

    /// <summary>Faux powercfg : garde la ligne reçue et écrit le fichier demandé, sans rien lancer.</summary>
    private sealed class FakePowercfg(string? content, int exitCode = 0) : ICommandRunner
    {
        public List<string> Seen { get; } = [];

        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Seen.Add(string.Join(' ', arguments));
            if (content is not null)
            {
                File.WriteAllText(arguments[2], content);
            }

            return Task.FromResult(new CommandResult(exitCode, string.Empty, string.Empty, false));
        }
    }

    private sealed class MissingPowercfg : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new DataSourceUnavailableException("introuvable");
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "maus-tests-" + Guid.NewGuid().ToString("N"), "rapport-batterie.xml");

    [Fact]
    public async Task An_elevated_process_refuses_and_runs_nothing()
    {
        var commands = new FakePowercfg(Report(Weeks));

        var result = await BatteryReport.ReadHistoryAsync(elevated: true, TempFile(), commands, CancellationToken.None);

        Assert.Null(result.History);
        Assert.False(string.IsNullOrWhiteSpace(result.Refusal));
        Assert.Empty(commands.Seen);
    }

    [Fact]
    public async Task The_report_is_read_with_the_documented_command_then_removed()
    {
        var file = TempFile();
        try
        {
            var commands = new FakePowercfg(Report(Weeks));

            var result = await BatteryReport.ReadHistoryAsync(elevated: false, file, commands, CancellationToken.None);

            Assert.Equal($"/batteryreport /output {file} /xml", Assert.Single(commands.Seen));
            Assert.Equal(4, result.History?.Points.Count);
            Assert.Null(result.Refusal);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("pas du xml", 0)]
    [InlineData("<a />", 1)]
    public async Task A_failed_report_is_not_read_and_leaves_nothing(string? content, int exitCode)
    {
        var file = TempFile();
        try
        {
            var result = await BatteryReport.ReadHistoryAsync(elevated: false, file, new FakePowercfg(content, exitCode), CancellationToken.None);

            Assert.Null(result.History);
            Assert.Null(result.Refusal);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_powercfg_is_not_read()
    {
        var file = TempFile();
        try
        {
            var result = await BatteryReport.ReadHistoryAsync(elevated: false, file, new MissingPowercfg(), CancellationToken.None);

            Assert.Null(result.History);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task An_oversized_report_is_not_read()
    {
        var file = TempFile();
        try
        {
            var result = await BatteryReport.ReadHistoryAsync(elevated: false, file, new OversizedPowercfg(), CancellationToken.None);

            Assert.Null(result.History);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    private sealed class OversizedPowercfg : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var stream = File.Create(arguments[2]);
            stream.SetLength(BatteryHistoryReader.MaxBytes + 1);
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, false));
        }
    }

    [Fact]
    public void Only_the_exact_xml_command_is_allowed()
    {
        var xml = ReadOnlyCommandRunner.BatteryHistoryPath;
        var html = ReadOnlyCommandRunner.BatteryReportPath;

        Assert.True(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", xml, "/xml"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", xml]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", html, "/xml"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", @"C:\Windows\System32\x.xml", "/xml"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/xml", "/output", xml]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", xml, "/xml", "/duration"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("powercfg.exe", ["/batteryreport", "/output", xml, "/duration"]));
        Assert.False(ReadOnlyCommandRunner.IsAllowed("cmd.exe", ["/batteryreport", "/output", xml, "/xml"]));
    }
}
