using System.Globalization;
using Maus.Core.Localization;
using Maus.Core.Modules.M10Memory;
using Maus.Core.Modules.M11Health;
using Maus.Core.Platform;
using Maus.Core.Tests.Modules.M03Updates;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Localization;

/// <summary>
/// Dates, nombres et unités suivent la langue de MAUS ; les CSV suivent le format régional de Windows ; la sortie des outils
/// classiques se lit dans la page OEM du système (défauts 3 et 6 de la 0.5.3).
/// </summary>
[Collection(nameof(LanguageSwitch))]
public class LocalizedFormatsTests
{
    [Fact]
    public async Task Dates_and_days_follow_the_language_of_maus()
    {
        try
        {
            Texts.Use("en");
            var windows = new WindowsInfo("Windows 11 Home", "Core", "24H2", 26100, 6000);

            var finding = WindowsUpdateModuleTests.Get(await WindowsUpdateModuleTests.Detect(windows: windows), "M03.windows-support");

            Assert.Contains("10/13/2026", finding.Current, StringComparison.Ordinal);
            Assert.Contains("19 days", finding.Current, StringComparison.Ordinal);
            Assert.DoesNotContain("13/10/2026", finding.Current, StringComparison.Ordinal);
        }
        finally
        {
            Texts.Use("fr");
        }
    }

    [Fact]
    public void Sizes_use_the_decimal_mark_and_unit_of_the_language()
    {
        const long Bytes = 500_107_862_016; // 465,76 Gio
        try
        {
            Assert.Equal("465,8 Go", HealthParsers.FormatGigabytes(Bytes));
            Assert.Equal("16 Go", MemoryModule.FormatCapacity(16L * 1024 * 1024 * 1024));

            Texts.Use("en");
            Assert.Equal("465.8 GB", HealthParsers.FormatGigabytes(Bytes));
            Assert.Equal("0.5 GB", MemoryModule.FormatCapacity(512L * 1024 * 1024));

            Texts.Use("es");
            Assert.Equal("465,8 GB", HealthParsers.FormatGigabytes(Bytes));
        }
        finally
        {
            Texts.Use("fr");
        }
    }

    [Fact]
    public void Oem_code_page_850_decodes_french_accents()
    {
        // Page 850 (Windows français) : 0x82 = « é », 0x85 = « à » ; lus en UTF-8, ces octets seraient illisibles.
        Assert.Equal("é", OemEncoding.Get(850).GetString([0x82]));
        Assert.Equal("à", OemEncoding.Get(850).GetString([0x85]));
    }

    [Fact]
    public void Unknown_code_page_falls_back_to_ascii_compatible_page()
    {
        Assert.Equal("OK", OemEncoding.Get(99_999).GetString("OK"u8.ToArray()));
        Assert.True(OemEncoding.CodePage > 0);
    }

    [Fact]
    public void Csv_follows_windows_regional_format_not_the_language_of_maus()
    {
        var previous = Texts.RegionalCulture;
        try
        {
            // Windows en anglais avec le format régional français : Excel attend « ; » et « 12,5 ».
            Texts.UseRegionalCulture(CultureInfo.GetCultureInfo("fr-FR"));
            Texts.Use("en");
            var start = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.FromHours(2));
            var recording = new SessionRecording(start);
            recording.Add(new SensorSnapshot
            {
                At = start.AddSeconds(1),
                CpuPercent = 12.5,
                CorePercents = [50, 12.5],
                MemoryUsedBytes = 8,
                MemoryTotalBytes = 16,
            });

            var lines = recording.ToCsv(Texts.RegionalCulture).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.StartsWith("Time;", lines[0], StringComparison.Ordinal);
            Assert.Contains(";12,5;", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            Texts.UseRegionalCulture(previous);
            Texts.Use("fr");
        }
    }
}
