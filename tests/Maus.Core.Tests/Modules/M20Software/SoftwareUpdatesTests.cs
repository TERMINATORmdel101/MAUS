using Maus.Core.Modules.M20Software;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Modules.M20Software;

public class WingetParserTests
{
    internal const string English =
        "   - \r   \\ \r   | \r                                                                                                                        \r" +
        "Name                               Id                           Version        Available      Source\n" +
        "-----------------------------------------------------------------------------------------------------\n" +
        "Mozilla Firefox (x64 fr)           Mozilla.Firefox              129.0.1        130.0          winget\n" +
        "7-Zip 23.01 (x64)                  7zip.7zip                    23.01          24.08          winget\n" +
        "Microsoft Visual Studio Code (Use… Microsoft.VisualStudioCode   1.92.2         1.93.1         winget\n" +
        "Some Very Long Application Name    Publisher.VeryLongIdentifie… 2.0            2.1            winget\n" +
        "4 upgrades available.\n" +
        "\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\n" +
        "Name      Id              Version Available Source\n" +
        "--------------------------------------------------\n" +
        "Pinned    Pinned.App      1.0     2.0       winget\n";

    internal const string French =
        "\u001b[0mNom                                ID                    Version   Disponible Source\r\n" +
        "--------------------------------------------------------------------------------------\r\n" +
        "Notepad++ (64-bit x64)             Notepad++.Notepad++   8.6.9     8.7        winget\r\n" +
        "Git                                Git.Git               2.45.2    2.46.0     winget\r\n" +
        "2 mises à niveau disponibles.\r\n";

    [Fact]
    public void Wide_characters_take_two_cells_in_japanese_tables()
    {
        const string japanese =
            "名前                ID                  バージョン  利用可能  ソース\n" +
            "----------------------------------------------------------------------\n" +
            "メモ帳アプリ        Contoso.Memo        1.0         2.0       winget\n" +
            "Notepad++           Notepad++.Notepad++ 8.6.9       8.7       winget\n";

        var updates = Winget.Parse(japanese);

        Assert.Equal(new SoftwareUpdate("メモ帳アプリ", "Contoso.Memo", "1.0", "2.0"), updates[0]);
        Assert.Equal(new SoftwareUpdate("Notepad++", "Notepad++.Notepad++", "8.6.9", "8.7"), updates[1]);
    }

    [Fact]
    public void Wide_characters_in_a_row_of_a_french_table_keep_the_columns()
    {
        const string french =
            "Nom            ID            Version Disponible Source\n" +
            "------------------------------------------------------\n" +
            "メモ帳         Contoso.Memo  1.0     2.0        winget\n";

        Assert.Equal(new SoftwareUpdate("メモ帳", "Contoso.Memo", "1.0", "2.0"), Assert.Single(Winget.Parse(french)));
    }

    [Fact]
    public void Header_of_several_words_is_refused_rather_than_misread()
    {
        const string korean =
            "이름       ID            버전  사용 가능  원본\n" +
            "----------------------------------------------\n" +
            "메모       Contoso.Memo  1.0   2.0        winget\n";

        Assert.Throws<DataSourceUnavailableException>(() => Winget.Parse(korean));
    }

    [Theory]
    [InlineData(":")]
    [InlineData("：")]
    public void Table_of_explicit_upgrades_alone_is_not_the_normal_list(string colon)
    {
        var output =
            "No installed package found matching input criteria.\n\n" +
            "The following packages have an upgrade available, but require explicit targeting for upgrade" + colon + "\n" +
            "Name      Id              Version Available Source\n" +
            "--------------------------------------------------\n" +
            "Pinned    Pinned.App      1.0     2.0       winget\n";

        Assert.Empty(Winget.Parse(output));
    }

    [Fact]
    public void English_table_is_read_by_column_positions()
    {
        var updates = Winget.Parse(English);

        Assert.Equal(4, updates.Count);
        Assert.Equal(new SoftwareUpdate("Mozilla Firefox (x64 fr)", "Mozilla.Firefox", "129.0.1", "130.0"), updates[0]);
        Assert.Equal("7zip.7zip", updates[1].Id);
        Assert.Equal("Microsoft Visual Studio Code (Use…", updates[2].Name);
        Assert.DoesNotContain(updates, u => u.Id == "Pinned.App");
    }

    [Fact]
    public void Truncated_or_odd_identifiers_are_never_targeted()
    {
        var updates = Winget.Parse(English);

        Assert.False(updates[3].CanTarget);
        Assert.True(updates[0].CanTarget);
        Assert.False(new SoftwareUpdate("x", "a&calc", "1", "2").CanTarget);
    }

