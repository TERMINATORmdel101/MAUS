using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>
/// Même lecture que <see cref="DismImageHealthChecker"/>, par l'outil <c>dism.exe /English /Online /Cleanup-Image
/// /CheckHealth</c> (lecture seule : il ne fait que lire l'indicateur d'altération). Sert dans le paquet du Microsoft Store :
/// essayé le 22/10/2026 sur le PC du porteur, l'API DISM appelée dans le processus d'une application empaquetée attend 132 s
/// puis échoue (DismOpenSession = 0x80040154, « classe non enregistrée »), alors que dism.exe répond en une seconde.
/// Messages reconnus (sortie anglaise forcée par <c>/English</c>, jamais traduite) : « No component store corruption
/// detected », « The component store is repairable », « The component store cannot be repaired » (relevés par woshub.com,
/// winhelponline.com et winaero.com ; le premier vu sur le PC du porteur). Toute autre sortie rend l'état inconnu.
/// </summary>
public sealed class DismCommandImageHealthChecker(ICommandRunner commands) : IImageHealthChecker
{
    /// <summary>Seule ligne de commande admise (voir <see cref="ReadOnlyCommandRunner"/>).</summary>
    public static readonly IReadOnlyList<string> Arguments = ["/English", "/Online", "/Cleanup-Image", "/CheckHealth"];

    /// <summary>Code de sortie de dism.exe sans droits administrateur (ERROR_ELEVATION_REQUIRED).</summary>
    private const int ElevationRequired = 740;

    public ImageHealth Check(TimeSpan timeout)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        var result = commands.RunAsync(dism, Arguments, timeout, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        if (result.TimedOut)
        {
            throw new DataSourceUnavailableException(T("DISM n'a pas répondu à temps."));
        }

        if (result.ExitCode == ElevationRequired)
        {
            throw new MausAccessDeniedException(T("DISM demande les droits administrateur."));
        }

        return Parse(result.StandardOutput)
            ?? throw new DataSourceUnavailableException(T("réponse de DISM non reconnue (code {0})", result.ExitCode));
    }

    /// <summary>État lu dans la sortie anglaise de DISM ; <c>null</c> si elle n'est pas reconnue.</summary>
    public static ImageHealth? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Contains("The component store cannot be repaired", StringComparison.OrdinalIgnoreCase))
        {
            return ImageHealth.NotRepairable;
        }

        if (output.Contains("The component store is repairable", StringComparison.OrdinalIgnoreCase))
        {
            return ImageHealth.Repairable;
        }

        return output.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase) ? ImageHealth.Healthy : null;
    }
}
