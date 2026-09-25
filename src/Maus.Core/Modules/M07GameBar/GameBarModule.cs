using System.Globalization;
using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Modules.M07GameBar.GameBarKeys;

namespace Maus.Core.Modules.M07GameBar;

/// <summary>
/// Module 7 — Xbox Game Bar. Distingue la superposition (Win+G), l'enregistrement en arrière-plan et le Mode Jeu :
/// seul l'enregistrement en arrière-plan a un coût notable, et le Mode Jeu reste activé dans tous les cas.
/// La Game Bar n'est jamais désinstallée ; sur un Ryzen X3D à deux CCD, elle est recommandée.
/// </summary>
public sealed class GameBarModule : IFixableModule
{
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    public string Id => "M07";

    public string Title => "Xbox Game Bar";

    public int Order => 70;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var registry = context.Registry;
        var x3d = context.Hardware.Cpu.IsAsymmetricDualCcdX3D;
        var packages = GamingPackages.TryRead(context.Packages);
        var (profile, chosen) = GamingPackages.Choose(x3d, packages, context.Preferences.GameBarProfile);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<Finding>
        {
            DetectBackgroundRecording(registry),
            DetectGameBarPackage(registry, packages, x3d),
            DetectGameMode(registry, x3d),
        };

        if (x3d)
        {
            findings.Add(DetectX3DRecommendation(registry, context.Hardware.Cpu.Name, packages, profile));
        }

