using Maus.Core.Modules.M01Audit;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Modules.M01Audit;

public class RiskyChangesParsersTests
{
    private static readonly string[] ExpectedHosts = ["vortex.data.microsoft.com", "settings-win.data.microsoft.com", "download.windowsupdate.com", "login.live.com.", "www.msftconnecttest.com"];
    private static readonly string[] HiddenList = ["N/A: Must be an administrator to view exclusions"];
    private static readonly string[] VisiblePaths = [@"D:\Jeux", " "];
    private static readonly string[] IsoExtension = [".iso"];
    private static readonly string[] ExpectedExclusions = [@"D:\Jeux", "extension .iso"];

    [Fact]
    public void Bcdedit_output_is_parsed_with_localised_values()
    {
        var elements = BcdEditParser.Parse(M01Pc.BcdCurrent + "testsigning             Oui\r\nuseplatformclock        No\r\nnointegritychecks       Yes\r\n");

        Assert.Equal("OptIn", elements["nx"]);
        Assert.True(BcdEditParser.IsYes(elements, "testsigning"));
        Assert.True(BcdEditParser.IsYes(elements, "nointegritychecks"));
        Assert.False(BcdEditParser.IsYes(elements, "useplatformclock"));
        Assert.False(BcdEditParser.IsYes(elements, "disabledynamictick"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Le magasin de données de configuration de démarrage n'a pas pu être ouvert.\r\nAccès refusé.\r\n")]
    public void Bcdedit_errors_give_no_elements(string? output) => Assert.Empty(BcdEditParser.Parse(output));

    [Theory]
    [InlineData(M01Pc.WinHttpDirectFr, "Direct", null)]
    [InlineData("\r\nCurrent WinHTTP proxy settings:\r\n\r\n    Direct access (no proxy server).\r\n", "Direct", null)]
    [InlineData("\r\nParamètres de proxy WinHTTP actuels :\r\n\r\n    Serveur(s) proxy :  proxy.local:8080\r\n    Liste de contournement     :  (aucun)\r\n", "Proxy", "proxy.local:8080")]
    [InlineData("\r\nCurrent WinHTTP proxy settings:\r\n\r\n    Proxy Server(s) :  10.0.0.1:3128\r\n    Bypass List     :  <local>\r\n", "Proxy", "10.0.0.1:3128")]
    [InlineData("Erreur inattendue", "Unknown", null)]
    [InlineData(null, "Unknown", null)]
    public void Winhttp_proxy_output_is_parsed_in_french_and_english(string? output, string kind, string? server)
    {
        var proxy = WinHttpProxyParser.Parse(output);

        Assert.Equal(Enum.Parse<WinHttpProxyKind>(kind), proxy.Kind);
        Assert.Equal(server, proxy.Server);
    }

    [Theory]
    [InlineData(M01Pc.ReAgentEnabledFr, true)]
    [InlineData("    Windows RE status:         Enabled\r\n", true)]
    [InlineData("    Windows RE status:         Disabled\r\n", false)]
    [InlineData("    État de Windows RE :       Activé\r\n", true)]
    [InlineData("    État de Windows RE :       Désactivé\r\n", false)]
    [InlineData("REAGENTC.EXE : échec de l'opération. 5\r\n", null)]
    [InlineData(null, null)]
    public void Reagentc_state_is_parsed(string? output, bool? enabled) => Assert.Equal(enabled, ReAgentInfoParser.ParseEnabled(output));

    [Fact]
    public void Hosts_file_keeps_only_active_microsoft_lines()
    {
        const string content =
            "# 127.0.0.1 microsoft.com\r\n" +
            "127.0.0.1 localhost\r\n" +
            "0.0.0.0 vortex.data.microsoft.com settings-win.data.microsoft.com # télémétrie\r\n" +
            "0.0.0.0\tdownload.windowsupdate.com\r\n" +
            "0.0.0.0 notmicrosoft.com\r\n" +
            "0.0.0.0 login.live.com.\r\n" +
            "0.0.0.0 www.msftconnecttest.com\r\n";

        var entries = HostsFileParser.FindMicrosoftEntries(content);

        Assert.Equal(
            ExpectedHosts,
            entries.Select(e => e.HostName));
        Assert.All(entries, e => Assert.Equal("0.0.0.0", e.Address));
        Assert.Empty(HostsFileParser.FindMicrosoftEntries(null));
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", @"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files\Outil\outil.exe /tray", @"C:\Program Files\Outil\outil.exe")]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", @"C:\Windows\System32\SecurityHealthSystray.exe")]
    [InlineData("rundll32 shell32.dll,Control_RunDLL", "rundll32")]
    [InlineData("   ", null)]
    [InlineData("\"sans fin", null)]
    public void Startup_command_executable_is_extracted(string command, string? executable) =>
        Assert.Equal(executable, StartupCommandParser.ExtractExecutable(command));

    [Theory]
    [InlineData(397568L, true)]
    [InlineData(266240L, true)]
    [InlineData(393472L, false)]
    [InlineData(393232L, false)]
    [InlineData(0L, false)]
    public void Security_center_product_state_bits(long state, bool active) =>
        Assert.Equal(active, new SecurityProduct("Produit", state).IsActive);

    [Fact]
    public void Microsoft_products_are_not_third_party()
    {
        var products = new[] { new SecurityProduct("Windows Defender", 397568), new SecurityProduct("Pare-feu Windows", 266256) };

        Assert.Null(SecurityProduct.ActiveThirdParty(products));
        Assert.Null(SecurityProduct.ActiveThirdParty(null));
        Assert.Equal("Bitdefender", SecurityProduct.ActiveThirdParty([.. products, new SecurityProduct("Bitdefender", 266240)])!.Name);
    }

    [Theory]
    [InlineData("Path", @"C:\", true)]
    [InlineData("Path", @"C:\Users", true)]
    [InlineData("Path", @"C:\Users\Alex\Downloads\", true)]
    [InlineData("Path", "%TEMP%", true)]
    [InlineData("Path", @"C:\Users\Alex\AppData\Local\Temp\*", true)]
    [InlineData("Path", @"C:\Program Files", true)]
    [InlineData("Path", @"D:\Jeux\Steam\steamapps", false)]
    [InlineData("Path", @"C:\Users\Alex\source\repos", false)]
    [InlineData("Extension", ".exe", true)]
    [InlineData("Extension", "ps1", true)]
    [InlineData("Extension", ".log", false)]
    [InlineData("Process", "powershell.exe", true)]
    [InlineData("Process", @"C:\Windows\System32\cmd.exe", true)]
    [InlineData("Process", @"D:\Jeux\jeu.exe", false)]
    public void Broad_exclusions_follow_the_microsoft_common_mistakes_list(string kind, string value, bool broad) =>
        Assert.Equal(broad, DefenderExclusionClassifier.IsBroad(new DefenderExclusion(Enum.Parse<ExclusionKind>(kind), value)));

    [Fact]
    public void Hidden_exclusions_are_not_mistaken_for_real_ones()
    {
        var hidden = new CimRow(new Dictionary<string, object?> { ["ExclusionPath"] = HiddenList });
        var visible = new CimRow(new Dictionary<string, object?> { ["ExclusionPath"] = VisiblePaths, ["ExclusionExtension"] = IsoExtension });

        Assert.Null(DefenderExclusion.ReadAll(hidden));
        Assert.Equal(ExpectedExclusions, DefenderExclusion.ReadAll(visible)!.Select(e => e.ToString()));
    }

    [Theory]
    [InlineData("explorer.exe", "explorer.exe", @"C:\Windows", true)]
    [InlineData(@"C:\WINDOWS\explorer.exe", "explorer.exe", @"C:\Windows", true)]
    [InlineData("explorer.exe, C:\\Users\\Public\\x.exe", "explorer.exe", @"C:\Windows", false)]
    [InlineData(@"C:\Windows\system32\userinit.exe,", "userinit.exe", @"C:\Windows\System32", true)]
    [InlineData(@"%windir%\system32\userinit.exe", "userinit.exe", @"C:\Windows\System32", true)]
    [InlineData(@"C:\Temp\userinit.exe,", "userinit.exe", @"C:\Windows\System32", false)]
    [InlineData(",", "userinit.exe", @"C:\Windows\System32", false)]
    public void Winlogon_defaults_are_recognised(string value, string fileName, string folder, bool expected) =>
        Assert.Equal(expected, RiskyChangesAuditModule.IsDefaultWinlogonEntry(value, fileName, folder, @"C:\Windows"));

    [Fact]
    public void Service_start_is_read_and_described()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\BITS", "Start", 4)
            .Deny(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Secret");

        var bits = ServiceStart.Read(registry, "BITS");
        var missing = ServiceStart.Read(registry, "wuauserv");
        var denied = ServiceStart.Read(registry, "Secret");

        Assert.True(bits.IsBroken);
        Assert.Equal("BITS : désactivé", bits.Describe());
        Assert.True(missing.IsBroken);
        Assert.Equal("wuauserv : absent", missing.Describe());
        Assert.False(denied.IsBroken);
        Assert.Equal("Secret : illisible", denied.Describe());
    }

    [Fact]
    public void Real_signature_verifier_distinguishes_signed_and_unsigned_files()
    {
        var verifier = new WinTrustSignatureVerifier();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        var unsigned = typeof(RiskyChangesParsersTests).Assembly.Location;

        if (File.Exists(notepad))
        {
            Assert.Equal(SignatureStatus.Signed, verifier.Verify(notepad));
        }

        Assert.Equal(SignatureStatus.Unsigned, verifier.Verify(unsigned));
    }
}
