using System.Globalization;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

public class SystemToolTests
{
    [Fact]
    public async Task A_rewritten_percentage_gives_one_line_each_time()
    {
        var lines = new List<string>();

        await SystemTool.PumpAsync(new StringReader("Début\r\n[= 10.0% =]\r[= 20.0% =]\r\n\r\n  fin"), lines.Add, CancellationToken.None);

        Assert.Equal(["Début", "[= 10.0% =]", "[= 20.0% =]", "fin"], lines);
    }

    [Fact]
    public async Task Pnputil_accents_read_correctly_on_a_french_windows()
    {
        var lines = new List<string>();

        await SystemTool.RunAsync(SystemTool.PathOf("pnputil.exe"), ["/enum-devices", "/class", "Display"], OemEncoding.Ansi, lines.Add, CancellationToken.None);

        // Lue dans la page OEM, sa sortie donne « DÚbut » et « instanceá » (constaté le 1/11/2026) : aucun des deux n'existe en français.
        if (CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "fr")
        {
            Assert.DoesNotContain(lines, line => line.Contains('Ú', StringComparison.Ordinal) || line.Contains('á', StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_real_tool_runs_directly_and_gives_its_exit_code()
    {
        var lines = new List<string>();

        var code = await SystemTool.RunAsync(SystemTool.PathOf("whoami.exe"), [], System.Text.Encoding.UTF8, lines.Add, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.NotEmpty(lines);
    }
}
