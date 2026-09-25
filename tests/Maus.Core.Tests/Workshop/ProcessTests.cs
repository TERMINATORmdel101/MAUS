using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class ProcessTests
{
    private static readonly SystemFolders Folders = new(@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData");
    private static readonly DateTime Started = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FakeProcesses : IProcessSource
    {
        public List<RawProcess> Current { get; set; } = [];

        public Dictionary<int, double> Gpu { get; } = [];

        public IReadOnlyList<RawProcess> Read() => Current;

        public IReadOnlyDictionary<int, double> GpuPercentByPid() => Gpu;
    }

    private static ProcessSample Sample(string name, string? path, int pid = 100) =>
        new(pid, name, path, 0, 0, 0, 0, null, 1, Started);

    [Fact]
    public void Monitor_turns_cumulative_counters_into_rates()
    {
        var clock = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        var source = new FakeProcesses
        {
            Current = [new RawProcess(10, "game.exe", @"C:\Games\game.exe", TimeSpan.FromSeconds(10), 1000, 2000, 1_000_000, 1, Started)],
        };
        source.Gpu[10] = 75;
        var monitor = new ProcessMonitor(source, logicalProcessors: 4, () => clock);

        var first = monitor.Sample().Single();
        clock = clock.AddSeconds(2);
        source.Current = [new RawProcess(10, "game.exe", @"C:\Games\game.exe", TimeSpan.FromSeconds(14), 1000, 2000, 5_000_000, 1, Started)];
        var second = monitor.Sample().Single();

        Assert.Equal(0, first.CpuPercent);
        Assert.Equal(50, second.CpuPercent, precision: 3);
        Assert.Equal(2_000_000, second.DiskBytesPerSecond, precision: 3);
        Assert.Equal(75, second.GpuPercent);
    }

    [Fact]
    public void Reused_process_id_is_not_mixed_with_the_old_process()
    {
        var clock = DateTimeOffset.UnixEpoch;
        var source = new FakeProcesses { Current = [new RawProcess(10, "a.exe", null, TimeSpan.FromSeconds(100), 0, 0, null, 1, Started)] };
        var monitor = new ProcessMonitor(source, 1, () => clock);
        monitor.Sample();
        clock = clock.AddSeconds(1);
        source.Current = [new RawProcess(10, "b.exe", null, TimeSpan.FromSeconds(1), 0, 0, null, 1, Started.AddMinutes(5))];

        Assert.Equal(0, monitor.Sample().Single().CpuPercent);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", ProcessTrust.Windows)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", ProcessTrust.Installed)]
    [InlineData(@"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe", ProcessTrust.UserFolder)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\a1b2\run.exe", ProcessTrust.Unusual)]
    [InlineData(@"C:\Users\x\Downloads\setup.exe", ProcessTrust.Unusual)]
    [InlineData(@"D:\Jeux\game.exe", ProcessTrust.Unknown)]
    public void Trust_depends_on_location(string path, ProcessTrust expected)
    {
        Assert.Equal(expected, ProcessRules.TrustOf(Sample("x.exe", path), Folders));
    }

    [Theory]
    [InlineData("csrss.exe", false)]
    [InlineData("lsass", false)]
    [InlineData("svchost.exe", false)]
    [InlineData("chrome.exe", true)]
    public void Vital_processes_can_never_be_terminated(string name, bool allowed)
    {
        Assert.Equal(allowed, ProcessRules.CanTerminate(Sample(name, null), ownPid: 1).Allowed);
    }

    [Fact]
    public void Maus_never_terminates_itself()
    {
        var (allowed, reason) = ProcessRules.CanTerminate(Sample("MAUS.exe", null, pid: 42), ownPid: 42);

        Assert.False(allowed);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Web_search_escapes_the_query_for_each_engine()
    {
        var query = WebSearch.ForProcess("svchost.exe");

        Assert.Equal("\"svchost.exe\" processus Windows", query);
        Assert.StartsWith("https://duckduckgo.com/?q=%22svchost.exe%22", WebSearch.Build(SearchEngine.DuckDuckGo, query).AbsoluteUri, StringComparison.Ordinal);
        Assert.StartsWith("https://www.qwant.com/?q=", WebSearch.Build(SearchEngine.Qwant, query).AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_explains_known_processes_with_or_without_extension()
    {
        var catalog = ProcessCatalog.Default;

        Assert.Equal("Windows", catalog.Explain("svchost").Category);
        Assert.Contains("onglet", catalog.Explain("chrome.exe").What, StringComparison.Ordinal);
        Assert.Equal("Non répertorié", catalog.Explain("inconnu123.exe").Category);
        Assert.Equal(catalog.Entries.Count, catalog.Entries.Select(e => e.Names[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Slowness_diagnosis_ranks_the_main_causes()
    {
        var samples = Enumerable.Range(0, 60).Select(i => new SensorSnapshot
        {
            At = DateTimeOffset.UnixEpoch.AddSeconds(i),
            CpuPercent = 95,
            MemoryUsedBytes = 90,
            MemoryTotalBytes = 100,
            DiskActivePercent = 20,
        }).ToList();
        ProcessSample[] processes = [new(7, "chrome.exe", null, 60, 1, 3L << 30, 0, null, 1, Started), new(8, "idle.exe", null, 1, 1, 1, 0, null, 1, Started)];

        var causes = SlownessDiagnosis.Analyze(samples, processes, ProcessCatalog.Default);

        Assert.Equal(2, causes.Count);
        Assert.Contains("processeur", causes[0].Title, StringComparison.Ordinal);
        Assert.Contains("chrome.exe", causes[0].Explanation, StringComparison.Ordinal);
        Assert.Equal("M10", causes[1].ModuleId);
    }

    [Fact]
    public void Calm_pc_gets_a_reassuring_answer()
    {
        var samples = new[] { new SensorSnapshot { At = DateTimeOffset.UnixEpoch, CpuPercent = 5, DiskActivePercent = 2 } };

        var cause = SlownessDiagnosis.Analyze(samples, [], ProcessCatalog.Default).Single();

        Assert.Equal(0, cause.Weight);
    }
}