    [Fact]
    public void French_headers_and_color_codes_are_handled()
    {
        var updates = Winget.Parse(French);

        Assert.Equal(["Notepad++.Notepad++", "Git.Git"], updates.Select(u => u.Id));
        Assert.Equal("8.7", updates[0].Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.\n")]
    [InlineData("Aucun package installé ne correspond aux critères d'entrée.\r\n")]
    public void No_table_means_no_update(string output) => Assert.Empty(Winget.Parse(output));

    [Fact]
    public void Console_updates_each_chosen_package_with_the_protected_winget()
    {
        const string path = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe\winget.exe";
        var arguments = Winget.UpgradeConsoleArguments(path, Winget.Parse(English));

        Assert.StartsWith("/s /k \"", arguments, StringComparison.Ordinal);
        Assert.EndsWith("\"", arguments, StringComparison.Ordinal);
        Assert.Contains($"\"{path}\" upgrade --id Mozilla.Firefox --exact --source winget", arguments, StringComparison.Ordinal);
        Assert.Contains("--id 7zip.7zip --exact", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("VeryLong", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--all", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("accept", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Winget_is_found_only_in_the_protected_package_folder()
    {
        const string folder = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe";
        var packages = new FakePackages();
        packages.Packages.Add(new InstalledPackage("Microsoft.DesktopAppInstaller", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "1.24.0.0", "CN=Microsoft", folder));
        var files = new FakeFiles().AddFile(folder + @"\winget.exe", string.Empty);

        Assert.Equal(folder + @"\winget.exe", Winget.Locate(packages, files, @"C:\Program Files"));
        Assert.Null(Winget.Locate(packages, files, @"D:\Autre"));
        Assert.Null(Winget.Locate(new FakePackages(), files, @"C:\Program Files"));
    }
}

public class SoftwareUpdatesModuleTests
{
    private const string Folder = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24_x64__8wekyb3d8bbwe";

    /// <summary>Audit sans droits administrateur, comme dans la fenêtre : winget n'est jamais lancé avec ces droits.</summary>
    private static async Task<Finding> Detect(FakeCommands commands, bool wingetInstalled = true, bool elevated = false)
    {
        var packages = new FakePackages();
        if (wingetInstalled)
        {
            packages.Packages.Add(new InstalledPackage("Microsoft.DesktopAppInstaller", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "1.24.0.0", "CN=Microsoft", Folder));
        }

        var files = new FakeFiles().AddFile(Folder + @"\winget.exe", string.Empty);
        var context = TestContext.Create(commands: commands, packages: packages, files: files, elevated: elevated);
        var findings = await new SoftwareUpdatesModule(@"C:\Program Files").DetectAsync(context, CancellationToken.None);
        return Assert.Single(findings);
    }

    /// <summary>
    /// winget est un outil propre au compte de l'utilisateur : un audit avec les droits administrateur (moteur, fenêtre lancée
    /// en administrateur) ne le lance pas, et dit pourquoi. La liste simulée prouve qu'il n'a pas été lancé : elle donnerait un orange.
    /// </summary>
    [Fact]
    public async Task Winget_is_never_started_with_administrator_rights()
    {
        var finding = await Detect(Answer(WingetParserTests.English), elevated: true);

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("droits administrateur", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("ouvrez MAUS normalement", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Winget_is_never_started_without_network()
    {
        var commands = new FakeCommands();
        var packages = new FakePackages();
        packages.Packages.Add(new InstalledPackage("Microsoft.DesktopAppInstaller", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "1.24.0.0", "CN=Microsoft", @"C:\Program Files\WindowsApps\x"));
        var context = TestContext.Create(commands: commands, packages: packages, files: new FakeFiles().AddFile(Folder + @"\winget.exe", string.Empty)).WithoutNetwork();

        var finding = Assert.Single(await new SoftwareUpdatesModule(@"C:\Program Files").DetectAsync(context, CancellationToken.None));

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("réseau", finding.Explanation, StringComparison.Ordinal);
    }

    private static FakeCommands Answer(string output, int exitCode = 0) =>
        new FakeCommands().Answer($@"{Folder}\winget.exe {string.Join(' ', Winget.ListArguments)}", output, exitCode);

    [Fact]
    public async Task Outdated_browser_or_archiver_is_orange_and_listed_first()
    {
        var finding = await Detect(Answer(WingetParserTests.English));

        Assert.Equal(FindingStatus.Warning, finding.Status);
        Assert.StartsWith("4 logiciel(s) : Mozilla Firefox (x64 fr) (129.0.1 → 130.0), 7-Zip", finding.Current, StringComparison.Ordinal);
        Assert.Contains("Mozilla Firefox (x64 fr), 7-Zip 23.01 (x64)", finding.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_updates_are_an_optimisation()
    {
        var finding = await Detect(Answer(WingetParserTests.French.Replace("Notepad++.Notepad++", "Some.Tool.Example.X", StringComparison.Ordinal)));

        Assert.Equal(FindingStatus.Improvable, finding.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x8A150014))]
    public async Task Nothing_to_update_is_compliant(int exitCode)
    {
        var finding = await Detect(Answer("No installed package found matching input criteria.\n", exitCode));

        Assert.Equal(FindingStatus.Ok, finding.Status);
    }

    [Fact]
    public async Task Winget_failure_is_unknown_never_a_problem()
    {
        var finding = await Detect(Answer("Failed in attempting to update the source: winget\n", unchecked((int)0x8A15000F)));

        Assert.Equal(FindingStatus.Unknown, finding.Status);
    }

    [Fact]
    public async Task Missing_winget_is_unknown()
    {
        var finding = await Detect(new FakeCommands(), wingetInstalled: false);

        Assert.Equal(FindingStatus.Unknown, finding.Status);
        Assert.Contains("Microsoft Store", finding.Explanation, StringComparison.Ordinal);
    }
}
