using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Liste blanche de ce que MAUS accepte d'écrire (0.8.6). Jusque-là, le plan calculé par le moteur était la seule barrière :
/// <see cref="SettingsAccessor"/> acceptait toute clé de registre et toute tâche. Elle borne l'emplacement (clé et nom de valeur),
/// ni l'action ni la valeur : celles-ci viennent du plan et du journal. Cette liste reprend, par préfixe de clé, le
/// §3.1 du document de sécurité du Store, avec les noms de valeur quand la clé en porte d'autres que MAUS ne doit jamais
/// toucher (services, Winlogon, IFEO…). Une clé fabriquée depuis une donnée lue sur le PC (M06 « valeurs Windows », M12)
/// ne peut donc pas en sortir, à l'application comme à l'annulation. Une correction qui écrit ailleurs doit s'ajouter ici
/// et dans le document du Store ; <c>SettingAllowlistTests</c> vérifie les catalogues, et les tests aller-retour le reste.
/// </summary>
public static class SettingAllowlist
{
    /// <param name="Names">Noms de valeur permis, sur cette clé exactement ; <c>null</c> = tous les noms de la clé et de ses sous-clés.</param>
    /// <param name="DirectChildrenOnly">Seulement les sous-clés immédiates (une par service, par profil de pare-feu, par programme, par entrée de démarrage), jamais la clé elle-même.</param>
    /// <param name="Children">Avec <paramref name="DirectChildrenOnly"/> : les seules sous-clés permises ; <c>null</c> = toutes.</param>
    private sealed record Entry(string Hive, string Path, string[]? Names = null, bool DirectChildrenOnly = false, string[]? Children = null);

    private const string Hklm = "HKLM";
    private const string Hkcu = "HKCU";

    /// <summary>Les seuls services dont M01 remet le type de démarrage (§3.1 du document de sécurité).</summary>
    private static readonly string[] FixedServices = ["wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "CryptSvc", "TrustedInstaller", "Audiosrv", "WSearch", "WinDefend", "mpssvc"];

    /// <summary>Les sous-clés de StartupApproved que le Gestionnaire des tâches et M12 utilisent.</summary>
    private static readonly string[] StartupApprovedKeys = ["Run", "Run32", "StartupFolder"];

    private static readonly Entry[] Registry =
    [
        // M01 : défenses de Windows remises en place
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows Defender"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\WindowsFirewall", ["EnableFirewall"], DirectChildrenOnly: true),
        new(Hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", ["EnableLUA", "ConsentPromptBehaviorAdmin", "PromptOnSecureDesktop"]),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\System"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"),
        new(Hklm, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"),
        new(Hklm, @"SYSTEM\CurrentControlSet\Services", ["Start"], DirectChildrenOnly: true, Children: FixedServices),
        new(Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", ["Shell", "Userinit"]),
        new(Hkcu, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", ["Shell"]),
        new(Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ["AppInit_DLLs", "LoadAppInit_DLLs"]),
        new(Hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows", ["AppInit_DLLs", "LoadAppInit_DLLs"]),
        new(Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", ["Debugger"], DirectChildrenOnly: true),
        new(Hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", ["Debugger"], DirectChildrenOnly: true),

        // M13 : compromis de sécurité
        new(Hklm, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ["FeatureSettingsOverride", "FeatureSettingsOverrideMask"]),
        new(Hklm, @"SYSTEM\CurrentControlSet\Control\CI\Config", ["VulnerableDriverBlocklistEnable"]),

        // M04 : vie privée
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI"),
        new(Hkcu, @"Software\Microsoft\Siuf\Rules"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Privacy"),
        new(Hkcu, @"Software\Policies\Microsoft\Windows\CloudContent"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"),
        new(Hkcu, @"Software\Policies\Microsoft\Windows\Explorer"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"),
        new(Hkcu, @"Control Panel\International\User Profile"),
        new(Hkcu, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy"),
        new(Hkcu, @"Software\Microsoft\InputPersonalization"),

        // M05 : alimentation
        new(Hklm, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", ["HiberbootEnabled"]),

        // M06 : effets visuels (les « valeurs Windows » ont en plus leur propre liste : VisualEffectDefaults.DeclaredKeys)
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects"),
        new(Hkcu, @"Software\Microsoft\Windows\DWM"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Dsh", ["AllowNewsAndInterests"]),

        // M07 : Game Bar
        new(Hkcu, @"Software\Microsoft\GameBar"),
        new(Hkcu, @"System\GameConfigStore", ["GameDVR_Enabled"]),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR"),
        new(Hklm, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", ["AllowGameDVR"]),

        // M09 : carte graphique
        new(Hklm, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ["HwSchMode"]),
        new(Hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", ["SearchOrderConfig"]),

        // M12 : programmes au démarrage (Run, Run32, StartupFolder, dans l'une ou l'autre ruche)
        new(Hklm, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", DirectChildrenOnly: true, Children: StartupApprovedKeys),
        new(Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved", DirectChildrenOnly: true, Children: StartupApprovedKeys),

        // Point de restauration : fréquence minimale levée le temps de l'appel, puis remise
        new(Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ["SystemRestorePointCreationFrequency"]),
    ];

    /// <summary>Vrai si MAUS accepte d'écrire (ou de supprimer) ce réglage.</summary>
    public static bool Allows(SettingKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Kind switch
        {
            SettingKind.Registry => key.Path is not null && Registry.Any(entry => Matches(entry, key)),
            SettingKind.ScheduledTask => AllowsTask(key),
            _ => true,
        };
    }

    /// <summary>Refuse, avant toute écriture, un réglage hors de la liste.</summary>
    /// <exception cref="InvalidOperationException">Réglage hors de la liste blanche.</exception>
    public static void Require(SettingKey key)
    {
        if (!Allows(key))
        {
            throw new InvalidOperationException(T("Réglage hors de la liste blanche de MAUS, écriture refusée : {0}", key));
        }
    }

    private static bool Matches(Entry entry, SettingKey key)
    {
        if (!string.Equals(entry.Hive, key.Hive, StringComparison.OrdinalIgnoreCase)
            || !key.Path!.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Sans liste de noms : la clé et toutes ses sous-clés. Avec : la clé exactement (ou ses sous-clés immédiates).
        var rest = key.Path.AsSpan(entry.Path.Length);
        var placed = rest.IsEmpty
            ? !entry.DirectChildrenOnly
            : rest[0] == '\\' && rest.Length > 1 && (entry.DirectChildrenOnly ? IsPermittedChild(entry, rest[1..].ToString()) : entry.Names is null);
        return placed && (entry.Names is null || entry.Names.Contains(key.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsPermittedChild(Entry entry, string child) =>
        !child.Contains('\\') && (entry.Children is null || entry.Children.Contains(child, StringComparer.OrdinalIgnoreCase));

    /// <summary>Une tâche de Windows lui-même (dossier <c>\Microsoft\</c>) n'est jamais touchée ; M12 ne liste que les autres.</summary>
    private static bool AllowsTask(SettingKey key) =>
        key.Path is not null && key.Name is { Length: > 0 }
        && !WindowsScheduledTaskAccessor.FullName(key.Path, key.Name).TrimStart('\\').StartsWith(@"Microsoft\", StringComparison.OrdinalIgnoreCase);
}
