using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>
/// Portes laissées ouvertes (souvent par un ancien dépannage) : Bureau à distance, ancien protocole de partage SMB 1, session
/// ouverte automatiquement. Lecture seule. Sources : Microsoft Learn, « fDenyTSConnections » (Terminal Services), « Detect,
/// enable, and disable SMBv1 » (fonctionnalité SMB1Protocol), « Turn on automatic logon in Windows » (mot de passe en clair).
/// </summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>Page « Bureau à distance » des Paramètres (Microsoft Learn, « Launch Windows Settings »).</summary>
    internal const string RemoteDesktopPage = "ms-settings:remotedesktop";

    /// <summary>État 1 de <c>Win32_OptionalFeature.InstallState</c> : fonctionnalité activée.</summary>
    private const int FeatureEnabled = 1;

    internal const string Smb1Query = "SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'SMB1Protocol'";

    private static Check RemoteDesktopCheck => new("M01.remote-desktop", T("Bureau à distance ouvert"), NetworkCategory, Severity.Medium, SettingsPage: RemoteDesktopPage);

    private static Check Smb1Check => new("M01.smb1", T("Ancien protocole de partage SMB 1"), NetworkCategory, Severity.Medium);

    private static Check AutoLogonCheck => new("M01.auto-logon", T("Session ouverte automatiquement"), NetworkCategory, Severity.Medium);

    private static Finding DetectRemoteDesktop(IRegistryReader registry, bool managed)
    {
        var explanation = T("Le Bureau à distance permet de prendre la main sur ce PC depuis un autre ordinateur. C'est utile si vous vous en servez ; sinon, c'est une porte d'entrée de plus.");
        var expected = T("fermé, sauf si vous vous en servez");
        if (registry.GetDword(Hklm, TerminalServerKey, "fDenyTSConnections") != 0)
        {
            return RemoteDesktopCheck.Compliant(T("fermé"), expected, explanation);
        }

        return managed
            ? RemoteDesktopCheck.Neutral(T("ouvert"), expected, T("Sur un PC géré, il est souvent ouvert par l'entreprise."))
            : RemoteDesktopCheck.Deviation(T("ouvert"), expected, explanation, T("Si vous ne vous en servez pas, coupez-le dans Paramètres > Système > Bureau à distance."));
    }

    private static Finding DetectSmb1(ICimReader cim)
    {
        var explanation = T("SMB 1 est un ancien protocole de partage de fichiers, que Microsoft déconseille et n'active plus par défaut : il n'a pas les protections des versions récentes. Seuls de très vieux appareils (NAS, imprimantes) en ont encore besoin.");
        var expected = T("désactivé");
        var state = FirstRow(cim.Query(Smb1Query))?.GetInt64("InstallState");
        return state == FeatureEnabled
            ? Smb1Check.Deviation(T("activé"), expected, explanation, T("Sauf si un vieil appareil en a besoin, décochez « Support de partage de fichiers SMB 1.0/CIFS » dans « Activer ou désactiver des fonctionnalités Windows »."))
            : Smb1Check.Compliant(expected, expected, explanation);
    }

    private static Finding DetectAutoLogon(IRegistryReader registry)
    {
        var explanation = T("Windows peut ouvrir une session tout seul au démarrage, sans demander de mot de passe : quiconque allume le PC arrive sur le Bureau. Quand le mot de passe est gardé pour cela dans le registre, il y est écrit en clair.");
        var expected = T("mot de passe demandé");
        if (registry.GetString(Hklm, WinlogonKey, "AutoAdminLogon") != "1")
        {
            return AutoLogonCheck.Compliant(expected, expected, explanation);
        }

        // Le mot de passe n'est jamais lu pour être affiché : seule sa présence compte.
        var advice = T("Si ce n'est pas vous qui l'avez voulu, coupez-le : tapez « netplwiz » dans le menu Démarrer, puis cochez « Les utilisateurs doivent entrer un nom d'utilisateur et un mot de passe ».");
        return IsSet(registry.GetString(Hklm, WinlogonKey, "DefaultPassword"))
            ? AutoLogonCheck.Deviation(T("session automatique, mot de passe en clair dans le registre"), expected, explanation, advice, Severity.High)
            : AutoLogonCheck.Deviation(T("session automatique"), expected, explanation, advice);
    }
}
