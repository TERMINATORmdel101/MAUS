using System.Buffers.Binary;
using System.Text;
using Maus.Core.Modules.M12Startup;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M12Startup;

public class StartupAppsModuleTests
{
    private const string Run = StartupAppsModule.RunPath;
    private const string RunOnce = StartupAppsModule.RunOncePath;
    private const string Approved = StartupAppsModule.ApprovedPath;
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string DiscordExe = @"C:\Users\Bob\AppData\Local\Discord\Update.exe";

    private static readonly DateTime DisabledOn = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Module_metadata_follows_the_spec()
    {
        var module = new StartupAppsModule();

        Assert.Equal("M12", module.Id);
        Assert.Equal("Applications au démarrage", module.Title);
        Assert.Equal(120, module.Order);
    }

    [Fact]
    public async Task Enabled_game_launcher_can_be_disabled_without_problem()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Run, "Steam", $"\"{SteamExe}\" -silent");
        var files = new FakeFiles().AddFile(SteamExe).SetVersionInfo(SteamExe, "Valve Corporation", "Steam");

        var findings = await Detect(registry: registry, files: files);

        var steam = findings.Single(f => f.Id == "M12.hkcu-run-steam");
        Assert.Equal(FindingStatus.Improvable, steam.Status);
        Assert.Equal(Severity.Low, steam.Severity);
        Assert.Equal("Lanceurs de jeux", steam.Category);
        Assert.Equal("Steam (lanceur de jeux)", steam.Title);
        Assert.Equal("activé · Valve Corporation", steam.Current);
        Assert.Equal("désactivé", steam.Expected);
        Assert.True(steam.Fixable);
        Assert.Contains("mises à jour des jeux", steam.Explanation, StringComparison.Ordinal);
        Assert.Null(steam.SettingsPage);
        Assert.Contains("1 à désactiver sans problème", findings.Single(f => f.Id == "M12.summary").Current, StringComparison.Ordinal);
        Assert.Equal("ms-settings:startupapps", findings.Single(f => f.Id == "M12.summary").SettingsPage);
        Assert.Equal(FindingStatus.Improvable, findings[0].Status);
    }

    [Fact]
    public async Task Entry_disabled_in_startup_approved_is_compliant_and_shows_the_date()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Run, "Steam", $"\"{SteamExe}\" -silent")
            .Set(RegistryHive.CurrentUser, $@"{Approved}\Run", "Steam", DisabledValue(0x03, DisabledOn));
        var files = new FakeFiles().AddFile(SteamExe).SetVersionInfo(SteamExe, "Valve Corporation");

        var findings = await Detect(registry: registry, files: files);

        var steam = findings.Single(f => f.Id == "M12.hkcu-run-steam");
        Assert.Equal(FindingStatus.Ok, steam.Status);
        Assert.StartsWith($"désactivé le {DisabledOn.ToLocalTime():dd/MM/yyyy}", steam.Current, StringComparison.Ordinal);
        Assert.False(steam.Fixable);
        Assert.StartsWith("0 lancement(s) actif(s) sur 1 entrée(s)", findings.Single(f => f.Id == "M12.summary").Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_and_32_bit_entries_use_their_own_approval_keys()
    {
        const string securityExe = @"C:\Windows\System32\SecurityHealthSystray.exe";
        const string realtekExe = @"C:\Program Files\Realtek\Audio\HDA\RtkAudUService64.exe";
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, Run, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe")
            .Set(RegistryHive.LocalMachine, StartupAppsModule.Wow64RunPath, "RtkAudUService", $"\"{realtekExe}\" -background")
            .Set(RegistryHive.LocalMachine, $@"{Approved}\Run32", "RtkAudUService", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var files = new FakeFiles()
            .AddFile(securityExe).SetVersionInfo(securityExe, "Microsoft Corporation")
            .AddFile(realtekExe).SetVersionInfo(realtekExe, "Realtek Semiconductor");

        var findings = await Detect(registry: registry, files: files);

        var security = findings.Single(f => f.Id == "M12.hklm-run-securityhealth");
        Assert.Equal(FindingStatus.Ok, security.Status);
        Assert.Equal("Sécurité", security.Category);
        Assert.False(security.Fixable);
        var realtek = findings.Single(f => f.Id == "M12.hklm-run32-rtkauduservice");
        Assert.Equal(FindingStatus.Ok, realtek.Status);
        Assert.Equal("Pilotes et système", realtek.Category);
    }

    [Fact]
    public async Task Cloud_and_messaging_are_left_to_the_user()
    {
        const string oneDrive = @"C:\Users\Bob\AppData\Local\Microsoft\OneDrive\OneDrive.exe";
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Run, "OneDrive", $"\"{oneDrive}\" /background")
            .Set(RegistryHive.CurrentUser, Run, "Discord", $"{DiscordExe} --processStart Discord.exe");
        var files = new FakeFiles()
            .AddFile(oneDrive).SetVersionInfo(oneDrive, "Microsoft Corporation")
            .AddFile(DiscordExe).SetVersionInfo(DiscordExe, "Discord Inc.");

        var findings = await Detect(registry: registry, files: files);

        var cloud = findings.Single(f => f.Id == "M12.hkcu-run-onedrive");
        Assert.Equal(FindingStatus.Info, cloud.Status);
        Assert.False(cloud.Fixable);
        Assert.Contains("risque de perdre", cloud.Explanation, StringComparison.Ordinal);
        var discord = findings.Single(f => f.Id == "M12.hkcu-run-discord");
        Assert.Equal(FindingStatus.Info, discord.Status);
        Assert.Equal("Messagerie", discord.Category);
        Assert.True(discord.Fixable);
    }

    [Fact]
    public async Task Unknown_entry_is_neutral_and_shows_the_publisher()
    {
        const string exe = @"C:\Program Files\Contoso\agent.exe";
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Run, "ContosoAgent", $"\"{exe}\"");
        var files = new FakeFiles().AddFile(exe).SetVersionInfo(exe, "Contoso Ltd", "Contoso Agent");

        var findings = await Detect(registry: registry, files: files);

        var entry = findings.Single(f => f.Id == "M12.hkcu-run-contosoagent");
        Assert.Equal(FindingStatus.Info, entry.Status);
        Assert.Equal("Contoso Agent (non répertorié)", entry.Title);
        Assert.Equal("Inconnu", entry.Category);
        Assert.Equal("activé · Contoso Ltd", entry.Current);
        Assert.Contains("ne figure pas dans le catalogue", entry.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_pointing_to_a_deleted_program_is_improvable()
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Run, "OldTool", @"""C:\Program Files\OldTool\old.exe"" /tray");

        var findings = await Detect(registry: registry);

        var entry = findings.Single(f => f.Id == "M12.hkcu-run-oldtool");
        Assert.Equal(FindingStatus.Improvable, entry.Status);
        Assert.Contains("n'existe plus", entry.Explanation, StringComparison.Ordinal);
        Assert.Equal("activé · éditeur inconnu", entry.Current);
        Assert.Equal("ms-settings:startupapps", entry.SettingsPage);
    }

    [Theory]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp\x1.exe", "dossier temporaire")]
    [InlineData(@"wscript.exe //B C:\Users\Bob\AppData\Roaming\run.vbs", "hôte de scripts")]
    [InlineData(@"powershell.exe -WindowStyle Hidden -File C:\Users\Bob\a.ps1", "PowerShell")]
    [InlineData(@"C:\Users\Bob\AppData\Roaming\qzxkwvbtrp.exe", "nom aléatoire")]
    public async Task Suspicious_entries_are_warnings(string command, string reason)
    {
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, Run, "Updater", command);

        var findings = await Detect(registry: registry);

        var entry = findings.Single(f => f.Id == "M12.hkcu-run-updater");
        Assert.Equal(FindingStatus.Warning, entry.Status);
        Assert.Equal(Severity.Medium, entry.Severity);
        Assert.Equal("Suspect", entry.Category);
        Assert.Contains(reason, entry.Explanation, StringComparison.Ordinal);
        Assert.Contains("Module 1", entry.Advice, StringComparison.Ordinal);
        Assert.Null(entry.SettingsPage);
        Assert.False(entry.Fixable);
        Assert.Same(entry, findings[0]);
        Assert.Contains("1 suspecte(s)", findings.Single(f => f.Id == "M12.summary").Current, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suspicious_entry_already_disabled_is_only_information()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Run, "Updater", @"C:\Users\Bob\AppData\Local\Temp\x1.exe")
            .Set(RegistryHive.CurrentUser, $@"{Approved}\Run", "Updater", DisabledValue(0x03, DisabledOn));

        var findings = await Detect(registry: registry);

        Assert.Equal(FindingStatus.Info, findings.Single(f => f.Id == "M12.hkcu-run-updater").Status);
    }

    [Fact]
    public async Task Run_once_entries_are_display_only()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, RunOnce, "Setup", @"C:\Windows\System32\cleanup.exe /quiet");

        var findings = await Detect(registry: registry);

        var entry = findings.Single(f => f.Id == "M12.hklm-runonce-setup");
        Assert.Equal(FindingStatus.Info, entry.Status);
        Assert.StartsWith("exécution unique", entry.Current, StringComparison.Ordinal);
        Assert.False(entry.Fixable);
    }

    [Fact]
    public async Task Startup_folder_shortcuts_are_resolved_and_use_the_folder_approval()
    {
        var shortcut = FakeStartupEnvironment.UserFolder + @"\Discord.lnk";
        var environment = new FakeStartupEnvironment { CommonStartupFolder = null };
        environment.Shortcuts[shortcut] = DiscordExe;
        var files = new FakeFiles()
            .AddFile(shortcut)
            .AddFile(FakeStartupEnvironment.UserFolder + @"\desktop.ini")
            .AddFile(DiscordExe).SetVersionInfo(DiscordExe, "Discord Inc.");
        var registry = new FakeRegistry().Set(RegistryHive.CurrentUser, $@"{Approved}\StartupFolder", "Discord.lnk", DisabledValue(0x03, DisabledOn));

        var findings = await Detect(registry: registry, files: files, environment: environment);

        var entry = findings.Single(f => f.Id == "M12.user-folder-discord-lnk");
        Assert.Equal(FindingStatus.Ok, entry.Status);
        Assert.Equal("Messagerie", entry.Category);
        Assert.Contains(DiscordExe, entry.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Id.Contains("desktop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Store_startup_tasks_are_matched_by_package()
    {
        const string family = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, $@"{StartupAppsModule.StoreTasksPath}\{family}\Spotify", "State", 2)
            .Set(RegistryHive.CurrentUser, $@"{StartupAppsModule.StoreTasksPath}\Microsoft.WindowsTerminal_8wekyb3d8bbwe\StartTerminalOnLoginTask", "State", 1);
        var packages = new FakePackages();
        packages.Packages.Add(new InstalledPackage("SpotifyAB.SpotifyMusic", family, "1.2.3.0", "CN=5E8FC25E-3D06-4CF3-9D34-8C9D7C5A1C3B"));

        var findings = await Detect(registry: registry, packages: packages);

        var spotify = findings.Single(f => f.Id.StartsWith("M12.store-spotifyab", StringComparison.Ordinal));
        Assert.Equal(FindingStatus.Improvable, spotify.Status);
        Assert.Equal("activé · application du Store", spotify.Current);
        Assert.False(spotify.Fixable);
        Assert.Contains("ms-settings:startupapps", spotify.Advice, StringComparison.Ordinal);
        Assert.Equal("ms-settings:startupapps", spotify.SettingsPage);
        var terminal = findings.Single(f => f.Id.StartsWith("M12.store-microsoft-windowsterminal", StringComparison.Ordinal));
        Assert.Equal(FindingStatus.Ok, terminal.Status);
        Assert.Null(terminal.SettingsPage);
    }

    [Fact]
    public async Task Access_denied_source_is_reported_as_unknown_not_as_a_problem()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, Run);

        var findings = await Detect(registry: registry);

        var source = findings.Single(f => f.Id == "M12.source-hklm-run");
        Assert.Equal(FindingStatus.Unknown, source.Status);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning);
    }

    [Fact]
    public async Task Boot_time_comes_from_event_100_with_slow_apps_from_event_101()
    {
        var logs = new FakeEventLogs()
            .Add(StartupAppsModule.PerformanceLog, "Microsoft-Windows-Diagnostics-Performance", 100, new DateTime(2026, 9, 20, 8, 0, 0),
                new Dictionary<string, string> { ["BootTime"] = "45300", ["MainPathBootTime"] = "20100" })
            .Add(StartupAppsModule.PerformanceLog, "Microsoft-Windows-Diagnostics-Performance", 100, new DateTime(2026, 9, 1, 8, 0, 0),
                new Dictionary<string, string> { ["BootTime"] = "90000" })
            .Add(StartupAppsModule.PerformanceLog, "Microsoft-Windows-Diagnostics-Performance", 101, new DateTime(2026, 9, 20, 8, 0, 0),
                new Dictionary<string, string> { ["Name"] = "steam.exe", ["FriendlyName"] = "Steam", ["DegradationTime"] = "3200" });

        var findings = await Detect(eventLogs: logs);

        var boot = findings.Single(f => f.Id == "M12.boot-time");
        Assert.Equal(FindingStatus.Info, boot.Status);
        Assert.Equal("45,3 s, le 20/09/2026", boot.Current);
        Assert.Contains("20,1 s avant l'affichage du bureau", boot.Explanation, StringComparison.Ordinal);
        Assert.Contains("Steam (+3,2 s)", boot.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Boot_time_is_unknown_when_the_log_is_protected_or_empty()
    {
        var denied = await Detect(eventLogs: new FakeEventLogs().Deny(StartupAppsModule.PerformanceLog));
        var empty = await Detect();

        Assert.Equal(FindingStatus.Unknown, denied.Single(f => f.Id == "M12.boot-time").Status);
        Assert.Contains("administrateur", denied.Single(f => f.Id == "M12.boot-time").Explanation, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Unknown, empty.Single(f => f.Id == "M12.boot-time").Status);
    }

    [Fact]
    public async Task Third_party_logon_tasks_are_listed()
    {
        var cim = new FakeCim().Answer(StartupAppsModule.TasksQuery, StartupAppsModule.TaskSchedulerScope,
            TaskRow(@"\", "MSIAfterburner", 3, [LogonTrigger()], @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe", "/s"),
            TaskRow(@"\Microsoft\Windows\Shell\", "CreateObjectTask", 3, [LogonTrigger()], "explorer.exe"),
            TaskRow(@"\", "DisabledTool", 1, [LogonTrigger()], @"C:\Tools\tool.exe"),
            TaskRow(@"\", "SessionUnlock", 3, [SessionTrigger()], @"C:\Tools\unlock.exe"),
            TaskRow(@"\Vendor\", "DisabledTrigger", 3, [LogonTrigger(enabled: false)], @"C:\Tools\off.exe"));

        var findings = await Detect(cim: cim);

        var tasks = findings.Single(f => f.Id == "M12.logon-tasks");
        Assert.Equal(FindingStatus.Info, tasks.Status);
        Assert.Equal("1 tâche(s)", tasks.Current);
        Assert.Contains(@"MSIAfterburner (C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe /s)", tasks.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateObjectTask", tasks.Explanation, StringComparison.Ordinal);
        Assert.True(tasks.Fixable);
    }

    [Fact]
    public async Task No_logon_task_is_compliant_and_unavailable_scheduler_is_unknown()
    {
        var none = await Detect();
        var denied = await Detect(cim: new FakeCim().Throw(StartupAppsModule.TasksQuery, new MausAccessDeniedException("refusé"), StartupAppsModule.TaskSchedulerScope));
        var missing = await Detect(cim: new FakeCim().Throw(StartupAppsModule.TasksQuery, new DataSourceUnavailableException("absent"), StartupAppsModule.TaskSchedulerScope));

        Assert.Equal(FindingStatus.Ok, none.Single(f => f.Id == "M12.logon-tasks").Status);
        Assert.Equal(FindingStatus.Unknown, denied.Single(f => f.Id == "M12.logon-tasks").Status);
        Assert.Equal(FindingStatus.Unknown, missing.Single(f => f.Id == "M12.logon-tasks").Status);
    }

    [Fact]
    public async Task Only_non_microsoft_auto_services_are_counted()
    {
        const string vendorExe = @"C:\Program Files\Vendor\svc.exe";
        const string unsignedExe = @"C:\Program Files\Other\agent.exe";
        const string microsoftExe = @"C:\Program Files\Microsoft Office\ClickToRun.exe";
        var cim = new FakeCim().Answer(StartupAppsModule.ServicesQuery,
            Row(("Name", "Netman"), ("DisplayName", "Connexions réseau"), ("PathName", @"C:\WINDOWS\System32\svchost.exe -k LocalSystemNetworkRestricted -p")),
            Row(("Name", "VendorSvc"), ("DisplayName", "Vendor Service"), ("PathName", $"\"{vendorExe}\" -service")),
            Row(("Name", "OtherSvc"), ("DisplayName", "Other Agent"), ("PathName", unsignedExe)),
            Row(("Name", "ClickToRunSvc"), ("DisplayName", "Microsoft Office Click-to-Run"), ("PathName", $"\"{microsoftExe}\" /service")),
            Row(("Name", "Empty"), ("PathName", null)));
        var files = new FakeFiles()
            .SetVersionInfo(vendorExe, "Vendor Inc.")
            .SetVersionInfo(microsoftExe, "Microsoft Corporation");

        var findings = await Detect(cim: cim, files: files);

        var services = findings.Single(f => f.Id == "M12.third-party-services");
        Assert.Equal(FindingStatus.Info, services.Status);
        Assert.Equal("2 service(s) tiers en démarrage automatique", services.Current);
        Assert.Contains("Vendor Service (Vendor Inc.)", services.Explanation, StringComparison.Ordinal);
        Assert.Contains("Other Agent", services.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("Click-to-Run", services.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_query_denied_is_unknown()
    {
        var cim = new FakeCim().Throw(StartupAppsModule.ServicesQuery, new MausAccessDeniedException("refusé"));

        var findings = await Detect(cim: cim);

        Assert.Equal(FindingStatus.Unknown, findings.Single(f => f.Id == "M12.third-party-services").Status);
    }

    [Fact]
    public async Task Empty_pc_gives_a_summary_and_no_deviation()
    {
        var findings = await Detect();

        Assert.Equal("0 lancement(s) actif(s) sur 0 entrée(s), dont 0 à désactiver sans problème", findings.Single(f => f.Id == "M12.summary").Current);
        Assert.DoesNotContain(findings, f => f.Status is FindingStatus.Problem or FindingStatus.Warning or FindingStatus.Improvable);
        Assert.Equal(findings.Count, findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task Duplicate_names_get_unique_ids()
    {
        const string exe = @"C:\Program Files\Contoso\agent.exe";
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Run, "Agent", exe)
            .Set(RegistryHive.CurrentUser, Run, "Agent!", exe);
        var files = new FakeFiles().AddFile(exe).SetVersionInfo(exe, "Contoso Ltd");

        var findings = await Detect(registry: registry, files: files);

        Assert.Contains(findings, f => f.Id == "M12.hkcu-run-agent");
        Assert.Contains(findings, f => f.Id == "M12.hkcu-run-agent-2");
    }

    // --- Analyseurs ---

    [Fact]
    public void Approval_value_is_parsed_like_the_task_manager()
    {
        Assert.True(StartupParsers.ParseApproval(null).Enabled);
        Assert.True(StartupParsers.ParseApproval([]).Enabled);
        Assert.True(StartupParsers.ParseApproval([0x02, 0, 0, 0]).Enabled);
        Assert.True(StartupParsers.ParseApproval([0x06, 0, 0, 0]).Enabled);

        var disabled = StartupParsers.ParseApproval(DisabledValue(0x03, DisabledOn));
        Assert.False(disabled.Enabled);
        Assert.Equal(DisabledOn, disabled.DisabledOnUtc);

        var shortValue = StartupParsers.ParseApproval([0x03, 0, 0, 0]);
        Assert.False(shortValue.Enabled);
        Assert.Null(shortValue.DisabledOnUtc);

        var zeroDate = StartupParsers.ParseApproval([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.False(zeroDate.Enabled);
        Assert.Null(zeroDate.DisabledOnUtc);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" -x --y", @"C:\Program Files\App\app.exe", "-x --y")]
    [InlineData(@"C:\Program Files\App\app.exe -minimized", @"C:\Program Files\App\app.exe", "-minimized")]
    [InlineData(@"rundll32.exe C:\x.dll,Entry", "rundll32.exe", @"C:\x.dll,Entry")]
    [InlineData(@"""C:\App\app.exe", @"C:\App\app.exe", "")]
    [InlineData("tool /quiet", "tool", "/quiet")]
    [InlineData("   ", "", "")]
    public void Command_lines_are_split_into_program_and_arguments(string command, string executable, string arguments)
    {
        var (exe, args) = StartupParsers.SplitCommand(command);

        Assert.Equal(executable, exe);
        Assert.Equal(arguments, args);
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301.exe", true)]
    [InlineData("a7f3c9e1b2d4f6a8.exe", true)]
    [InlineData("x9k2p7q4m1.exe", true)]
    [InlineData("qzxkwvbtrp.exe", true)]
    [InlineData("OneDrive.exe", false)]
    [InlineData("Discord.exe", false)]
    [InlineData("updater.exe", false)]
    [InlineData("gjagent.exe", false)]
    public void Random_looking_names_are_detected(string fileName, bool expected)
    {
        Assert.Equal(expected, StartupParsers.LooksRandom(fileName));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", "", "Contoso", false)]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp\a.exe", "", "Contoso", true)]
    [InlineData("mshta.exe", "http://x", null, true)]
    [InlineData("powershell.exe", "-enc SQBFAFgA", null, true)]
    [InlineData("powershell.exe", @"-File C:\Scripts\backup.ps1", null, false)]
    [InlineData(@"C:\Users\Bob\Documents\start.vbs", "", null, true)]
    [InlineData(@"C:\Users\Bob\AppData\Roaming\qzxkwvbtrp.exe", "", "Contoso", false)]
    public void Suspicion_rules_follow_the_spec(string executable, string arguments, string? company, bool suspicious)
    {
        Assert.Equal(suspicious, StartupParsers.SuspicionReason(executable, arguments, company) is not null);
    }

    [Fact]
    public void Shortcut_target_is_read_from_ansi_link_info()
    {
        var data = Shortcut(@"C:\Program Files\Steam\steam.exe", unicodePath: null, withIdList: true);

        Assert.Equal(@"C:\Program Files\Steam\steam.exe", StartupParsers.ParseShortcutTarget(data));
    }

    [Fact]
    public void Shortcut_target_prefers_the_unicode_path()
    {
        var data = Shortcut(@"C:\Prog?\app.exe", unicodePath: @"C:\Progé\app.exe", withIdList: false);

        Assert.Equal(@"C:\Progé\app.exe", StartupParsers.ParseShortcutTarget(data));
    }

    [Fact]
    public void Shortcut_without_local_path_or_invalid_is_null()
    {
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);

        Assert.Null(StartupParsers.ParseShortcutTarget(header));
        Assert.Null(StartupParsers.ParseShortcutTarget([1, 2, 3]));
        Assert.Null(StartupParsers.ParseShortcutTarget(new byte[0x4C]));
    }

    [Theory]
    [InlineData("CN=Intel Corporation, O=Intel Corporation, L=Santa Clara, C=US", "Intel Corporation")]
    [InlineData("CN=\"Spotify AB\", O=Spotify AB", "Spotify AB")]
    [InlineData("CN=EB51A5DA-0E72-4863-82E4-EA21C1F8DFE3", null)]
    [InlineData("Contoso", "Contoso")]
    [InlineData("  ", null)]
    public void Package_publisher_is_shortened(string publisher, string? expected)
    {
        Assert.Equal(expected, StartupParsers.PublisherName(publisher));
    }

    [Theory]
    [InlineData("Steam", "steam")]
    [InlineData("com.squirrel.Teams.Teams", "com-squirrel-teams-teams")]
    [InlineData("  !!  ", "x")]
    [InlineData("Écran d'accueil", "cran-d-accueil")]
    public void Slugs_are_lowercase_and_dashed(string value, string expected)
    {
        Assert.Equal(expected, StartupParsers.Slug(value));
    }

    [Fact]
    public void Event_durations_are_converted_to_seconds()
    {
        var data = new Dictionary<string, string> { ["BootTime"] = "45300", ["Bad"] = "abc", ["Negative"] = "-5" };

        Assert.Equal(45.3, StartupParsers.ParseMilliseconds(data, "BootTime"));
        Assert.Null(StartupParsers.ParseMilliseconds(data, "Bad"));
        Assert.Null(StartupParsers.ParseMilliseconds(data, "Negative"));
        Assert.Null(StartupParsers.ParseMilliseconds(data, "Missing"));
    }

    [Fact]
    public void Logon_task_parser_reads_command_and_path()
    {
        var task = new CimRow(TaskRow(@"\Vendor\", "Tray", 4, [LogonTrigger()], @"C:\Vendor\tray.exe", null));

        var info = StartupParsers.ParseLogonTask(task);

        Assert.NotNull(info);
        Assert.Equal(@"Vendor\Tray", info.Name);
        Assert.Equal(@"C:\Vendor\tray.exe", info.Command);
    }

    [Fact]
    public void Catalog_is_consistent_and_matches_known_apps()
    {
        var catalog = StartupCatalog.LoadEmbedded();

        Assert.Equal(9, catalog.Families.Count);
        Assert.All(catalog.Apps, app => Assert.Contains(catalog.Families, f => f.Id == app.Family));
        Assert.All(catalog.Families, f => Assert.False(string.IsNullOrWhiteSpace(f.Loses)));
        Assert.Equal("security", catalog.Match("SecurityHealth", "SecurityHealthSystray.exe", null)?.Family.Id);
        Assert.Equal("games", catalog.Match("Launcher", "EpicGamesLauncher.exe", null)?.Family.Id);
        Assert.Equal("media", catalog.Match("x", null, "SpotifyAB.SpotifyMusic")?.Family.Id);
        Assert.Null(catalog.Match("ContosoAgent", "agent.exe", null));
    }

    // --- Outils de test ---

    private static async Task<IReadOnlyList<Finding>> Detect(
        FakeRegistry? registry = null,
        FakeFiles? files = null,
        FakeCim? cim = null,
        FakeEventLogs? eventLogs = null,
        FakePackages? packages = null,
        FakeStartupEnvironment? environment = null)
    {
        var context = TestContext.Create(registry: registry, cim: cim, files: files, eventLogs: eventLogs, packages: packages, elevated: false);
        var module = new StartupAppsModule(environment ?? new FakeStartupEnvironment());
        return await module.DetectAsync(context, CancellationToken.None);
    }

    private static byte[] DisabledValue(byte flag, DateTime utc)
    {
        var value = new byte[12];
        value[0] = flag;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(4), utc.ToFileTimeUtc());
        return value;
    }

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    private static CimRow LogonTrigger(bool enabled = true) =>
        new(Row(("Enabled", enabled), ("Delay", null), ("UserId", "Bob"), ("Id", null)));

    private static CimRow SessionTrigger() =>
        new(Row(("Enabled", true), ("Delay", null), ("UserId", "Bob"), ("StateChange", 8u)));

    private static Dictionary<string, object?> TaskRow(string path, string name, int state, CimRow[] triggers, string execute, string? arguments = null) =>
        Row(
            ("TaskPath", path),
            ("TaskName", name),
            ("State", (uint)state),
            ("Triggers", triggers),
            ("Actions", new[] { new CimRow(Row(("Execute", execute), ("Arguments", arguments), ("WorkingDirectory", null))) }));

    /// <summary>Raccourci .lnk minimal (en-tête, liste d'identifiants facultative, LinkInfo avec chemin local).</summary>
    private static byte[] Shortcut(string ansiPath, string? unicodePath, bool withIdList)
    {
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), withIdList ? 0x3u : 0x2u);
        byte[] idList = withIdList ? [4, 0, 0xAA, 0xBB, 0xCC, 0xDD] : [];

        var headerSize = unicodePath is null ? 0x1C : 0x24;
        var ansi = Encoding.Latin1.GetBytes(ansiPath + "\0");
        var unicode = unicodePath is null ? [] : Encoding.Unicode.GetBytes(unicodePath + "\0");
        var baseOffset = headerSize;
        var suffixOffset = baseOffset + ansi.Length;
        var unicodeOffset = suffixOffset + 1;
        var unicodeSuffixOffset = unicodeOffset + unicode.Length;
        var size = unicodePath is null ? suffixOffset + 1 : unicodeSuffixOffset + 2;

        var info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        ansi.CopyTo(info, baseOffset);
        if (unicodePath is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)unicodeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), (uint)unicodeSuffixOffset);
            unicode.CopyTo(info, unicodeOffset);
        }

        return [.. header, .. idList, .. info];
    }
}
