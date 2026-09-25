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
