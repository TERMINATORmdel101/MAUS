using Maus.Core.Fixes;
using Maus.Core.Platform;
using static Maus.Core.Modules.M07GameBar.GameBarKeys;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M07GameBar;

/// <summary>
/// Étape Plan du Module 7. Les valeurs sont écrites pour l'utilisateur courant. La Game Bar n'est jamais désinstallée ;
/// sa réinstallation par winget (si elle manque) arrive dans une étape suivante.
/// </summary>
internal static class GameBarPlanner
{
    public static IReadOnlyList<PlannedChange> Plan(string moduleId, AuditContext context, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        bool Deviates(string id) => byId.TryGetValue(id, out var f) && f.Fixable && f.Status is FindingStatus.Improvable or FindingStatus.Warning;

        var (profile, _) = GamingPackages.Choose(context.Hardware.Cpu.IsAsymmetricDualCcdX3D, GamingPackages.TryRead(context.Packages), context.Preferences.GameBarProfile);
        var changes = new List<PlannedChange>();
        void Add(string id, string title, string description, string category, params SettingWrite[] writes) => changes.Add(new PlannedChange
        {
            Id = id,
            ModuleId = moduleId,
            Title = title,
            Description = description,
            Category = category,
            Writes = writes,
        });

        if (Deviates("M07.background-recording"))
        {
            Add("M07.background-recording", T("Couper l'enregistrement en arrière-plan"),
                T("La partie n'est plus filmée en continu : moins de charge pour la carte graphique et le disque. Les captures manuelles restent possibles."),
                Recording, Hkcu(GameDvrUser, "HistoricalCaptureEnabled", 0));
            changes[^1] = changes[^1] with { Gain = T("Moins de charge pendant les jeux (ampleur à mesurer, voir Module 11).") };
        }

        if (Deviates("M07.game-mode"))
        {
            Add("M07.game-mode", T("Réactiver le Mode Jeu"),
                T("Le jeu en cours reçoit la priorité et Windows Update n'installe rien pendant la partie."),
                GameMode, Hkcu(GameBarUser, "AutoGameModeEnabled", 1));
        }

        if (Deviates("M07.captures"))
        {
            Add("M07.captures", T("Couper les captures de jeu (profil 1)"),
                T("Clips et captures d'écran de la Game Bar désactivés. Win+G ouvre encore la Game Bar, qui reste installée."),
                Recording, Hkcu(GameConfigStore, "GameDVR_Enabled", 0), Hkcu(GameDvrUser, "AppCaptureEnabled", 0));
        }

        if (Deviates("M07.controller-button"))
        {
            Add("M07.controller-button", T("Ne plus ouvrir la Game Bar avec le bouton Xbox de la manette (profil 1)"),
                T("Évite les ouvertures par erreur en pleine partie."),
                Overlay, Hkcu(GameBarUser, "UseNexusForGameBarEnabled", 0));
        }

        if (Deviates("M07.gamedvr-policy") && profile == GamingProfile.X3D)
        {
            Add("M07.gamedvr-policy", T("Retirer la stratégie qui bloque la Game Bar (Ryzen X3D)"),
                T("La Game Bar redevient pleinement active pour placer vos jeux sur les cœurs dotés du V-Cache."),
                Recording, new SettingWrite(SettingKey.Registry("HKLM", GameDvrPolicy, "AllowGameDVR"), null));
        }

        // Verrou optionnel du profil 1 : proposé non coché, jamais en profil 3.
        if (profile == GamingProfile.NoXbox && !context.Windows.IsHomeEdition && ReadPolicy(context.Registry) != 0)
        {
            changes.Add(new PlannedChange
            {
                Id = "M07.gamedvr-policy",
                ModuleId = moduleId,
                Title = T("Verrouiller l'enregistrement des jeux pour tous les comptes (AllowGameDVR = 0)"),
                Description = T("Verrou optionnel du profil 1 : la stratégie interdit l'enregistrement des jeux pour tous les utilisateurs de ce PC."),
                Category = Recording,
                Recommended = false,
                Advanced = true,
                Risk = T("Si vous installez un jour l'app Xbox ou passez au Game Pass, il faudra retirer ce verrou (Annuler)."),
                Warning = context.Hardware.Cpu.IsAsymmetricDualCcdX3D
                    ? T("Votre Ryzen X3D a besoin de la Game Bar pour placer les jeux sur les cœurs dotés du V-Cache : ce verrou peut réduire vos performances en jeu.")
                    : null,
                Writes = [new SettingWrite(SettingKey.Registry("HKLM", GameDvrPolicy, "AllowGameDVR"), SettingValue.Dword(0))],
            });
        }

        return changes;
    }

    private static int? ReadPolicy(IRegistryReader registry)
    {
        try
        {
            return registry.GetDword(Microsoft.Win32.RegistryHive.LocalMachine, GameDvrPolicy, "AllowGameDVR");
        }
        catch (MausAccessDeniedException)
        {
            return 0;
        }
    }

    private static SettingWrite Hkcu(string path, string name, int value) => new(SettingKey.Registry("HKCU", path, name), SettingValue.Dword(value));
}
