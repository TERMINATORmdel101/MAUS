using Maus.Core.Localization;
using Maus.Core.Modules.M05Power;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.Core.Tests.Workshop;

public class LastRestartTests
{
    private const string SystemLog = "System";

    private static readonly DateTime Started = new(2026, 10, 10, 7, 9, 21);

    private static FakeEventLogs Start(FakeEventLogs logs, DateTime when) => logs.Add(SystemLog, LastRestart.StartProvider, LastRestart.StartEventId, when);

    private static FakeEventLogs Boot(FakeEventLogs logs, DateTime when, string type) =>
        logs.Add(SystemLog, LastRestart.BootProvider, LastRestart.BootEventId, when, new Dictionary<string, string> { ["BootType"] = type });

    private static FakeRegistry Setting(int? hiberboot, int? hibernate = 1)
    {
        var registry = new FakeRegistry();
        if (hiberboot is { } on)
        {
            registry.Set(RegistryHive.LocalMachine, PowerModule.SessionPowerKey, "HiberbootEnabled", on);
        }

        if (hibernate is { } allowed)
        {
            registry.Set(RegistryHive.LocalMachine, PowerModule.PowerKey, "HibernateEnabled", allowed);
        }

        return registry;
    }

    private static string Date(DateTime when) => when.ToString("g", Texts.Culture);

    [Fact]
    public void The_full_start_is_dated_without_a_duration_and_without_being_called_the_cause()
    {
        var restart = LastRestart.Read(Boot(Start(new FakeEventLogs(), Started), Started, "0"), new FakeCim(), Setting(0));

        Assert.Equal(Started, restart.RestartedAt);
        Assert.Null(restart.FastStartedAt);
        Assert.Contains(Date(Started), restart.Text, StringComparison.Ordinal);
        Assert.Contains("Rien ne prouve", restart.Text, StringComparison.Ordinal);
        Assert.Contains("sans gain promis", restart.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("il y a", restart.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(" jours", restart.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fast_startup_noted_by_windows_keeps_the_older_full_start()
    {
        var fast = Started.AddDays(3);
        var logs = Boot(Boot(Start(new FakeEventLogs(), Started), Started, "0"), fast, "1");

        var restart = LastRestart.Read(logs, new FakeCim(), Setting(1));

        Assert.Equal(Started, restart.RestartedAt);
        Assert.Equal(fast, restart.FastStartedAt);
        Assert.Contains("démarrage rapide : ce n'était pas un démarrage complet", restart.Text, StringComparison.Ordinal);
        Assert.Contains(Date(fast), restart.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x1", true)]
    [InlineData("1", true)]
    [InlineData("0x0", false)]
    [InlineData("0", false)]
    [InlineData("2", false)]
    [InlineData("n'importe quoi", false)]
    public void Only_boot_type_one_is_called_a_fast_startup(string type, bool fast)
    {
        var logs = Boot(Start(new FakeEventLogs(), Started), Started.AddDays(1), type);

        Assert.Equal(fast, LastRestart.Read(logs, new FakeCim(), Setting(1)).FastStartedAt is not null);
    }

    [Fact]
    public void A_full_start_after_a_fast_one_clears_it()
    {
        var logs = Boot(Start(Boot(new FakeEventLogs(), Started.AddDays(-1), "1"), Started), Started, "0");

        Assert.Null(LastRestart.Read(logs, new FakeCim(), Setting(1)).FastStartedAt);
    }

    [Fact]
    public void A_log_too_short_falls_back_on_wmi()
    {
        var cim = new FakeCim().Answer(LastRestart.BootQuery, new Dictionary<string, object?> { ["LastBootUpTime"] = Started });

        Assert.Equal(Started, LastRestart.Read(new FakeEventLogs(), cim, Setting(0)).RestartedAt);
    }

    [Fact]
    public void Nothing_readable_is_said_and_nothing_is_invented()
    {
        var cim = new FakeCim().Throw(LastRestart.BootQuery, new DataSourceUnavailableException("WMI"));

        var restart = LastRestart.Read(new FakeEventLogs().Deny(SystemLog), cim, Setting(null, null));

        Assert.Null(restart.RestartedAt);
        Assert.Equal(FastStartupSetting.Unknown, restart.Setting);
        Assert.Contains("n'a pas pu être lue", restart.Text, StringComparison.Ordinal);
        Assert.Contains("n'a pas pu être lu :", restart.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, FastStartupSetting.Active)]
    [InlineData(1, null, FastStartupSetting.Active)]
    [InlineData(1, 0, FastStartupSetting.Inactive)]
    [InlineData(0, 1, FastStartupSetting.Inactive)]
    [InlineData(null, 1, FastStartupSetting.Unknown)]
    public void The_setting_only_counts_when_hibernation_allows_it(int? hiberboot, int? hibernate, FastStartupSetting expected)
    {
        Assert.Equal(expected, LastRestart.Read(new FakeEventLogs(), new FakeCim(), Setting(hiberboot, hibernate)).Setting);
    }
}
