using System.Globalization;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class SessionRecordingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 20, 0, 0, TimeSpan.FromHours(2));

    private static SensorSnapshot Sample(int second, double cpu, double maxCore, double gpu, double? cpuTemp = 70, double gpuTemp = 70, long memoryUsed = 8) => new()
    {
        At = Start.AddSeconds(second),
        CpuPercent = cpu,
        CorePercents = [maxCore, cpu],
        CpuMhz = 4800,
        CpuTemperatureC = cpuTemp,
        MemoryUsedBytes = memoryUsed,
        MemoryTotalBytes = 16,
        Gpus = [new GpuSensor("RTX", gpu, gpuTemp, null, null, null, 200, 2700, null, 88)],
    };

    private static SessionRecording Record(int seconds, Func<int, SensorSnapshot> make)
    {
        var recording = new SessionRecording(Start);
        for (var i = 1; i <= seconds; i++)
        {
            recording.Add(make(i));
        }

        return recording;
    }

    [Fact]
    public void Gpu_bound_game_is_described_as_normal()
    {
        var summary = Record(60, i => Sample(i, 40, 70, 99)).Summarize(95);

        Assert.Equal(TimeSpan.FromSeconds(60), summary.Duration);
        Assert.Equal(99, summary.GpuLoad.Average);
        Assert.Contains(summary.Findings, f => f.Contains("carte graphique", StringComparison.Ordinal) && f.Contains("normal", StringComparison.Ordinal));
    }

    [Fact]
    public void Saturated_core_with_waiting_gpu_is_cpu_bound()
    {
        var summary = Record(60, i => Sample(i, 30, 100, 60)).Summarize(95);

        Assert.Contains(summary.Findings, f => f.StartsWith("Limité par le processeur 100 %", StringComparison.Ordinal));
    }

    [Fact]
    public void Heat_and_full_memory_are_reported()
    {
        var summary = Record(60, i => Sample(i, 90, 95, 97, cpuTemp: 94, gpuTemp: 88, memoryUsed: 15)).Summarize(95);

        Assert.Contains(summary.Findings, f => f.Contains("limite de température", StringComparison.Ordinal));
        Assert.Contains(summary.Findings, f => f.Contains("seuil de ralentissement", StringComparison.Ordinal));
        Assert.Contains(summary.Findings, f => f.Contains("90 %", StringComparison.Ordinal));
        Assert.Equal(94, summary.CpuTemperature.Max);
    }

    [Fact]
    public void Quiet_session_says_so_and_missing_sensors_are_ignored()
    {
        var summary = Record(10, i => Sample(i, 20, 30, 20, cpuTemp: null)).Summarize(95);

        Assert.Single(summary.Findings);
        Assert.Null(summary.CpuTemperature.Max);
    }

    [Fact]
    public void Csv_follows_the_windows_language()
    {
        var csv = Record(2, i => Sample(i, 12.5, 50, 99)).ToCsv(CultureInfo.GetCultureInfo("fr-FR"));
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Heure;", lines[0], StringComparison.Ordinal);
        var time = Start.AddSeconds(1).LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.StartsWith(time + ";12,5;50;4800;70;;99;70;2700;200;50", lines[1], StringComparison.Ordinal);
    }
}
