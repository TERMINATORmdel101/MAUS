using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Protections de Windows que MAUS active sur ses propres processus (demande du porteur du 29/10/2026 : « une sécurité de
/// plus contre les pirates et les virus »), par <c>SetProcessMitigationPolicy</c> (Microsoft Learn, processthreadsapi.h,
/// Windows 8 et plus). Elles ne touchent que le processus de MAUS, jamais le reste du PC, et ne peuvent qu'interdire :
/// <list type="bullet">
/// <item>bibliothèques (DLL) : jamais chargées depuis un partage réseau, ni depuis un fichier marqué « intégrité basse »
/// (écrit par un programme bridé, un navigateur par exemple) ; celles de Windows sont cherchées d'abord dans System32
/// (<c>PROCESS_MITIGATION_IMAGE_LOAD_POLICY</c> : NoRemoteImages, NoLowMandatoryLabelImages, PreferSystem32Images) ;</item>
/// <item>moteur administrateur seulement : anciens points d'injection de DLL coupés (<c>PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY</c>,
/// « legacy extension point DLLs ») et RedirectionGuard : le moteur ne suit pas une jonction de dossier créée par un compte
/// non administrateur (<c>PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY</c>, EnforceRedirectionTrust), le piège classique
/// pour faire écrire un programme administrateur ailleurs que prévu.</item>
/// </list>
/// Appliquées au démarrage, avant toute demande ; elles ne protègent pas de ce qui a été chargé avant (Microsoft Learn :
/// « mitigation policies should be applied before or during process initialization »). Une protection que Windows refuse
/// est simplement absente de la liste renvoyée : MAUS continue.
/// </summary>
public static partial class ProcessHardening
{
    // Rangs dans l'énumération PROCESS_MITIGATION_POLICY (winnt.h, Microsoft Learn).
    private const int ExtensionPointDisablePolicy = 6;
    private const int ImageLoadPolicy = 10;
    private const int RedirectionTrustPolicy = 16;

    // Bits des structures (winnt.h, Microsoft Learn).
    private const uint NoRemoteImages = 0x1;
    private const uint NoLowMandatoryLabelImages = 0x2;
    private const uint PreferSystem32Images = 0x4;
    private const uint DisableExtensionPoints = 0x1;
    private const uint EnforceRedirectionTrust = 0x1;

    /// <summary>Fenêtre de MAUS (sans droits) : bibliothèques venues du réseau ou d'intégrité basse refusées.</summary>
    /// <returns>Les protections appliquées (noms courts, pour le journal de diagnostic).</returns>
    public static IReadOnlyList<string> HardenInterface() =>
        Apply([(ImageLoadPolicy, NoRemoteImages | NoLowMandatoryLabelImages, "images")]);

    /// <summary>Moteur administrateur (et audit planifié) : toutes les protections ci-dessus.</summary>
    /// <returns>Les protections appliquées (noms courts, pour le journal de diagnostic).</returns>
    public static IReadOnlyList<string> HardenEngine() =>
        Apply(
        [
            (ImageLoadPolicy, NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images, "images"),
            (ExtensionPointDisablePolicy, DisableExtensionPoints, "extension-points"),
            (RedirectionTrustPolicy, EnforceRedirectionTrust, "redirection-guard"),
        ]);

    private static List<string> Apply(IEnumerable<(int Policy, uint Flags, string Name)> policies)
    {
        var applied = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return applied;
        }

        foreach (var (policy, flags, name) in policies)
        {
            var value = flags;
            try
            {
                if (SetProcessMitigationPolicy(policy, ref value, sizeof(uint)))
                {
                    applied.Add(name);
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows trop ancien pour cette fonction : rien à faire.
            }
        }

        return applied;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);
}
