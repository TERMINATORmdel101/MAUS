using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Le rapport fait, ou ce qui l'en a empêché.</summary>
/// <param name="File">Chemin du fichier ; <c>null</c> si le rapport n'existe pas.</param>
/// <param name="Refusal">Raison du refus de MAUS ; <c>null</c> si Windows a fait le rapport, ou n'a pas pu le faire (pas de batterie).</param>
public sealed record BatteryReportResult(string? File, string? Refusal = null);

/// <summary>La capacité passée lue dans le rapport XML de Windows, ou ce qui l'en a empêché.</summary>
/// <param name="History">Ce qui a été lu ; <c>null</c> si rien n'a été lu.</param>
/// <param name="Refusal">Raison du refus de MAUS ; <c>null</c> si MAUS a essayé (sans <paramref name="History"/>, c'est « non lu »).</param>
public sealed record BatteryHistoryResult(BatteryHistory? History, string? Refusal = null);

/// <summary>
/// Rapport de batterie de Windows (<c>powercfg /batteryreport</c>). Ce n'est pas une opération du moteur : il se fait dans le
/// processus de l'interface, avec ses droits. powercfg écrit dans le profil de l'utilisateur, un dossier que tout programme de
/// l'utilisateur peut modifier, et les protections de Windows du moteur ne valent pas pour powercfg.exe. Une fenêtre lancée en
/// administrateur le refuse donc : elle ne fait pas cette écriture avec ses droits.
/// </summary>
public static class BatteryReport
{
    public static Task<BatteryReportResult> CreateAsync(ICommandRunner commands, CancellationToken cancellationToken) =>
        CreateAsync(Environment.IsPrivilegedProcess, commands, cancellationToken);

    internal static async Task<BatteryReportResult> CreateAsync(bool elevated, ICommandRunner commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (elevated)
        {
            return new BatteryReportResult(null, ElevatedRefusal());
        }

        var file = ReadOnlyCommandRunner.BatteryReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        var powercfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
        var result = await commands.RunAsync(powercfg, ["/batteryreport", "/output", file], TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return new BatteryReportResult(result.ExitCode == 0 && File.Exists(file) ? file : null);
    }

    /// <summary>Fait le rapport XML de Windows, en lit la capacité passée, puis le supprime (il contient le nom du PC).</summary>
    public static Task<BatteryHistoryResult> ReadHistoryAsync(ICommandRunner commands, CancellationToken cancellationToken) =>
        ReadHistoryAsync(Environment.IsPrivilegedProcess, ReadOnlyCommandRunner.BatteryHistoryPath, commands, cancellationToken);

    internal static async Task<BatteryHistoryResult> ReadHistoryAsync(bool elevated, string file, ICommandRunner commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (elevated)
        {
            return new BatteryHistoryResult(null, ElevatedRefusal());
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Delete(file);
            var powercfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
            var result = await commands.RunAsync(powercfg, ["/batteryreport", "/output", file, "/xml"], TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return new BatteryHistoryResult(result.ExitCode == 0 ? ReadFile(file) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DataSourceUnavailableException)
        {
            return new BatteryHistoryResult(null);
        }
        finally
        {
            DeleteQuietly(file);
        }
    }

    private static BatteryHistory? ReadFile(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > BatteryHistoryReader.MaxBytes)
        {
            return null;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return BatteryHistoryReader.Parse(stream);
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tenu par un antivirus, par exemple : il reste dans le profil de l'utilisateur, le prochain clic le remplacera.
        }
    }

    private static string ElevatedRefusal() =>
        T("MAUS a été lancé en administrateur : fermez-le et rouvrez-le normalement pour cette action, afin que le rapport de batterie ne s'écrive pas dans votre profil avec ces droits.");
}
