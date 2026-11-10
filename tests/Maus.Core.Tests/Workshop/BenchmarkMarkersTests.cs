using System.IO.Compression;
using System.Text.Json;
using Maus.Core.Workshop.Benchmark;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

public sealed class BenchmarkMarkersTests
{
    private static BenchmarkReport Run(string? driver, string? build, string gpu = "Carte A") => new()
    {
        Gpu = gpu,
        GpuDriver = driver,
        WindowsBuild = build,
    };

    [Fact]
    public void A_driver_change_is_said_with_both_versions_and_no_cause()
    {
        var marker = BenchmarkMarkers.Between(Run("32.0.1", "26100.1"), Run("32.0.2", "26100.1"));

        Assert.Equal("Pilote graphique changé entre ces deux mesures : 32.0.1 → 32.0.2.", marker!.Text);
        Assert.Null(marker.BuildTo);
    }

    [Fact]
    public void A_windows_change_is_said_with_both_builds()
    {
        var marker = BenchmarkMarkers.Between(Run("32.0.1", "26100.1"), Run("32.0.1", "26100.2"));

        Assert.Equal("Build de Windows changé entre ces deux mesures : 26100.1 → 26100.2.", marker!.Text);
        Assert.Null(marker.DriverTo);
    }

    [Fact]
    public void Both_changes_appear_in_one_sentence()
    {
        var text = BenchmarkMarkers.Between(Run("1", "a"), Run("2", "b"))!.Text;

        Assert.Equal("Pilote graphique (1 → 2) et build de Windows (a → b) changés entre ces deux mesures.", text);
    }

    [Theory]
    [InlineData(null, null, "32.0.1", "26100.1")]
    [InlineData("32.0.1", "26100.1", null, null)]
    [InlineData("32.0.1", null, null, "26100.1")]
    [InlineData("", "  ", "32.0.1", "26100.1")]
    [InlineData("32.0.1", "26100.1", "32.0.1", "26100.1")]
    [InlineData(" 32.0.1 ", "26100.1", "32.0.1", "26100.1 ")]
    public void Nothing_is_shown_when_a_value_is_missing_or_unchanged(string? driverA, string? buildA, string? driverB, string? buildB)
    {
        Assert.Null(BenchmarkMarkers.Between(Run(driverA, buildA), Run(driverB, buildB)));
    }

    [Fact]
    public void A_different_card_is_not_called_a_driver_change()
    {
        Assert.Null(BenchmarkMarkers.Between(Run("1", "a", "Carte A"), Run("2", "a", "Carte B")));
        Assert.NotNull(BenchmarkMarkers.Between(Run("1", "a", "Carte A"), Run("2", "b", "Carte B"))!.BuildTo);
    }

    [Fact]
    public void The_driver_version_is_written_like_the_device_manager()
    {
        var version = (32L << 48) | (0L << 32) | (16L << 16) | 1742L;

        Assert.Equal("32.0.16.1742", BenchmarkMarkers.DriverVersionText(version));
        Assert.Null(BenchmarkMarkers.DriverVersionText(0));
        Assert.Null(BenchmarkMarkers.DriverVersionText(-1));
    }

    [Theory]
    [InlineData("26100", 7171, "26100.7171")]
    [InlineData("26100", null, "26100")]
    [InlineData("pas un nombre", 7171, null)]
    [InlineData(null, null, null)]
    public void The_windows_build_is_never_invented(string? build, int? ubr, string? expected)
    {
        const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var registry = new FakeRegistry().EnsureKey(RegistryHive.LocalMachine, Key);
        if (build is not null)
        {
            registry.Set(RegistryHive.LocalMachine, Key, "CurrentBuild", build);
        }

        if (ubr is not null)
        {
            registry.Set(RegistryHive.LocalMachine, Key, "UBR", ubr);
        }

        Assert.Equal(expected, BenchmarkMarkers.WindowsBuildText(registry));
    }

    [Fact]
    public void An_old_history_stays_readable_and_gets_no_invented_value()
    {
        const string old = "{\"date\":\"2026-10-01T00:00:00+00:00\",\"gpu\":\"Carte A\",\"tests\":[]}";

        var report = JsonSerializer.Deserialize<BenchmarkReport>(old, BenchmarkHistoryStore.Json)!;

        Assert.Null(report.GpuDriver);
        Assert.Null(report.WindowsBuild);
        Assert.DoesNotContain("gpuDriver", BenchmarkHistoryStore.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("windowsBuild", BenchmarkHistoryStore.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_fields_survive_in_the_history_file()
    {
        var folder = Directory.CreateTempSubdirectory("maus-markers-");
        try
        {
            var store = new BenchmarkHistoryStore(Path.Combine(folder.FullName, "history.json"), _ => { });
            store.Add(Run("32.0.1", "26100.1") with { Date = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero) });

            var read = store.Load().Single();

            Assert.Equal("32.0.1", read.GpuDriver);
            Assert.Equal("26100.1", read.WindowsBuild);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_share_code_keeps_exactly_the_same_format()
    {
        var report = new BenchmarkReport
        {
            Date = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero),
            Version = "0.8.6",
            Api = "Direct3D 12",
            RenderResolution = "1920×1080",
            DisplayResolution = "2560×1440",
            Gpu = "Carte A",
            Cpu = "Processeur",
            Threads = 12,
            GpuScore = 9000,
            CpuScore = 9500,
            OverallScore = 9120,
            RayTracingScore = 8000,
            Completed = true,
            Tests = [new BenchmarkTestResult("ring", "gpu", 32.4, "u", 9000, 29.1)],
        };

        var code = BenchmarkShareCode.Encode(report);

        // Les nouveaux champs ne changent pas un seul caractère du code.
        Assert.Equal(code, BenchmarkShareCode.Encode(report with { GpuDriver = "32.0.1", WindowsBuild = "26100.1" }));

        using var json = JsonDocument.Parse(Inflate(code));
        string[] expected =
        [
            "version", "date", "api", "renderResolution", "gpu", "cpu", "threads", "gpuScore", "cpuScore", "overallScore", "completed", "tests", "rayTracingScore", "displayResolution",
        ];
        Assert.Equal(expected, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.StartsWith("MAUS-BENCH-1:", code, StringComparison.Ordinal);

        var decoded = BenchmarkShareCode.Decode(code)!;
        Assert.Null(decoded.GpuDriver);
        Assert.Null(decoded.WindowsBuild);
    }

    private static string Inflate(string code)
    {
        var data = code[BenchmarkShareCode.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        data = data.PadRight(data.Length + ((4 - (data.Length % 4)) % 4), '=');
        using var input = new MemoryStream(Convert.FromBase64String(data));
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate);
        return reader.ReadToEnd();
    }
}
