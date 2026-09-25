using Maus.Core.Hardware;
using Maus.Core.Modules.M01Audit;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M01Audit;

/// <summary>Signatures simulées : « signé » par défaut, sauf chemins déclarés autrement.</summary>
internal sealed class FakeSignatureVerifier : ISignatureVerifier
{
    public Dictionary<string, SignatureStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SignatureStatus Verify(string path) => Statuses.GetValueOrDefault(path, SignatureStatus.Signed);
}

/// <summary>Planificateur simulé : tâches par dossier, ou exception à lever.</summary>
internal sealed class FakeScheduledTasks : IScheduledTaskReader
{
    public Dictionary<string, List<ScheduledTaskInfo>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Failure { get; set; }

    public IReadOnlyList<ScheduledTaskInfo> GetTasks(string folderPath) =>
        Failure is not null ? throw Failure : Folders.GetValueOrDefault(folderPath) ?? [];
}

/// <summary>
/// PC de test « propre » (Windows 11 25H2 non modifié, administrateur) que chaque test altère pour simuler une modification risquée.
/// Les services sont écrits dans le registre au lancement : un service retiré du dictionnaire n'a pas de clé.
/// </summary>
internal sealed class M01Pc
{
    public const string WindowsDirectory = @"C:\Windows";
    public const string Hosts = @"C:\Windows\System32\drivers\etc\hosts";
    public const string UpdateTasks = @"\Microsoft\Windows\UpdateOrchestrator";
    public const string WuPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string UxSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    public const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string AppInit = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    public const string HklmRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string HkcuRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public const string WebView2 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    public const string Edge = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
    public const string Policies = @"SOFTWARE\Policies\Microsoft";
    public const string AntivirusQuery = "SELECT displayName, productState FROM AntiVirusProduct";
    public const string FirewallProductQuery = "SELECT displayName, productState FROM FirewallProduct";
    public const string ComputerSystemQuery = "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem";
    public const string PageFileQuery = "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting";
    public const string DepQuery = "SELECT DataExecutionPrevention_SupportPolicy FROM Win32_OperatingSystem";
    public const string DomainQuery = "SELECT PartOfDomain FROM Win32_ComputerSystem";

