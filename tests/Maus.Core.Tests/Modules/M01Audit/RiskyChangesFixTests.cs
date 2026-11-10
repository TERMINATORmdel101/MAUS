using Maus.Core.Fixes;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M01Audit;

public class RiskyChangesFixTests
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;

    /// <summary>PC passé par un script de « debloat » : tout ce que MAUS sait remettre par le registre.</summary>
    private static M01Pc Debloated()
    {
        var pc = new M01Pc();
        pc.Services["wuauserv"] = 4;
        pc.Services["CryptSvc"] = 4;
        pc.Registry
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0)
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\System", "ShellSmartScreenLevel", "Warn")
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "NoAutoUpdate", 1)
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "UseWUServer", 1)
            .Set(Hklm, M01Pc.WuPolicy, "WUServer", "http://127.0.0.1:8530")
            .Set(Hklm, M01Pc.WuPolicy, "TargetReleaseVersion", 1)
            .Set(Hklm, M01Pc.WuPolicy, "TargetReleaseVersionInfo", "23H2")
            .Set(Hklm, M01Pc.UacKey, "EnableLUA", 0)
            .Set(Hklm, M01Pc.UacKey, "ConsentPromptBehaviorAdmin", 0)
            .Set(Hklm, M01Pc.Winlogon, "Shell", "explorer.exe, C:\\ProgramData\\x\\evil.exe")
            .Set(Hklm, M01Pc.AppInit, "AppInit_DLLs", "C:\\ProgramData\\x\\evil.dll")
            .Set(Hklm, M01Pc.AppInit, "LoadAppInit_DLLs", 1)
            .Set(Hklm, M01Pc.Ifeo + @"\taskmgr.exe", "Debugger", "C:\\Tools\\procexp.exe")
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", "EnableFirewall", 0);
        return pc;
    }

    private static readonly string[] OtherServices = ["UsoSvc", "WaaSMedicSvc", "BITS", "TrustedInstaller", "Audiosrv", "WSearch", "WinDefend", "mpssvc"];

    [Fact]
    public async Task Every_kind_of_fix_round_trips_and_only_writes_allowed_keys()
    {
        const string Wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion";
        var pc = Debloated();
        foreach (var name in OtherServices)
        {
            pc.Services[name] = 4;
        }

        pc.Registry
            .Set(Hklm, M01Pc.UxSettings, "PauseUpdatesExpiryTime", "2046-01-01T00:00:00Z")
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 1)
            .Set(RegistryHive.CurrentUser, M01Pc.Winlogon, "Shell", @"C:\ProgramData\x\evil.exe")
            .Set(Hklm, M01Pc.Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\ProgramData\x\evil.exe,")
            .Set(Hklm, Wow + @"\Windows", "AppInit_DLLs", @"C:\ProgramData\x\evil32.dll")
            .Set(Hklm, Wow + @"\Windows", "LoadAppInit_DLLs", 1)
            .Set(Hklm, M01Pc.Ifeo + @"\sethc.exe", "Debugger", @"C:\x\cmd.exe")
            .Set(Hklm, Wow + @"\Image File Execution Options\sethc.exe", "Debugger", @"C:\x\cmd.exe");
        var audit = pc.CreateContext();

        var (plan, _) = await Maus.Core.Tests.Fixes.RoundTrip.AssertAsync(pc.Module, audit, pc.Registry);

        // Le même programme détourné sous les deux clés ne donne qu'une correction, qui les remet toutes les deux.
        Assert.Equal(2, plan.Single(c => c.Id == "M01.ifeo-debugger.sethc.exe").Writes.Count);
        Assert.Contains(plan, c => c.Id == "M01.wu-pause");
        Assert.Contains(plan, c => c.Id == "M01.defender-realtime");
        Assert.Equal(3, plan.Single(c => c.Id == "M01.winlogon").Writes.Count);
        Assert.All(OtherServices, name => Assert.Contains(plan, c => c.Id.EndsWith("." + name, StringComparison.Ordinal)));
        Assert.All(plan.SelectMany(c => c.Writes), w => Assert.True(SettingAllowlist.Allows(w.Key), w.Key.Describe()));
    }

    [Fact]
    public async Task No_fix_ever_targets_ucpd()
    {
        var pc = new M01Pc();
        pc.Services["UCPD"] = 4;
        var audit = pc.CreateContext();

        var plan = pc.Module.Plan(audit, await pc.Module.DetectAsync(audit, CancellationToken.None));

        Assert.DoesNotContain(plan.SelectMany(c => c.Writes), w => (w.Key.Path ?? string.Empty).Contains("UCPD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Debloated_pc_round_trip_returns_to_the_initial_state()
    {
        var pc = Debloated();
        var audit = pc.CreateContext();

        var (plan, _) = await Maus.Core.Tests.Fixes.RoundTrip.AssertAsync(pc.Module, audit, pc.Registry);

        Assert.Contains(plan, c => c.Id == "M01.wu-services.wuauserv" && c.FindingId == "M01.wu-services");
        Assert.Contains(plan, c => c.Id == "M01.core-services.CryptSvc");
        Assert.Equal(2, plan.Single(c => c.Id == "M01.smartscreen").Writes.Count);
        Assert.True(plan.Single(c => c.Id == "M01.uac").Recommended);
        Assert.False(plan.Single(c => c.Id == "M01.defender-policy").Recommended);
        Assert.False(plan.Single(c => c.Id == "M01.ifeo-debugger.taskmgr.exe").Recommended);
        Assert.Equal(ChangeEffect.Restart, plan.Single(c => c.Id == "M01.uac").Effect);
    }

    [Fact]
    public async Task Policy_deletions_warn_when_a_local_policy_file_exists()
    {
        var pc = Debloated();
        pc.Files.AddFile(@"C:\Windows\System32\GroupPolicy\Machine\Registry.pol");
        var audit = pc.CreateContext();

        var plan = pc.Module.Plan(audit, await pc.Module.DetectAsync(audit, CancellationToken.None));

        Assert.Contains("Registry.pol", plan.Single(c => c.Id == "M01.wu-server").Risk, StringComparison.Ordinal);
        Assert.Null(plan.Single(c => c.Id == "M01.uac-prompt").Risk);
    }

    [Fact]
    public async Task Clean_pc_gets_no_change()
    {
        var pc = new M01Pc();
        var audit = pc.CreateContext();

        Assert.Empty(pc.Module.Plan(audit, await pc.Module.DetectAsync(audit, CancellationToken.None)));
    }
}