        findings.Add(DescribeProfile(profile, packages, chosen));
        findings.Add(DetectCaptures(registry, profile));
        findings.Add(DetectControllerButton(registry, profile));
        findings.Add(DetectRecordingPolicy(registry, context.Windows, profile == GamingProfile.X3D));

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    public IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings) =>
        GameBarPlanner.Plan(Id, context, findings);

    /// <summary>« Enregistrer ce qui s'est passé » : absente, la valeur vaut 0 (désactivé), comme le défaut de Windows.</summary>
    private static Finding DetectBackgroundRecording(IRegistryReader registry)
    {
        const string id = "M07.background-recording";
        const string title = "Enregistrement en arrière-plan désactivé";
        return Guard(id, title, Recording, () =>
        {
            var value = registry.GetDword(Hkcu, GameDvrUser, "HistoricalCaptureEnabled");
            var on = value is not null and not 0;
            return new Finding
            {
                Id = id,
                Title = title,
                Category = Recording,
                Status = on ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Ok,
                Severity = Severity.Low,
                Current = value switch
                {
                    null => "désactivé (par défaut)",
                    0 => "désactivé",
                    _ => "activé",
                },
                Expected = "désactivé, quel que soit le profil",
                Explanation =
                    "« Enregistrer ce qui s'est passé » filme la partie en continu pour pouvoir sauvegarder les dernières minutes après coup. " +
                    "C'est la partie de la Game Bar qui sollicite le plus la machine (carte graphique et disque).",
                Advice = on
                    ? "Couper « Enregistrer ce qui s'est passé » dans Paramètres > Jeux > Captures (ms-settings:gaming-gamedvr). Les captures manuelles restent possibles."
                    : null,
                Fixable = on,
            };
        });
    }

    /// <summary>Sans la Game Bar, le lien <c>ms-gamingoverlay</c> n'a plus d'application associée et Windows affiche une fenêtre d'erreur.</summary>
    private static Finding DetectGameBarPackage(IRegistryReader registry, GamingPackages? packages, bool x3d)
    {
        const string id = "M07.gamebar-package";
        const string title = "Game Bar installée";
        if (packages is null)
        {
            return Finding.Unknown(id, title, "L'inventaire des applications n'a pas pu être lu.", Overlay);
        }

        if (packages.GameBar is { } gameBar)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = Overlay,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = $"installée (version {gameBar.Version})",
                Expected = "installée",
                Explanation =
                    "La Game Bar (Win+G) est la superposition de jeu de Windows. MAUS ne la désinstalle jamais : sans elle, " +
                    "Windows affiche une fenêtre d'erreur quand un jeu ou le raccourci Win+G l'appelle.",
            };
        }

        var explanation =
            "La Game Bar (Microsoft.XboxGamingOverlay) est absente. Le raccourci Win+G et certains jeux appellent pourtant le lien " +
            "ms-gamingoverlay, qui n'a plus d'application associée : Windows affiche alors une fenêtre d'erreur.";
        var games = CountRecognizedGames(registry);
        if (games > 0)
        {
            explanation += $" Windows reconnaît {games.Value.ToString(CultureInfo.InvariantCulture)} jeu(x) sur ce PC.";
        }

        if (x3d)
        {
            explanation += " Sur votre Ryzen X3D, elle sert aussi à placer les jeux sur les cœurs dotés du V-Cache.";
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = Overlay,
            Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = "absente",
            Expected = "installée",
            Explanation = explanation,
            Advice =
                $"Réinstaller « Game Bar » depuis le Microsoft Store (identifiant {GameBarStoreId}). MAUS proposera la commande " +
                $"« winget install --id {GameBarStoreId} --source msstore », lancée seulement avec votre accord.",
            Fixable = true,
        };
    }

    /// <summary>Mode Jeu : absent, <c>AutoGameModeEnabled</c> vaut activé. Il reste activé dans tous les profils.</summary>
    private static Finding DetectGameMode(IRegistryReader registry, bool x3d)
    {
        const string id = "M07.game-mode";
        const string title = "Mode Jeu activé";
        return Guard(id, title, GameMode, () =>
        {
            var value = registry.GetDword(Hkcu, GameBarUser, "AutoGameModeEnabled");
            var off = value == 0;
            var severity = x3d ? Severity.Medium : Severity.Low;
            var explanation =
                "Le Mode Jeu donne la priorité au jeu en cours et empêche Windows Update d'installer des pilotes ou d'afficher " +
                "des notifications de redémarrage pendant la partie. MAUS le laisse activé dans tous les cas.";
            if (x3d)
            {
                explanation += " Sur un Ryzen X3D à deux CCD, il aide aussi la Game Bar à placer le jeu sur les cœurs dotés du V-Cache.";
            }

            return new Finding
            {
                Id = id,
                Title = title,
                Category = GameMode,
                Status = off ? FindingStatusExtensions.ForDeviation(severity) : FindingStatus.Ok,
                Severity = severity,
                Current = value switch
                {
                    null => "activé (par défaut)",
                    0 => "désactivé",
                    _ => "activé",
                },
                Expected = "activé",
                Explanation = explanation,
                Advice = off ? "Activer « Mode Jeu » dans Paramètres > Jeux > Mode Jeu (ms-settings:gaming-gamemode)." : null,
                Fixable = off,
            };
        });
    }

    /// <summary>Décision du projet : sur un X3D à deux CCD asymétrique, Game Bar et Mode Jeu sont recommandés ; l'utilisateur peut refuser.</summary>
    private static Finding DetectX3DRecommendation(IRegistryReader registry, string cpuName, GamingPackages? packages, GamingProfile? profile)
    {
        const string id = "M07.x3d-vcache";
        const string title = "Ryzen X3D à deux CCD : garder la Game Bar et le Mode Jeu";
        return Guard(id, title, Profile, () =>
        {
            var gameBar = packages is null ? "inconnue" : packages.GameBar is null ? "absente" : "installée";
            var gameMode = registry.GetDword(Hkcu, GameBarUser, "AutoGameModeEnabled") == 0 ? "désactivé" : "activé";
            return new Finding
            {
                Id = id,
                Title = title,
                Category = Profile,
                Status = FindingStatus.Info,
                Severity = Severity.Info,
                Current = $"{cpuName} ; Game Bar : {gameBar} ; Mode Jeu : {gameMode}",
                Expected = "Game Bar installée, activée et à jour, avec le Mode Jeu (recommandé)",
                Explanation =
                    "Sur ce processeur, un seul des deux blocs de cœurs (CCD) porte le cache 3D V-Cache. La Game Bar reconnaît les jeux " +
                    "et, avec le Mode Jeu, les place sur ce bloc, ce qui donne les meilleures performances. Un jeu non reconnu peut être " +
                    "marqué comme jeu depuis Win+G.",
                Advice = profile is null or GamingProfile.X3D
                    ? "Recommandé : garder la Game Bar et le Mode Jeu. Vous pouvez choisir de les couper après avertissement : " +
                      "vos jeux risquent alors de tourner sur le bloc de cœurs sans V-Cache."
                    : $"Vous avez choisi le profil {(int)profile.Value} : vos jeux risquent de tourner sur le bloc de cœurs sans V-Cache. " +
                      "Vous pouvez revenir au profil 3 à tout moment.",
            };
        });
    }

    private static Finding DescribeProfile(GamingProfile? profile, GamingPackages? packages, bool chosenByUser)
    {
        const string id = "M07.profile";
        var title = chosenByUser ? "Profil de jeu choisi par vous" : "Profil de jeu proposé";
        if (profile is null)
        {
            return Finding.Unknown(id, title, "Le profil n'a pas pu être proposé : l'inventaire des applications n'a pas pu être lu.", Profile);
        }

        var (label, explanation) = profile.Value switch
        {
            GamingProfile.X3D => (
                "Profil 3 : Ryzen X3D à deux CCD",
                "Game Bar installée, activée et à jour, avec le Mode Jeu ; seul l'enregistrement en arrière-plan est coupé."),
            GamingProfile.XboxApp => (
                "Profil 2 : j'utilise l'app Xbox ou le Game Pass",
                "Profil Game Pass détecté : la superposition peut rester, seul l'enregistrement en arrière-plan est coupé. " +
                "L'app Xbox et les Services de jeu ne sont jamais modifiés."),
            _ => (
                "Profil 1 : je n'utilise ni l'app Xbox ni le Game Pass",
                "Captures, enregistrement en arrière-plan et ouverture par la manette sont coupés. Aucun interrupteur global n'est " +
                "documenté : Win+G ouvre encore la Game Bar, qui reste installée."),
        };

        return new Finding
        {
            Id = id,
            Title = title,
            Category = Profile,
            Status = FindingStatus.Info,
            Severity = Severity.Info,
            Current = packages is null
                ? label
                : $"{label} (app Xbox : {(packages.XboxApp is null ? "absente" : "installée")} ; Services de jeu : {(packages.GamingServices is null ? "absents" : "installés")})",
            Expected = "au choix de l'utilisateur",
            Explanation = explanation,
            Advice = chosenByUser
                ? "Vous avez choisi ce profil : MAUS s'y tient. Vous pouvez en changer à tout moment dans l'onglet Corrections."
                : "Ce profil est seulement présélectionné : vous pouvez en choisir un autre dans l'onglet Corrections avant toute correction.",
        };
    }

    /// <summary>Captures de jeu : coupées en profil 1 seulement ; inchangées dans les autres profils.</summary>
    private static Finding DetectCaptures(IRegistryReader registry, GamingProfile? profile)
    {
        const string id = "M07.captures";
        const string title = "Captures de jeu (clips et captures d'écran)";
        return Guard(id, title, Recording, () =>
        {
            var gameDvr = registry.GetDword(Hkcu, GameConfigStore, "GameDVR_Enabled");
            var appCapture = registry.GetDword(Hkcu, GameDvrUser, "AppCaptureEnabled");
            var disabledCount = (gameDvr == 0 ? 1 : 0) + (appCapture == 0 ? 1 : 0);
            var current = disabledCount switch
            {
                0 => "activées",
                1 => "désactivées en partie",
                _ => "désactivées",
            };
            const string explanation =
                "Les captures permettent d'enregistrer des clips et des captures d'écran avec la Game Bar. Elles ne coûtent rien tant " +
                "que vous ne vous en servez pas ; l'enregistrement en arrière-plan est traité à part.";

            if (profile == GamingProfile.NoXbox)
            {
                var compliant = disabledCount == 2;
                return new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Recording,
                    Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
                    Severity = Severity.Low,
                    Current = current,
                    Expected = "désactivées (profil 1)",
                    Explanation = explanation,
                    Advice = compliant ? null : "Couper les captures dans Paramètres > Jeux > Captures, ou depuis la Game Bar (Win+G > Paramètres > Captures).",
                    Fixable = !compliant,
                };
            }

            return Unchanged(id, title, Recording, current, profile, explanation);
        });
    }

    /// <summary>Ouverture par le bouton Xbox de la manette : absente, la valeur vaut activé.</summary>
    private static Finding DetectControllerButton(IRegistryReader registry, GamingProfile? profile)
    {
        const string id = "M07.controller-button";
        const string title = "Ouverture de la Game Bar par le bouton Xbox de la manette";
        return Guard(id, title, Overlay, () =>
        {
            var value = registry.GetDword(Hkcu, GameBarUser, "UseNexusForGameBarEnabled");
            var on = value != 0;
            var current = value switch
            {
                null => "activée (par défaut)",
                0 => "désactivée",
                _ => "activée",
            };
            const string explanation = "Un appui sur le bouton Xbox d'une manette ouvre la Game Bar, parfois par erreur en pleine partie.";

            if (profile == GamingProfile.NoXbox)
            {
                return new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Overlay,
                    Status = on ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Ok,
                    Severity = Severity.Low,
                    Current = current,
                    Expected = "désactivée (profil 1)",
                    Explanation = explanation,
                    Advice = on ? "Couper « Ouvrir la Game Bar avec ce bouton sur une manette » dans Paramètres > Jeux > Game Bar (ms-settings:gaming-gamebar)." : null,
                    Fixable = on,
                };
            }

            return Unchanged(id, title, Overlay, current, profile, explanation);
        });
    }

    /// <summary>Stratégie <c>AllowGameDVR</c> : verrou optionnel du profil 1, à ne jamais poser sur un Ryzen X3D à deux CCD.</summary>
    private static Finding DetectRecordingPolicy(IRegistryReader registry, WindowsInfo windows, bool x3d)
    {
        const string id = "M07.gamedvr-policy";
        const string title = "Stratégie d'enregistrement des jeux (AllowGameDVR)";
        return Guard(id, title, Recording, () =>
        {
            var value = registry.GetDword(Hklm, GameDvrPolicy, "AllowGameDVR");
            if (value != 0)
            {
                return new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Recording,
                    Status = FindingStatus.Ok,
                    Severity = Severity.Info,
                    Current = value is null ? "non configurée (enregistrement autorisé)" : "enregistrement autorisé",
                    Expected = x3d ? "non configurée (jamais posée en profil 3)" : "non configurée, ou 0 en verrou optionnel du profil 1",
                    Explanation = "Cette stratégie peut interdire l'enregistrement des jeux pour tous les utilisateurs. Elle n'est pas posée.",
                };
            }

            if (x3d)
            {
                return new Finding
                {
                    Id = id,
                    Title = title,
                    Category = Recording,
                    Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
                    Severity = Severity.Medium,
                    Current = "enregistrement interdit (0)",
                    Expected = "non configurée (jamais posée en profil 3)",
                    Explanation =
                        "Cette stratégie coupe les fonctions de jeu de la Game Bar. Sur un Ryzen X3D à deux CCD, la Game Bar doit rester " +
                        "pleinement active pour placer les jeux sur les cœurs dotés du V-Cache.",
                    Advice = "Retirer la stratégie « Active ou désactive l'enregistrement et la diffusion de jeux Windows » (AllowGameDVR).",
                    Fixable = true,
                };
            }

            var explanation = "Verrou optionnel du profil 1 : l'enregistrement des jeux est interdit pour tous les utilisateurs de ce PC.";
            if (windows.IsHomeEdition)
            {
                explanation += " Sur Windows Famille, cette stratégie n'est pas garantie.";
            }

            return new Finding
            {
                Id = id,
                Title = title,
                Category = Recording,
                Status = FindingStatus.Info,
                Severity = Severity.Info,
                Current = "enregistrement interdit (0)",
                Expected = "non configurée, ou 0 en verrou optionnel du profil 1",
                Explanation = explanation,
            };
        });
    }

    private static Finding Unchanged(string id, string title, string category, string current, GamingProfile? profile, string explanation) => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Status = FindingStatus.Info,
        Severity = Severity.Info,
        Current = current,
        Expected = profile switch
        {
            GamingProfile.XboxApp => "inchangé (profil 2)",
            GamingProfile.X3D => "inchangé (profil 3)",
            _ => "selon le profil choisi",
        },
        Explanation = explanation,
    };

    /// <summary>Jeux reconnus par Windows (sous-clés de <c>GameConfigStore\Children</c>), ou <c>null</c> si illisible.</summary>
    private static int? CountRecognizedGames(IRegistryReader registry)
    {
        try
        {
            return registry.GetSubKeyNames(Hkcu, GameConfigStoreChildren).Count;
        }
        catch (MausAccessDeniedException)
        {
            return null;
        }
    }

    private static Finding Guard(string id, string title, string category, Func<Finding> check)
    {
        try
        {
            return check();
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, category);
        }
    }
}
