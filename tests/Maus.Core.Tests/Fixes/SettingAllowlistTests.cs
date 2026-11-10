using Maus.Core.Fixes;
using Maus.Core.Rules;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

/// <summary>Liste blanche des réglages (0.8.6) : les catalogues et les clés écrites par le code passent, tout le reste est refusé.</summary>
public class SettingAllowlistTests
{
    private static SettingKey Key(string hive, string path, string name) => SettingKey.Registry(hive, path, name);

    [Theory]
    [InlineData("m01-audit-rules.json")]
    [InlineData("m04-privacy-rules.json")]
    [InlineData("m06-visual-rules.json")]
    public void Every_registry_rule_of_the_catalogs_is_allowed(string catalog)
    {
        var rules = EmbeddedCatalog.LoadRegistryRules(catalog);

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.True(SettingAllowlist.Allows(Key(rule.Hive, rule.Path, rule.Name)), $"{rule.Hive}\\{rule.Path}\\{rule.Name}"));

        // Toutes les écritures d'une correction, valeurs supprimées en même temps comprises.
        Assert.All(rules.Where(rule => rule.Fix is not null).SelectMany(rule => rule.Fix!.ToWrites(rule)), write => Assert.True(SettingAllowlist.Allows(write.Key), write.Key.Describe()));
    }

    [Fact]
    public void Windows_update_pause_key_is_allowed() =>
        Assert.True(SettingAllowlist.Allows(Key("HKLM", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime")));

    [Theory]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe", "Debugger")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe", "Debugger")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Userinit")]
    [InlineData("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows", "LoadAppInit_DLLs")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection\Test\A", "X")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", "EnableFirewall")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig")]
    [InlineData("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "OneDrive")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "Discord.lnk")]
    [InlineData("HKCU", @"System\GameConfigStore", "GameDVR_Enabled")]
    [InlineData("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled")]
    [InlineData("hklm", @"software\policies\microsoft\windows\datacollection", "allowtelemetry")]
    public void Keys_written_by_the_code_are_allowed(string hive, string path, string name) =>
        Assert.True(SettingAllowlist.Allows(Key(hive, path, name)));

    [Fact]
    public void Restore_point_frequency_key_is_allowed() => Assert.True(SettingAllowlist.Allows(RestorePointCreator.FrequencyKey));

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Updater")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\wuauserv", "ImagePath")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services", "Start")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\wuauserv\Parameters", "Start")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe", "UseFilter")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\a\b", "Debugger")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Notify")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\GPExtensions", "Shell")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\WindowsFirewall", "EnableFirewall")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler", "HwSchMode")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender Security Center", "Enabled")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\AdvancedX", "Hidden")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", "Run")]
    [InlineData("HKCU", @"System\GameConfigStore\Children\x", "GameDVR_Enabled")]
    [InlineData("HKLM", @"SAM\SAM", "C")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown")]
    [InlineData("HKU", @"S-1-5-18\Software", "X")]
    [InlineData("HKLM", "", "X")]
    public void Any_other_key_is_refused(string hive, string path, string name) =>
        Assert.False(SettingAllowlist.Allows(Key(hive, path, name)));

    [Fact]
    public void Accessor_refuses_to_write_or_delete_outside_the_list_and_leaves_the_registry_untouched()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater", "x.exe");
        var parameters = new FakeSystemParameters();
        var accessor = new SettingsAccessor(registry, registry, parameters, parameters);
        var key = Key("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater");

        Assert.Throws<InvalidOperationException>(() => accessor.Write(key, SettingValue.Text("y.exe")));
        Assert.Throws<InvalidOperationException>(() => accessor.Write(key, null));
        Assert.Throws<InvalidOperationException>(() => accessor.RemoveContainerIfEmpty(key));

        Assert.Equal("x.exe", registry.GetValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater"));
        Assert.Equal("x.exe", accessor.Read(key)?.Data);
    }

    [Fact]
    public void Accessor_writes_a_listed_key()
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters();
        var accessor = new SettingsAccessor(registry, registry, parameters, parameters);
        var key = Key("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");

        accessor.Write(key, SettingValue.Dword(2));

        Assert.Equal(2, registry.GetValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"));
    }

    [Theory]
    [InlineData(@"\", "Updater", true)]
    [InlineData(@"\Vendor\", "Updater", true)]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate", "Scheduled Start", false)]
    [InlineData(@"Microsoft\", "x", false)]
    [InlineData(@"\", "", false)]
    public void Windows_own_tasks_are_never_touched(string path, string name, bool allowed) =>
        Assert.Equal(allowed, SettingAllowlist.Allows(SettingKey.ScheduledTask(path, name)));

    [Fact]
    public void Other_kinds_of_settings_are_not_filtered_here()
    {
        Assert.True(SettingAllowlist.Allows(SettingKey.ActivePowerScheme));
        Assert.True(SettingAllowlist.Allows(SettingKey.MinimizeAnimation));
        Assert.True(SettingAllowlist.Allows(SettingKey.Spi(0x0026, 0x0025)));
    }
}
