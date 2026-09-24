using Maus.Core.Platform;
using Maus.Core.Rules;
using Microsoft.Win32;

namespace Maus.Core.Modules.M06Visual;

/// <summary>
/// Module 6 — Interface et effets visuels. Garde 4 effets utiles (miniatures, contenu des fenêtres déplacées,
/// sélection translucide, lissage des polices) et signale les autres comme optimisations possibles.
/// </summary>
public sealed class VisualEffectsModule : IAuditModule
{
    private const string Category = "Effets désactivés";

    /// <summary>Effets pilotés par SystemParametersInfo : code GET, libellé Windows, valeur attendue.</summary>
    private static readonly (string Id, uint Action, string Title, bool Expected, string Explanation)[] SpiEffects =
    [
        ("M06.keep.drag", SpiGet.DragFullWindows, "Contenu des fenêtres pendant leur déplacement (à conserver)", true,
            "Voir le contenu d'une fenêtre qu'on déplace est plus confortable ; cet effet fait partie des 4 conservés."),
        ("M06.keep.fonts", SpiGet.FontSmoothing, "Lissage des polices d'écran (à conserver)", true,
            "Sans lissage, le texte devient crénelé et fatigant à lire ; cet effet fait partie des 4 conservés."),
        ("M06.client-animation", SpiGet.ClientAreaAnimation, "Animations dans les fenêtres désactivées", false,
            "Couvre « Animer les contrôles et éléments » et l'interrupteur « Effets d'animation » des Paramètres."),
        ("M06.menu-animation", SpiGet.MenuAnimation, "Fondu ou glissement des menus désactivé", false,
            "Les menus s'ouvrent instantanément sans cette animation."),
        ("M06.tooltip-animation", SpiGet.TooltipAnimation, "Fondu des infobulles désactivé", false,
            "Les infobulles apparaissent instantanément sans cette animation."),
        ("M06.selection-fade", SpiGet.SelectionFade, "Disparition progressive des éléments de menu désactivée", false,
            "Effet décoratif après un clic dans un menu."),
        ("M06.cursor-shadow", SpiGet.CursorShadow, "Ombre sous le pointeur désactivée", false,
            "Effet décoratif sous le pointeur de la souris."),
        ("M06.window-shadow", SpiGet.DropShadow, "Ombre sous les fenêtres désactivée", false,
            "Effet décoratif autour des fenêtres."),
        ("M06.combobox-animation", SpiGet.ComboBoxAnimation, "Animation des listes déroulantes désactivée", false,
            "Les listes s'ouvrent instantanément sans cette animation."),
        ("M06.smooth-scrolling", SpiGet.ListBoxSmoothScrolling, "Défilement doux des listes désactivé", false,
            "Le défilement doux ralentit la navigation dans les longues listes."),
    ];

    private static readonly Lazy<IReadOnlyList<RegistryRule>> Rules = new(() => EmbeddedCatalog.LoadRegistryRules("m06-visual-rules.json"));

    public string Id => "M06";

    public string Title => "Interface et effets visuels";

    public int Order => 60;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>(RegistryRuleEvaluator.EvaluateAll(Rules.Value, context.Registry))
        {
            DetectWidgets(context.Registry),
            DetectMinimizeAnimation(context.SystemParameters),
        };

        foreach (var effect in SpiEffects)
        {
            var current = context.SystemParameters.GetBool(effect.Action);
            findings.Add(current is null
                ? Finding.Unknown(effect.Id, effect.Title, "Lecture du paramètre système impossible.", Category)
                : new Finding
                {
                    Id = effect.Id,
                    Title = effect.Title,
                    Category = effect.Id.StartsWith("M06.keep", StringComparison.Ordinal) ? "Effets conservés" : Category,
                    Status = current == effect.Expected ? FindingStatus.Ok : FindingStatus.Improvable,
                    Severity = Severity.Low,
                    Current = OnOff(current.Value),
                    Expected = OnOff(effect.Expected),
                    Explanation = effect.Explanation,
                });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    /// <summary>
    /// Les Widgets sont coupés soit par la stratégie <c>AllowNewsAndInterests</c> (Pro et plus), soit par <c>TaskbarDa</c>,
    /// que le pilote UCPD protège en écriture sur les builds récents.
    /// </summary>
    private static Finding DetectWidgets(IRegistryReader registry)
    {
        const string title = "Widgets désactivés";
        try
        {
            var policy = registry.GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests");
            var taskbar = registry.GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa");
            var disabled = policy == 0 || taskbar == 0;
            return new Finding
            {
                Id = "M06.widgets",
                Title = title,
                Category = "Barre des tâches",
                Status = disabled ? FindingStatus.Ok : FindingStatus.Improvable,
                Severity = Severity.Low,
                Current = disabled ? "désactivés" : "actifs",
                Expected = "désactivés",
                Explanation = "Le panneau Widgets charge du contenu en ligne en arrière-plan (actualités, météo, publicités).",
                Advice = disabled ? null : "Désactiver les Widgets par la stratégie AllowNewsAndInterests (Pro et plus) ou dans Paramètres > Barre des tâches.",
            };
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired("M06.widgets", title, "Barre des tâches");
        }
    }

    private static Finding DetectMinimizeAnimation(ISystemParametersReader parameters)
    {
        const string id = "M06.minimize-animation";
        const string title = "Animation de réduction et d'agrandissement désactivée";
        var current = parameters.GetMinimizeAnimation();
        return current is null
            ? Finding.Unknown(id, title, "Lecture du paramètre système impossible.", Category)
            : new Finding
            {
                Id = id,
                Title = title,
                Category = Category,
                Status = current.Value ? FindingStatus.Improvable : FindingStatus.Ok,
                Severity = Severity.Low,
                Current = OnOff(current.Value),
                Expected = OnOff(false),
                Explanation = "L'animation retarde l'apparition des fenêtres réduites ou agrandies.",
            };
    }

    private static string OnOff(bool value) => value ? "activé" : "désactivé";
}