    public const string WinHttpDirectFr = "\r\nParamètres de proxy WinHTTP actuels :\r\n\r\n    Accès direct (sans serveur proxy).\r\n";
    public const string ReAgentEnabledFr =
        "\r\nInformations de configuration de l'environnement de récupération Windows (Windows RE) et de réinitialisation du système :\r\n\r\n" +
        "    État de Windows RE :       Enabled\r\n    Emplacement de Windows RE :       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\r\n\r\n" +
        "REAGENTC.EXE : opération réussie.\r\n";
    public const string BcdCurrent =
        "\r\nWindows Boot Loader\r\n-------------------\r\nidentifier              {current}\r\ndevice                  partition=C:\r\n" +
        "path                    \\WINDOWS\\system32\\winload.efi\r\ndescription             Windows 11\r\nlocale                  fr-FR\r\n" +
        "osdevice                partition=C:\r\nsystemroot              \\WINDOWS\r\nnx                      OptIn\r\nbootmenupolicy          Standard\r\n";

    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    public M01Pc()
    {
        Registry
            .Set(Hklm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", 1)
            .Set(Hklm, @"SYSTEM\CurrentControlSet\Control", "SystemStartOptions", " NOEXECUTE=OPTIN  NOVGA")
            .Set(Hklm, WebView2, "pv", "140.0.3485.54")
            .Set(Hklm, Edge, "pv", "140.0.3485.54")
            .Set(Hklm, Winlogon, "Shell", "explorer.exe")
            .Set(Hklm, Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,")
            .Set(Hklm, AppInit, "AppInit_DLLs", string.Empty)
            .Set(Hklm, AppInit, "LoadAppInit_DLLs", 0)
            .Set(Hklm, UacKey, "EnableLUA", 1)
            .Set(Hklm, UacKey, "ConsentPromptBehaviorAdmin", 5)
            .Set(Hklm, UacKey, "PromptOnSecureDesktop", 1)
            .Set(Hklm, Policies + @"\TPM", "OSManagedAuthLevel", 5)
            .Set(Hklm, Policies + @"\Windows NT\Terminal Services\Client", "fEnableUsbBlockDeviceBySetupClass", 1)
            .Set(Hklm, Ifeo + @"\notepad.exe", "MitigationOptions", 0)
            .Set(Hklm, HklmRun, "SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe")
            .Set(Hkcu, HkcuRun, "Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent")
            .Set(Hkcu, InternetSettings, "ProxyEnable", 0);

        Cim
            .Answer(RiskyChangesAuditModule.DefenderStatusQuery, CimScopes.Defender, new Dictionary<string, object?>
            {
                ["RealTimeProtectionEnabled"] = true,
                ["IsTamperProtected"] = true,
                ["AntivirusEnabled"] = true,
                ["AMServiceEnabled"] = true,
            })
            .Answer(RiskyChangesAuditModule.DefenderPreferenceQuery, CimScopes.Defender, new Dictionary<string, object?>
            {
                ["ExclusionPath"] = null,
                ["ExclusionExtension"] = null,
                ["ExclusionProcess"] = null,
            })
            .Answer(AntivirusQuery, CimScopes.SecurityCenter2, Product("Windows Defender", 397568u))
            .Answer(RiskyChangesAuditModule.FirewallProfileQuery, CimScopes.StandardCimv2, Profile("Domain", 1), Profile("Private", 1), Profile("Public", 1))
            .Answer(ComputerSystemQuery, new Dictionary<string, object?> { ["AutomaticManagedPagefile"] = true })
            .Answer(DepQuery, new Dictionary<string, object?> { ["DataExecutionPrevention_SupportPolicy"] = 2u })
            .Answer(RiskyChangesAuditModule.ActivationQuery, License("Retail", 1));

        Commands
            .Answer("netsh.exe winhttp show proxy", WinHttpDirectFr)
            .Answer("bcdedit.exe /enum {current}", BcdCurrent)
            .Answer("reagentc.exe /info", ReAgentEnabledFr);

        Files
            .AddFile(Hosts, "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n#      102.54.94.97     rhino.acme.com          # source server\r\n# 127.0.0.1       localhost\r\n")
            .AddFile(@"C:\Windows\System32\SecurityHealthSystray.exe")
            .AddFile(@"C:\Program Files (x86)\Steam\steam.exe");

        Tasks.Folders[UpdateTasks] =
        [
            new ScheduledTaskInfo("Schedule Scan", true),
            new ScheduledTaskInfo("Schedule Scan Static Task", true),
            new ScheduledTaskInfo("Reboot_Battery", false),
            new ScheduledTaskInfo("USO_UxBroker", true),
        ];
    }

    public Dictionary<string, int> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = 3,
        ["UsoSvc"] = 2,
        ["WaaSMedicSvc"] = 3,
        ["BITS"] = 3,
        ["CryptSvc"] = 2,
        ["TrustedInstaller"] = 3,
        ["Audiosrv"] = 2,
        ["WSearch"] = 2,
        ["WinDefend"] = 2,
        ["mpssvc"] = 2,
        ["UCPD"] = 1,
    };

    public FakeRegistry Registry { get; } = new();

    public FakeCim Cim { get; } = new();

    public FakeCommands Commands { get; } = new();

    public FakeFiles Files { get; } = new();

    public FakePackages Packages { get; } = new FakePackages()
        .Add("Microsoft.WindowsStore")
        .Add("Microsoft.SecHealthUI")
        .Add("Microsoft.DesktopAppInstaller")
        .Add("Microsoft.WindowsCalculator");

    public FakeScheduledTasks Tasks { get; } = new();

    public FakeSignatureVerifier Signatures { get; } = new();

    public bool Elevated { get; set; } = true;

    public bool HardwareManaged { get; set; }

    public WindowsInfo Windows { get; set; } = new("Windows 11 Pro", "Professional", "25H2", 26200, 6584);

    public DateTimeOffset Now { get; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2));

    public static Dictionary<string, object?> Product(string name, uint state) => new() { ["displayName"] = name, ["productState"] = state };

    public static Dictionary<string, object?> Profile(string name, ushort enabled) => new() { ["Name"] = name, ["Enabled"] = enabled };

    public static Dictionary<string, object?> License(string channel, uint status, string? kms = null) => new()
    {
        ["Name"] = "Windows(R), Professional edition",
        ["ProductKeyChannel"] = channel,
        ["KeyManagementServiceMachine"] = kms,
        ["DiscoveredKeyManagementServiceMachineName"] = null,
        ["LicenseStatus"] = status,
    };

    public RiskyChangesAuditModule Module => new(Signatures, Tasks, WindowsDirectory);

    public async Task<IReadOnlyList<Finding>> RunAsync() => await Module.DetectAsync(CreateContext(), CancellationToken.None);

    /// <summary>Écrit les services dans le registre, puis construit le contexte d'audit.</summary>
    public AuditContext CreateContext()
    {
        foreach (var (name, start) in Services)
        {
            Registry.Set(Hklm, @"SYSTEM\CurrentControlSet\Services\" + name, "Start", start);
        }

        return TestContext.Create(
            Registry,
            Cim,
            Commands,
            hardware: new HardwareProfile { FormFactor = FormFactor.Desktop, IsManaged = HardwareManaged },
            windows: Windows,
            elevated: Elevated,
            now: Now,
            packages: Packages,
            files: Files);
    }

    public async Task<Finding> RunAsync(string id) => (await RunAsync()).Single(f => f.Id == id);
}
