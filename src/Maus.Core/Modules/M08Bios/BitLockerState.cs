using System.Text.RegularExpressions;
using Maus.Core.Modules.M09Gpu;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M08Bios;

/// <summary>
/// État de BitLocker sur le disque système, lu pendant l'audit. Trois lectures documentées seulement : l'état du volume, l'avancement
/// du chiffrement et les identifiants des protecteurs. MAUS ne lit jamais la clé, ne suspend et ne désactive rien : il informe.
/// </summary>
/// <remarks>
/// Avancement : https://learn.microsoft.com/windows/win32/secprov/getconversionstatus-win32-encryptablevolume
/// Protecteurs : https://learn.microsoft.com/windows/win32/secprov/getkeyprotectors-win32-encryptablevolume
/// </remarks>
internal sealed partial record BitLockerState(
    bool Denied,
    bool Unavailable,
    bool VolumeFound,
    long? Protection,
    long? Conversion,
    long? Percent,
    IReadOnlyList<string>? RecoveryIds,
    bool HasPin,
    string Drive,
    bool NoAnswer = false)
{
    /// <summary>Clés de récupération : où les retrouver, et comment reconnaître la bonne par son identifiant (Microsoft Support).</summary>
    internal const string RecoveryKeyHelpUrl = "https://support.microsoft.com/windows/finding-your-bitlocker-recovery-key-in-windows-6b71ad27-0b89-ea08-f143-056f5ab347d6";

    /// <summary>Procédure officielle de désactivation, à faire par l'utilisateur (BitLocker operations guide, « Disable BitLocker »).</summary>
    internal const string DisableProcedureUrl = "https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/operations-guide#disable-bitlocker";

    internal const string ConversionMethod = "GetConversionStatus";
    internal const string ProtectorsMethod = "GetKeyProtectors";

    // Types de protecteurs de GetKeyProtectors : 3 = mot de passe numérique, 4 = TPM + PIN, 6 = TPM + PIN + clé de démarrage.
    private const uint NumericPassword = 3;
    private const uint TpmAndPin = 4;
    private const uint TpmAndPinAndStartupKey = 6;

    internal static string VolumeQuery(string deviceId) =>
        $"SELECT * FROM Win32_EncryptableVolume WHERE DeviceID = '{GpuParsers.EscapeWql(deviceId)}'";

    public bool IsActive => Protection == 1;

    /// <summary>L'état n'a pas pu être lu : droits manquants, Windows qui ne répond pas, ou qui ne le dit pas.</summary>
    public bool NotRead => Denied || NoAnswer || (VolumeFound && Protection is not (0 or 1));

    /// <summary>Lettre du lecteur système (celle de <see cref="Environment.SystemDirectory"/>), « C: » si elle n'a pas la forme attendue.</summary>
    internal static string SystemDrive => ValidDrive(Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/'));

    internal static string ValidDrive(string? letter) => letter is not null && DriveLetter().IsMatch(letter) ? letter.ToUpperInvariant() : "C:";

    /// <summary>Le disque est chiffré, en tout ou en partie, d'après ce que Windows a dit.</summary>
    private bool HoldsEncryptedData => Conversion is 1 or 2 or 3 or 4 or 5 || (Conversion is null && IsActive);

    public static BitLockerState Read(ICimReader cim)
    {
        ArgumentNullException.ThrowIfNull(cim);

        IReadOnlyList<CimRow> rows;
        try
        {
            rows = cim.Query(BiosModule.BitLockerQuery, CimScopes.BitLocker);
        }
        catch (MausAccessDeniedException)
        {
            return new BitLockerState(true, false, false, null, null, null, null, false, SystemDrive);
        }
        catch (DataSourceUnavailableException ex) when (ex.IsAbsent)
        {
            return new BitLockerState(false, true, false, null, null, null, null, false, SystemDrive);
        }
        catch (DataSourceUnavailableException)
        {
            // Délai, service arrêté, fournisseur en panne : rien ne dit que BitLocker est absent.
            return new BitLockerState(false, false, false, null, null, null, null, false, SystemDrive, NoAnswer: true);
        }

        var systemDrive = SystemDrive;
        var system = rows.FirstOrDefault(r => r.GetInt64("VolumeType") == 0)
            ?? rows.FirstOrDefault(r => string.Equals(r.GetString("DriveLetter"), systemDrive, StringComparison.OrdinalIgnoreCase));
        if (system is null)
        {
            return new BitLockerState(false, false, false, null, null, null, null, false, systemDrive);
        }

        var protection = system.GetInt64("ProtectionStatus");
        var letter = system.GetString("DriveLetter");
        var drive = letter is not null && DriveLetter().IsMatch(letter) ? letter.ToUpperInvariant() : systemDrive;
        if (system.GetString("DeviceID") is not { Length: > 0 } deviceId)
        {
            return new BitLockerState(false, false, true, protection, null, null, null, false, drive);
        }

        var query = VolumeQuery(deviceId);
        var conversion = Call(cim, query, ConversionMethod, null);
        var percent = conversion?.GetInt64("EncryptionPercentage");
        var status = percent is >= 0 and <= 100 ? conversion?.GetInt64("ConversionStatus") : null;
        if (status == 0)
        {
            return new BitLockerState(false, false, true, protection, status, percent, null, false, drive);
        }

        var recoveryIds = Protectors(cim, query, NumericPassword)?
            .Select(id => Guid.TryParse(id, out var guid) ? guid.ToString("D").ToUpperInvariant() : null)
            .OfType<string>()
            .ToList();
        var hasPin = Protectors(cim, query, TpmAndPin) is { Count: > 0 } || Protectors(cim, query, TpmAndPinAndStartupKey) is { Count: > 0 };
        return new BitLockerState(false, false, true, protection, status, percent, recoveryIds, hasPin, drive);
    }

    private static IReadOnlyList<string>? Protectors(ICimReader cim, string query, uint type) =>
        Call(cim, query, ProtectorsMethod, new Dictionary<string, object?> { ["KeyProtectorType"] = type })?.GetStringArray("VolumeKeyProtectorID");

    /// <summary>Sortie de la méthode, ou <c>null</c> si Windows ne répond pas ou signale une erreur.</summary>
    private static CimRow? Call(ICimReader cim, string query, string method, IReadOnlyDictionary<string, object?>? parameters)
    {
        try
        {
            var output = cim.InvokeMethod(query, method, parameters, CimScopes.BitLocker);
            return output?.GetInt64("ReturnValue") == 0 ? output : null;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return null;
        }
    }

    public string Current => Conversion switch
    {
        0 => T("disque non chiffré"),
        1 when IsActive => T("disque chiffré, protection active"),
        1 when Protection == 0 => T("disque chiffré, protection non active"),
        1 => T("disque chiffré, état de la protection non lu"),
        2 => T("partiellement chiffré ({0} %), chiffrement en cours", Percent),
        3 => T("partiellement chiffré ({0} %), déchiffrement en cours", Percent),
        4 => T("partiellement chiffré ({0} %), chiffrement en pause", Percent),
        5 => T("partiellement chiffré ({0} %), déchiffrement en pause", Percent),
        _ => Protection switch
        {
            1 => T("protection active"),

            // Selon Microsoft, « protection désactivée » couvre aussi un disque partiellement chiffré :
            // https://learn.microsoft.com/windows/win32/secprov/getprotectionstatus-win32-encryptablevolume
            0 => T("protection non active : disque non chiffré ou partiellement chiffré, détail non lu"),
            _ => T("état inconnu"),
        },
    };

    /// <summary>Jamais « clé sauvegardée » : MAUS ne peut pas le savoir, et il ne lit jamais la clé.</summary>
    public string? KeyAdvice
    {
        get
        {
            if (!HoldsEncryptedData)
            {
                return null;
            }

            return RecoveryIds switch
            {
                null => T("Identifiant de la clé non lu : vérifiez que vous avez votre clé de récupération. Pour la retrouver, page officielle de Microsoft : {0}", RecoveryKeyHelpUrl),
                { Count: 0 } => T("Windows n'indique aucune clé de récupération à 48 chiffres pour ce disque. Page officielle de Microsoft sur cette clé : {0}", RecoveryKeyHelpUrl),
                _ => T("Une clé de récupération existe pour ce disque : vérifiez que vous l'avez. MAUS ne lit jamais cette clé et ne peut pas savoir si elle est gardée ailleurs. Pour la retrouver, page officielle de Microsoft : {0}", RecoveryKeyHelpUrl),
            };
        }
    }

    /// <summary>Identifiant de la clé (non secret) : à l'écran seulement, jamais dans un export ni le presse-papiers.</summary>
    public string? KeyIdentifierNote => RecoveryIds is { Count: > 0 } ids
        ? T("Identifiant de la clé, non secret, à comparer avec celle que vous retrouvez : {0}", string.Join(", ", ids))
        : null;

    /// <summary>
    /// Encadré à lire avant une mise à jour du BIOS : BitLocker actif, ou état non lu. MAUS ne suspend rien lui-même.
    /// </summary>
    /// <remarks>
    /// Mise à jour du BIOS et clé demandée, suspension temporaire : https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
    /// Panneau de configuration et commande : https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/operations-guide#suspend-and-resume
    /// Nombre de redémarrages (-rc) : https://learn.microsoft.com/windows-server/administration/windows-commands/manage-bde-protectors
    /// </remarks>
    public string? BiosUpdateCallout
    {
        get
        {
            string intro;
            if (NotRead)
            {
                intro = T("À faire avant de mettre à jour le BIOS. MAUS n'a pas pu lire l'état de BitLocker. S'il est actif, Windows peut refuser votre mot de passe ou votre PIN au redémarrage et demander la clé de récupération.");
            }
            else if (IsActive && HasPin)
            {
                intro = T("À faire absolument avant de mettre à jour le BIOS. Ce disque demande un PIN au démarrage (TPM + PIN) : si ce PIN est refusé après la mise à jour, il vous faudra la clé de récupération pour rouvrir Windows.");
            }
            else if (IsActive)
            {
                intro = T("À faire avant de mettre à jour le BIOS. BitLocker est actif sur ce disque : au redémarrage, Windows peut refuser votre mot de passe ou votre PIN et demander la clé de récupération.");
            }
            else
            {
                return null;
            }

            return string.Join(
                '\n',
                intro,
                T("1. Retrouvez et notez votre clé de récupération : {0}", RecoveryKeyHelpUrl),
                T("2. Suspendez BitLocker comme l'indique Microsoft : Panneau de configuration, « Chiffrement de lecteur BitLocker », « Suspendre la protection » (elle reprend au redémarrage suivant) ; ou, dans une invite de commandes en administrateur, manage-bde -protectors -disable {0} -rc 2 (elle reprend après deux redémarrages).", Drive),
                T("MAUS ne suspend rien lui-même et ne touche pas au BIOS."),
                T("Sur un Windows modifié ou allégé, ce réglage peut être absent ou se comporter autrement : gardez d'abord votre clé."));
        }
    }

    /// <summary>
    /// Note factuelle sur un choix laissé à l'utilisateur (décision du porteur, 2/11/2026) : ni correction, ni profil, ni bouton qui agit.
    /// </summary>
    /// <remarks>
    /// Recommandation de garder le chiffrement de l'appareil : https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/
    /// Performance, sans aucun chiffre : https://learn.microsoft.com/windows/security/book/operating-system-security-encryption-and-data-protection
    /// </remarks>
    public string? ChoiceNote => Conversion is 1 or 2 or 4 || (Conversion is null && IsActive)
        ? T("À titre d'information, c'est votre choix et MAUS ne désactive rien lui-même. Sur un PC fixe qui sert seulement à jouer, sans données sensibles et qui ne quitte pas la maison, " +
            "certains préfèrent désactiver BitLocker : un changement de BIOS ou de matériel peut demander la clé de récupération, et sans cette clé les données sont perdues. " +
            "Ce que ça retire : sans chiffrement, quelqu'un qui emporte le PC ou le disque lit les fichiers. " +
            "C'est déconseillé sur un portable ou avec des données personnelles ou professionnelles, et Microsoft recommande de garder le chiffrement de l'appareil activé. " +
            "Côté performance, Microsoft indique seulement que confier le chiffrement à du matériel dédié améliore celles de BitLocker : MAUS n'a rien mesuré sur ce PC et ne promet aucun gain. " +
            "Si vous décidez de le désactiver, suivez la procédure officielle de Microsoft : c'est long et le PC doit rester allumé. {0}", DisableProcedureUrl)
        : null;

    [GeneratedRegex("^[A-Za-z]:$", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();
}
