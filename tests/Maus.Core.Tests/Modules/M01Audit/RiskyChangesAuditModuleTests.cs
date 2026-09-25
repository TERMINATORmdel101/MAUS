using Maus.Core.Engine;
using Maus.Core.Modules.M01Audit;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M01Audit;

public class RiskyChangesAuditModuleTests
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    private static readonly string[] BroadPaths = [@"C:\", @"D:\Jeux\Steam"];
    private static readonly string[] ExeExtension = [".exe"];
    private static readonly string[] PowerShellProcess = ["powershell.exe"];
    private static readonly string[] GamePath = [@"D:\Jeux\Steam\steamapps"];
    private static readonly string[] ToolProcess = [@"C:\Outils\build.exe"];

    [Fact]
    public void Module_is_discovered_with_its_catalogue_identity()
    {
        var module = AuditEngine.DiscoverModules(typeof(IAuditModule).Assembly).OfType<RiskyChangesAuditModule>().Single();

        Assert.Equal("M01", module.Id);
        Assert.Equal("Audit des modifications risquées", module.Title);
        Assert.Equal(10, module.Order);
        Assert.Equal(TimeSpan.FromSeconds(90), ((IAuditModule)module).Timeout);
    }

    [Fact]
    public async Task Clean_windows_is_fully_compliant()
    {
        var findings = await new M01Pc().RunAsync();

        Assert.Equal(31, findings.Count);
        Assert.All(findings, f => Assert.StartsWith("M01.", f.Id, StringComparison.Ordinal));
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
        Assert.All(findings, f => Assert.True(f.Status == FindingStatus.Ok, $"{f.Id} : {f.Status} ({f.Current})"));
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Explanation)));
    }

    [Fact]
    public async Task Without_admin_rights_protected_sources_are_unknown_and_never_problems()
    {
        var pc = new M01Pc { Elevated = false };
        const string hidden = "N/A: Must be an administrator to view exclusions";
        pc.Cim.Answer(RiskyChangesAuditModule.DefenderPreferenceQuery, CimScopes.Defender, new Dictionary<string, object?>
        {
            ["ExclusionPath"] = new[] { hidden },
            ["ExclusionExtension"] = new[] { hidden },
            ["ExclusionProcess"] = new[] { hidden },
        });
        pc.Tasks.Folders.Clear();
        pc.Registry.Deny(Hklm, M01Pc.Ifeo + @"\MsMpEng.exe").Set(Hklm, M01Pc.Ifeo + @"\MsMpEng.exe", "x", 1);

        var findings = await pc.RunAsync();

        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.defender-exclusions").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.wu-tasks").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.boot-timer").Status);
        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M01.driver-signing").Status);
        Assert.Contains("WinRE non vérifié", findings.Single(f => f.Id == "M01.modified-image").Current, StringComparison.Ordinal);
        Assert.Contains("protégée", findings.Single(f => f.Id == "M01.ifeo-debugger").Current, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable);
    }

    [Fact]
    public async Task Debloat_script_traces_are_reported_with_their_severity()
    {
        var pc = new M01Pc();
        pc.Registry
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 1)
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0)
            .Set(Hklm, M01Pc.UacKey, "EnableLUA", 0)
            .Set(Hklm, M01Pc.UacKey, "ConsentPromptBehaviorAdmin", 0)
            .Set(Hklm, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", "EnableFirewall", 0)
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "NoAutoUpdate", 1)
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "UseWUServer", 1)
            .Set(Hklm, M01Pc.WuPolicy, "WUServer", "http://127.0.0.1")
            .Set(Hklm, M01Pc.WuPolicy, "TargetReleaseVersion", 1)
            .Set(Hklm, M01Pc.WuPolicy, "ProductVersion", "Windows 11")
            .Set(Hklm, M01Pc.WuPolicy, "TargetReleaseVersionInfo", "23H2")
            .Set(Hklm, M01Pc.UxSettings, "PauseUpdatesExpiryTime", "2046-01-01T00:00:00Z")
            .Set(Hkcu, M01Pc.InternetSettings, "ProxyEnable", 1)
            .Set(Hkcu, M01Pc.InternetSettings, "ProxyServer", "127.0.0.1:8080")
            .Set(Hklm, M01Pc.WebView2, "pv", "0.0.0.0")
            .Set(Hklm, @"SYSTEM\CurrentControlSet\Control", "SystemStartOptions", " NOEXECUTE=ALWAYSOFF  TESTSIGNING")
            .Set(Hklm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", 0)
            .Set(Hklm, M01Pc.Ifeo + @"\MsMpEng.exe", "Debugger", "svchost.exe")
            .Set(Hklm, M01Pc.Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\Users\Public\evil.exe,")
            .Set(Hklm, M01Pc.AppInit, "AppInit_DLLs", @"C:\ProgramData\hook.dll")
            .Set(Hklm, M01Pc.AppInit, "LoadAppInit_DLLs", 1)
            .Set(Hklm, M01Pc.Policies + @"\Windows\Explorer", "NoControlPanel", 1);
        pc.Services["wuauserv"] = 4;
        pc.Services["WaaSMedicSvc"] = 4;
        pc.Services["Audiosrv"] = 4;
        pc.Cim
            .Answer(RiskyChangesAuditModule.DefenderStatusQuery, CimScopes.Defender, new Dictionary<string, object?> { ["RealTimeProtectionEnabled"] = false, ["IsTamperProtected"] = false })
            .Answer(RiskyChangesAuditModule.DefenderPreferenceQuery, CimScopes.Defender, new Dictionary<string, object?>
            {
                ["ExclusionPath"] = BroadPaths,
                ["ExclusionExtension"] = ExeExtension,
                ["ExclusionProcess"] = PowerShellProcess,
            })
            .Answer(M01Pc.ComputerSystemQuery, new Dictionary<string, object?> { ["AutomaticManagedPagefile"] = false })
            .Answer(M01Pc.DepQuery, new Dictionary<string, object?> { ["DataExecutionPrevention_SupportPolicy"] = 0u });
        pc.Commands.Answer("bcdedit.exe /enum {current}", M01Pc.BcdCurrent + "testsigning             Yes\r\nuseplatformclock        Yes\r\ndisabledynamictick      Yes\r\n");
        pc.Files
            .AddFile(M01Pc.Hosts, "127.0.0.1 localhost\r\n0.0.0.0 update.microsoft.com\r\n0.0.0.0 fe2.update.microsoft.com # blocage\r\n0.0.0.0 v10.events.data.microsoft.com\r\n")
            .AddFile(@"C:\Windows\AtlasModules\Tools\x.cmd");
        pc.Packages.Packages.RemoveAll(p => p.Name == "Microsoft.SecHealthUI");
        pc.Tasks.Folders[M01Pc.UpdateTasks] = [new ScheduledTaskInfo("Schedule Scan", false), new ScheduledTaskInfo("USO_UxBroker", true)];
        pc.Signatures.Statuses[@"C:\Program Files (x86)\Steam\steam.exe"] = SignatureStatus.Unsigned;

        var findings = await pc.RunAsync();

        var expected = new Dictionary<string, FindingStatus>
        {
            ["M01.defender-realtime"] = FindingStatus.Problem,
            ["M01.defender-policy"] = FindingStatus.Problem,
            ["M01.defender-tamper"] = FindingStatus.Problem,
            ["M01.defender-exclusions"] = FindingStatus.Problem,
            ["M01.smartscreen"] = FindingStatus.Problem,
            ["M01.uac"] = FindingStatus.Problem,
            ["M01.uac-prompt"] = FindingStatus.Problem,
            ["M01.firewall"] = FindingStatus.Problem,
            ["M01.wu-services"] = FindingStatus.Problem,
            ["M01.wu-auto-update"] = FindingStatus.Problem,
            ["M01.wu-server"] = FindingStatus.Problem,
            ["M01.wu-pause"] = FindingStatus.Problem,
            ["M01.wu-target-version"] = FindingStatus.Problem,
            ["M01.wu-tasks"] = FindingStatus.Warning,
            ["M01.hosts"] = FindingStatus.Problem,
            ["M01.proxy"] = FindingStatus.Problem,
            ["M01.core-services"] = FindingStatus.Problem,
            ["M01.system-apps"] = FindingStatus.Warning,
            ["M01.webview2"] = FindingStatus.Problem,
            ["M01.pagefile"] = FindingStatus.Warning,
            ["M01.dep"] = FindingStatus.Problem,
            ["M01.driver-signing"] = FindingStatus.Problem,
            ["M01.boot-timer"] = FindingStatus.Warning,
            ["M01.secure-boot"] = FindingStatus.Warning,
            ["M01.ifeo-debugger"] = FindingStatus.Problem,
            ["M01.winlogon"] = FindingStatus.Problem,
            ["M01.appinit"] = FindingStatus.Problem,
            ["M01.unsigned-startup"] = FindingStatus.Warning,
            ["M01.modified-image"] = FindingStatus.Problem,
            ["M01.activation"] = FindingStatus.Ok,
            ["M01.residual-policies"] = FindingStatus.Improvable,
        };
        foreach (var (id, status) in expected)
        {
            var finding = findings.Single(f => f.Id == id);
            Assert.True(finding.Status == status, $"{id} : {finding.Status} au lieu de {status} ({finding.Current})");
        }

        Assert.Equal(Severity.Critical, findings.Single(f => f.Id == "M01.defender-realtime").Severity);
        Assert.Contains("stratégie", findings.Single(f => f.Id == "M01.defender-realtime").Current, StringComparison.Ordinal);
        Assert.True(findings.Single(f => f.Id == "M01.defender-realtime").Fixable);
        Assert.False(findings.Single(f => f.Id == "M01.defender-tamper").Fixable);
        var exclusions = findings.Single(f => f.Id == "M01.defender-exclusions").Current!;
        Assert.Contains("powershell.exe", exclusions, StringComparison.Ordinal);
        Assert.DoesNotContain("Steam", exclusions, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", findings.Single(f => f.Id == "M01.wu-server").Current, StringComparison.Ordinal);
        Assert.Contains("3 ligne(s)", findings.Single(f => f.Id == "M01.hosts").Current, StringComparison.Ordinal);
        Assert.Contains("Audiosrv : désactivé", findings.Single(f => f.Id == "M01.core-services").Current, StringComparison.Ordinal);
        Assert.Contains("evil.exe", findings.Single(f => f.Id == "M01.winlogon").Current, StringComparison.Ordinal);
        Assert.Contains("MsMpEng.exe", findings.Single(f => f.Id == "M01.ifeo-debugger").Current, StringComparison.Ordinal);
        Assert.Contains("Steam", findings.Single(f => f.Id == "M01.unsigned-startup").Current, StringComparison.Ordinal);
        Assert.Contains(@"Windows\Explorer\NoControlPanel", findings.Single(f => f.Id == "M01.residual-policies").Current, StringComparison.Ordinal);
        Assert.Contains("Schedule Scan", findings.Single(f => f.Id == "M01.wu-tasks").Current, StringComparison.Ordinal);
        var timer = findings.Single(f => f.Id == "M01.boot-timer").Current!;
        Assert.Contains("useplatformclock", timer, StringComparison.Ordinal);
        Assert.DoesNotContain("useplatformtick", timer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Third_party_antivirus_makes_a_passive_defender_normal()
    {
        var pc = new M01Pc();
        pc.Cim
            .Answer(M01Pc.AntivirusQuery, CimScopes.SecurityCenter2, M01Pc.Product("Windows Defender", 393472u), M01Pc.Product("ESET Security", 266240u))
            .Answer(RiskyChangesAuditModule.DefenderStatusQuery, CimScopes.Defender, new Dictionary<string, object?> { ["RealTimeProtectionEnabled"] = false, ["IsTamperProtected"] = false });
        pc.Services["WinDefend"] = 4;

        var findings = await pc.RunAsync();

        var realtime = findings.Single(f => f.Id == "M01.defender-realtime");
        Assert.Equal(FindingStatus.Info, realtime.Status);
        Assert.Contains("ESET", realtime.Current, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M01.defender-tamper").Status);
        var services = findings.Single(f => f.Id == "M01.core-services");
        Assert.Equal(FindingStatus.Warning, services.Status);
        Assert.Equal(Severity.Medium, services.Severity);
    }

    [Fact]
    public async Task Inactive_third_party_antivirus_does_not_excuse_a_disabled_defender()
    {
        var pc = new M01Pc();
        pc.Cim
            .Answer(M01Pc.AntivirusQuery, CimScopes.SecurityCenter2, M01Pc.Product("Windows Defender", 397568u), M01Pc.Product("Malwarebytes", 393232u))
            .Answer(RiskyChangesAuditModule.DefenderStatusQuery, CimScopes.Defender, new Dictionary<string, object?> { ["RealTimeProtectionEnabled"] = false, ["IsTamperProtected"] = true });

        var realtime = await pc.RunAsync("M01.defender-realtime");

        Assert.Equal(FindingStatus.Problem, realtime.Status);
        Assert.Equal("désactivée", realtime.Current);
    }

    [Fact]
    public async Task Unreadable_defender_with_no_active_antivirus_is_critical()
    {
        var pc = new M01Pc();
        pc.Cim
            .Throw(RiskyChangesAuditModule.DefenderStatusQuery, new DataSourceUnavailableException("absent"), CimScopes.Defender)
            .Answer(M01Pc.AntivirusQuery, CimScopes.SecurityCenter2, M01Pc.Product("Windows Defender", 393472u));

        var findings = await pc.RunAsync();

        Assert.Equal(FindingStatus.Problem, findings.Single(f => f.Id == "M01.defender-realtime").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.defender-tamper").Status);
    }

    [Fact]
    public async Task Denied_defender_and_security_center_give_admin_required()
    {
        var pc = new M01Pc();
        pc.Cim
            .Throw(RiskyChangesAuditModule.DefenderStatusQuery, new MausAccessDeniedException("refusé"), CimScopes.Defender)
            .Throw(RiskyChangesAuditModule.DefenderPreferenceQuery, new MausAccessDeniedException("refusé"), CimScopes.Defender)
            .Throw(M01Pc.AntivirusQuery, new MausAccessDeniedException("refusé"), CimScopes.SecurityCenter2);

        var findings = await pc.RunAsync();

        foreach (var id in new[] { "M01.defender-realtime", "M01.defender-tamper", "M01.defender-exclusions" })
        {
            var finding = findings.Single(f => f.Id == id);
            Assert.Equal(FindingStatus.Unknown, finding.Status);
            Assert.Contains("administrateur", finding.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Targeted_exclusions_are_information_only()
    {
        var pc = new M01Pc();
        pc.Cim.Answer(RiskyChangesAuditModule.DefenderPreferenceQuery, CimScopes.Defender, new Dictionary<string, object?>
        {
            ["ExclusionPath"] = GamePath,
            ["ExclusionProcess"] = ToolProcess,
        });

        var finding = await pc.RunAsync("M01.defender-exclusions");

        Assert.Equal(FindingStatus.Info, finding.Status);
        Assert.StartsWith("2 exclusion(s)", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managed_pc_is_announced_first_and_organisation_settings_become_information()
    {
        var pc = new M01Pc { HardwareManaged = true };
        pc.Cim.Answer(M01Pc.DomainQuery, new Dictionary<string, object?> { ["PartOfDomain"] = true });
        pc.Registry
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "UseWUServer", 1)
            .Set(Hklm, M01Pc.WuPolicy, "WUServer", "https://wsus.entreprise.local:8531")
            .Set(Hkcu, M01Pc.InternetSettings, "AutoConfigURL", "http://proxy.entreprise.local/proxy.pac")
            .Set(Hklm, M01Pc.Policies + @"\Windows\Explorer", "NoControlPanel", 1);

        var findings = await pc.RunAsync();

        Assert.Equal("M01.managed-pc", findings[0].Id);
        Assert.Equal(FindingStatus.Info, findings[0].Status);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M01.wu-server").Status);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M01.proxy").Status);
        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M01.residual-policies").Status);
    }

    [Fact]
    public async Task Unmanaged_pc_with_wsus_leftovers_is_flagged()
    {
        var pc = new M01Pc { HardwareManaged = false };
        pc.Registry
            .Set(Hklm, M01Pc.WuPolicy + @"\AU", "UseWUServer", 1)
            .Set(Hklm, M01Pc.WuPolicy, "WUServer", "http://127.0.0.1");

        var findings = await pc.RunAsync();

        Assert.DoesNotContain(findings, f => f.Id == "M01.managed-pc");
        Assert.Equal(FindingStatus.Problem, findings.Single(f => f.Id == "M01.wu-server").Status);
    }

    [Theory]
    [InlineData("2026-10-04T10:00:00Z", null, FindingStatus.Info)]
    [InlineData("2027-06-01T10:00:00Z", null, FindingStatus.Problem)]
    [InlineData("2026-01-01T10:00:00Z", null, FindingStatus.Ok)]
    [InlineData("pas une date", null, FindingStatus.Unknown)]
    [InlineData(null, 7300, FindingStatus.Problem)]
    [InlineData(null, 35, FindingStatus.Ok)]
    public async Task Update_pause_is_judged_against_the_five_week_limit(string? expiry, int? maxDays, FindingStatus status)
    {
        var pc = new M01Pc();
        if (expiry is not null)
        {
            pc.Registry.Set(Hklm, M01Pc.UxSettings, "PauseUpdatesExpiryTime", expiry);
        }

        if (maxDays is not null)
        {
            pc.Registry.Set(Hklm, M01Pc.UxSettings, "FlightSettingsMaxPauseDays", maxDays.Value);
        }

        var finding = await pc.RunAsync("M01.wu-pause");

        Assert.Equal(status, finding.Status);
        if (status == FindingStatus.Info)
        {
            Assert.Equal("en pause jusqu'au 4 octobre 2026", finding.Current);
        }
    }

    [Fact]
    public async Task Residual_update_server_values_without_activation_are_a_warning()
    {
        var pc = new M01Pc();
        pc.Registry.Set(Hklm, M01Pc.WuPolicy, "DoNotConnectToWindowsUpdateInternetLocations", 1);

        var finding = await pc.RunAsync("M01.wu-server");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.True(finding.Fixable);
    }

    [Fact]
    public async Task Inactive_target_version_values_are_an_optimisation()
    {
        var pc = new M01Pc();
        pc.Registry.Set(Hklm, M01Pc.WuPolicy, "TargetReleaseVersionInfo", "24H2");

        var finding = await pc.RunAsync("M01.wu-target-version");

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Contains("24H2", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_tasks_all_disabled_or_unreadable()
    {
        var disabled = new M01Pc();
        disabled.Tasks.Folders[M01Pc.UpdateTasks] = [new ScheduledTaskInfo("Report policies", false), new ScheduledTaskInfo("USO_UxBroker", false)];
        Assert.Equal(FindingStatus.Warning, (await disabled.RunAsync("M01.wu-tasks")).Status);

        var elevatedEmpty = new M01Pc();
        elevatedEmpty.Tasks.Folders.Clear();
        Assert.Equal(FindingStatus.Warning, (await elevatedEmpty.RunAsync("M01.wu-tasks")).Status);

        var denied = new M01Pc();
        denied.Tasks.Failure = new MausAccessDeniedException("refusé");
        Assert.Equal(FindingStatus.Unknown, (await denied.RunAsync("M01.wu-tasks")).Status);

        var missing = new M01Pc();
        missing.Tasks.Failure = new DataSourceUnavailableException("absent");
        Assert.Equal(FindingStatus.Unknown, (await missing.RunAsync("M01.wu-tasks")).Status);
    }

    [Fact]
    public async Task Firewall_nuances()
    {
        var domainOnly = new M01Pc();
        domainOnly.Cim.Answer(RiskyChangesAuditModule.FirewallProfileQuery, CimScopes.StandardCimv2, M01Pc.Profile("Domain", 0), M01Pc.Profile("Private", 1), M01Pc.Profile("Public", 2));
        var finding = await domainOnly.RunAsync("M01.firewall");
        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Contains("domaine", finding.Current, StringComparison.Ordinal);

        var thirdParty = new M01Pc();
        thirdParty.Cim
            .Answer(RiskyChangesAuditModule.FirewallProfileQuery, CimScopes.StandardCimv2, M01Pc.Profile("Domain", 0), M01Pc.Profile("Private", 0), M01Pc.Profile("Public", 0))
            .Answer(M01Pc.FirewallProductQuery, CimScopes.SecurityCenter2, M01Pc.Product("Norton Firewall", 266256u));
        Assert.Equal(FindingStatus.Info, (await thirdParty.RunAsync("M01.firewall")).Status);

        var unreadable = new M01Pc();
        unreadable.Cim.Throw(RiskyChangesAuditModule.FirewallProfileQuery, new MausAccessDeniedException("refusé"), CimScopes.StandardCimv2);
        Assert.Equal(FindingStatus.Unknown, (await unreadable.RunAsync("M01.firewall")).Status);

        var policy = new M01Pc();
        policy.Cim.Throw(RiskyChangesAuditModule.FirewallProfileQuery, new MausAccessDeniedException("refusé"), CimScopes.StandardCimv2);
        policy.Registry.Set(Hklm, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\StandardProfile", "EnableFirewall", 0);
        var byPolicy = await policy.RunAsync("M01.firewall");
        Assert.Equal(FindingStatus.Problem, byPolicy.Status);
        Assert.Contains("stratégie", byPolicy.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uac_prompt_without_secure_desktop_is_a_warning()
    {
        var pc = new M01Pc();
        pc.Registry.Set(Hklm, M01Pc.UacKey, "PromptOnSecureDesktop", 0);

        var finding = await pc.RunAsync("M01.uac-prompt");

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.Equal(Severity.Medium, finding.Severity);
    }

    [Fact]
    public async Task Service_start_types_only_flag_disabled_or_missing_services()
    {
        var search = new M01Pc();
        search.Services["WSearch"] = 4;
        search.Services["TrustedInstaller"] = 2;
        search.Services.Remove("UCPD");
        var core = await search.RunAsync("M01.core-services");
        Assert.Equal(FindingStatus.Warning, core.Status);
        Assert.Equal("WSearch : désactivé", core.Current);

        var missing = new M01Pc();
        missing.Services.Remove("TrustedInstaller");
        Assert.Equal(FindingStatus.Problem, (await missing.RunAsync("M01.core-services")).Status);

        var denied = new M01Pc();
        foreach (var name in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS" })
        {
            denied.Registry.Deny(Hklm, @"SYSTEM\CurrentControlSet\Services\" + name);
        }

        Assert.Equal(FindingStatus.Unknown, (await denied.RunAsync("M01.wu-services")).Status);
    }

    [Fact]
    public async Task Page_file_settings()
    {
        var manual = new M01Pc();
        manual.Cim
            .Answer(M01Pc.ComputerSystemQuery, new Dictionary<string, object?> { ["AutomaticManagedPagefile"] = false })
            .Answer(M01Pc.PageFileQuery, new Dictionary<string, object?> { ["Name"] = @"C:\pagefile.sys", ["InitialSize"] = 4096u, ["MaximumSize"] = 8192u });
        var finding = await manual.RunAsync("M01.pagefile");
        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Contains("4096 à 8192 Mo", finding.Current, StringComparison.Ordinal);

        var tiny = new M01Pc();
        tiny.Cim
            .Answer(M01Pc.ComputerSystemQuery, new Dictionary<string, object?> { ["AutomaticManagedPagefile"] = false })
            .Answer(M01Pc.PageFileQuery, new Dictionary<string, object?> { ["Name"] = @"C:\pagefile.sys", ["InitialSize"] = 16u, ["MaximumSize"] = 256u });
        Assert.Equal(FindingStatus.Warning, (await tiny.RunAsync("M01.pagefile")).Status);

        var systemSized = new M01Pc();
        systemSized.Cim
            .Answer(M01Pc.ComputerSystemQuery, new Dictionary<string, object?> { ["AutomaticManagedPagefile"] = false })
            .Answer(M01Pc.PageFileQuery, new Dictionary<string, object?> { ["Name"] = @"D:\pagefile.sys", ["InitialSize"] = 0u, ["MaximumSize"] = 0u });
        Assert.Equal(FindingStatus.Ok, (await systemSized.RunAsync("M01.pagefile")).Status);

        var unreadable = new M01Pc();
        unreadable.Cim.Answer(M01Pc.ComputerSystemQuery);
        Assert.Equal(FindingStatus.Unknown, (await unreadable.RunAsync("M01.pagefile")).Status);
    }

    [Fact]
    public async Task Startup_entries_without_program_are_an_optimisation()
    {
        var pc = new M01Pc();
        pc.Registry.Set(Hkcu, M01Pc.HkcuRun, "Ancien", @"C:\Program Files\Ancien\ancien.exe /tray");

        var finding = await pc.RunAsync("M01.unsigned-startup");

        Assert.Equal(FindingStatus.Improvable, finding.Status);
        Assert.Contains("Ancien", finding.Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modified_image_clues()
    {
        var winRe = new M01Pc();
        winRe.Commands.Answer("reagentc.exe /info", "Windows RE status:         Disabled\r\nWindows RE location:\r\n");
        var single = await winRe.RunAsync("M01.modified-image");
        Assert.Equal(FindingStatus.Warning, single.Status);
        Assert.Contains("reagentc /enable", single.Advice, StringComparison.Ordinal);

        var tiny11 = new M01Pc();
        tiny11.Services.Remove("WinDefend");
        tiny11.Services.Remove("wuauserv");
        var stripped = await tiny11.RunAsync("M01.modified-image");
        Assert.Equal(FindingStatus.Problem, stripped.Status);
        Assert.Contains("image modifiée", stripped.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Volume:GVLK", 1u, "kms.example.net", "Professional", FindingStatus.Info)]
    [InlineData("Volume:GVLK", 1u, null, "Enterprise", FindingStatus.Ok)]
    [InlineData("OEM:DM", 1u, null, "Core", FindingStatus.Ok)]
    [InlineData("Retail", 5u, null, "Professional", FindingStatus.Info)]
    public async Task Activation_channel_is_reported_neutrally(string channel, uint status, string? kms, string edition, FindingStatus expected)
    {
        var pc = new M01Pc { Windows = new WindowsInfo("Windows 11", edition, "25H2", 26200, 6584) };
        pc.Cim.Answer(RiskyChangesAuditModule.ActivationQuery, M01Pc.License(channel, status, kms));

        var finding = await pc.RunAsync("M01.activation");

        Assert.Equal(expected, finding.Status);
        Assert.NotEqual(FindingStatus.Problem, finding.Status);
        Assert.Contains("ne modifie jamais", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_licensing_data_is_unknown()
    {
        var pc = new M01Pc();
        pc.Cim.Answer(RiskyChangesAuditModule.ActivationQuery);

        Assert.Equal(FindingStatus.Unknown, (await pc.RunAsync("M01.activation")).Status);
    }

    [Fact]
    public async Task Winlogon_accepts_environment_variables_and_flags_user_shell()
    {
        var variables = new M01Pc();
        variables.Registry.Set(Hklm, M01Pc.Winlogon, "Userinit", @"%SystemRoot%\system32\userinit.exe");
        Assert.Equal(FindingStatus.Ok, (await variables.RunAsync("M01.winlogon")).Status);

        var userShell = new M01Pc();
        userShell.Registry.Set(Hkcu, M01Pc.Winlogon, "Shell", @"C:\Users\Public\shell.exe");
        Assert.Equal(FindingStatus.Problem, (await userShell.RunAsync("M01.winlogon")).Status);
    }

    [Fact]
    public async Task Inactive_appinit_list_is_an_optimisation()
    {
        var pc = new M01Pc();
        pc.Registry.Set(Hklm, M01Pc.AppInit, "AppInit_DLLs", @"C:\Anciens\hook.dll");

        Assert.Equal(FindingStatus.Improvable, (await pc.RunAsync("M01.appinit")).Status);
    }

    [Fact]
    public async Task System_proxy_is_read_from_netsh_and_unreadable_output_is_unknown()
    {
        var pc = new M01Pc();
        pc.Commands.Answer("netsh.exe winhttp show proxy", "Current WinHTTP proxy settings:\r\n\r\n    Proxy Server(s) :  10.0.0.1:3128\r\n    Bypass List     :  (none)\r\n");
        var proxy = await pc.RunAsync("M01.proxy");
        Assert.Equal(FindingStatus.Problem, proxy.Status);
        Assert.Contains("10.0.0.1:3128", proxy.Current, StringComparison.Ordinal);

        var garbled = new M01Pc();
        garbled.Commands.Answer("netsh.exe winhttp show proxy", "???");
        Assert.Equal(FindingStatus.Unknown, (await garbled.RunAsync("M01.proxy")).Status);

        var failed = new M01Pc();
        failed.Commands.Answer("netsh.exe winhttp show proxy", string.Empty, exitCode: 1);
        Assert.Equal(FindingStatus.Unknown, (await failed.RunAsync("M01.proxy")).Status);
    }

    [Fact]
    public async Task Hosts_file_without_microsoft_lines_or_absent_is_compliant()
    {
        var pc = new M01Pc();
        pc.Files.AddFile(M01Pc.Hosts, "127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n# 0.0.0.0 microsoft.com\r\n");
        Assert.Equal(FindingStatus.Ok, (await pc.RunAsync("M01.hosts")).Status);

        var context = TestContext.Create(files: new FakeFiles());
        var findings = await new RiskyChangesAuditModule(new FakeSignatureVerifier(), new FakeScheduledTasks(), M01Pc.WindowsDirectory).DetectAsync(context, CancellationToken.None);
        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M01.hosts").Status);
    }

    [Fact]
    public async Task WebView2_and_edge_presence()
    {
        var noEdge = new M01Pc();
        noEdge.Registry.Set(Hklm, M01Pc.Edge, "pv", null);
        Assert.Equal(FindingStatus.Info, (await noEdge.RunAsync("M01.webview2")).Status);

        var userWebView = new M01Pc();
        userWebView.Registry
            .Set(Hklm, M01Pc.WebView2, "pv", null)
            .Set(Hkcu, @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv", "139.0.1.0");
        Assert.Equal(FindingStatus.Ok, (await userWebView.RunAsync("M01.webview2")).Status);
    }

    [Fact]
    public async Task Empty_package_inventory_is_unknown()
    {
        var pc = new M01Pc();
        pc.Packages.Packages.Clear();

        Assert.Equal(FindingStatus.Unknown, (await pc.RunAsync("M01.system-apps")).Status);
    }

    [Fact]
    public async Task Boot_configuration_failures_and_legacy_boot()
    {
        var failed = new M01Pc();
        failed.Commands.Answer("bcdedit.exe /enum {current}", "Accès refusé.", exitCode: 1);
        failed.Registry.Set(Hklm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
        var findings = await failed.RunAsync();
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.boot-timer").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.secure-boot").Status);
        Assert.Equal(FindingStatus.Ok, findings.Single(f => f.Id == "M01.dep").Status);

        var nx = new M01Pc();
        nx.Commands.Answer("bcdedit.exe /enum {current}", M01Pc.BcdCurrent.Replace("OptIn", "AlwaysOff", StringComparison.Ordinal) + "nointegritychecks       Oui\r\n");
        var boot = await nx.RunAsync();
        Assert.Equal(FindingStatus.Problem, boot.Single(f => f.Id == "M01.dep").Status);
        Assert.Contains("nointegritychecks", boot.Single(f => f.Id == "M01.driver-signing").Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_policy_keys_give_admin_required_without_failing_the_module()
    {
        var pc = new M01Pc();
        pc.Registry.Deny(Hklm, M01Pc.WuPolicy).Deny(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\System");

        var findings = await pc.RunAsync();

        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.wu-server").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.wu-target-version").Status);
        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M01.smartscreen").Status);
        Assert.Equal(31, findings.Count);
    }

    [Fact]
    public async Task Accepted_policies_are_not_residual()
    {
        var pc = new M01Pc();
        pc.Registry
            .Set(Hklm, M01Pc.Policies + @"\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)
            .Set(Hklm, M01Pc.Policies + @"\Dsh", "AllowNewsAndInterests", 0)
            .Set(Hklm, M01Pc.Policies + @"\Windows\System", "EnableSmartScreen", 1)
            .Set(Hklm, M01Pc.Policies + @"\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1);

        Assert.Equal(FindingStatus.Ok, (await pc.RunAsync("M01.residual-policies")).Status);
        Assert.True(RiskyChangesAuditModule.IsAcceptedPolicy(@"Edge\HideFirstRunExperience"));
        Assert.False(RiskyChangesAuditModule.IsAcceptedPolicy(@"Windows\System\DisableCMD"));
        Assert.False(RiskyChangesAuditModule.IsAcceptedPolicy(@"EdgeUpdateX\Foo"));
    }
}
