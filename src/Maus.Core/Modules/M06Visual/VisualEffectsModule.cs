using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Rules;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M06Visual;

/// <summary>
/// Module 6 — Interface et effets visuels. Garde 4 effets utiles (miniatures, contenu des fenêtres déplacées,
/// sélection translucide, lissage des polices) et signale les autres comme optimisations possibles.
/// </summary>
public sealed class VisualEffectsModule : IFixableModule
{
    private static string Category => T("Effets désactivés");

    /// <summary>Effets pilotés par SystemParametersInfo : code GET, libellé Windows, valeur attendue.</summary>
    private static readonly (string Id, uint Action, string Title, bool Expected, string Explanation)[] SpiEffects =
    [
        ("M06.keep.drag", SpiGet.DragFullWindows, T("Contenu des fenêtres pendant leur déplacement (à conserver)"), true,
            T("Voir le contenu d'une fenêtre qu'on déplace est plus confortable ; cet effet fait partie des 4 conservés.")),
        ("M06.keep.fonts", SpiGet.FontSmoothing, T("Lissage des polices d'écran (à conserver)"), true,
            T("Sans lissage, le texte devient crénelé et fatigant à lire ; cet effet fait partie des 4 conservés.")),
        ("M06.client-animation", SpiGet.ClientAreaAnimation, T("Animations dans les fenêtres désactivées"), false,
            T("Couvre « Animer les contrôles et éléments » et l'interrupteur « Effets d'animation » des Paramètres.")),
        ("M06.menu-animation", SpiGet.MenuAnimation, T("Fondu ou glissement des menus désactivé"), false,
            T("Les menus s'ouvrent instantanément sans cette animation.")),
        ("M06.tooltip-animation", SpiGet.TooltipAnimation, T("Fondu des infobulles désactivé"), false,
            T("Les infobulles apparaissent instantanément sans cette animation.")),
        ("M06.selection-fade", SpiGet.SelectionFade, T("Disparition progressive des éléments de menu désactivée"), false,
            T("Effet décoratif après un clic dans un menu.")),
        ("M06.cursor-shadow", SpiGet.CursorShadow, T("Ombre sous le pointeur désactivée"), false,
            T("Effet décoratif sous le pointeur de la souris.")),
        ("M06.window-shadow", SpiGet.DropShadow, T("Ombre sous les fenêtres désactivée"), false,
            T("Effet décoratif autour des fenêtres.")),
        ("M06.combobox-animation", SpiGet.ComboBoxAnimation, T("Animation des listes déroulantes désactivée"), false,
            T("Les listes s'ouvrent instantanément sans cette animation.")),
        ("M06.smooth-scrolling", SpiGet.ListBoxSmoothScrolling, T("Défilement doux des listes désactivé"), false,
            T("Le défilement doux ralentit la navigation dans les longues listes.")),
    ];

    /// <summary>Code SPI_SET* de chaque effet ; la valeur passe par uiParam pour le glisser des fenêtres et le lissage des polices.</summary>
    private static readonly Dictionary<uint, (uint Set, bool UiParam)> SpiSetters = new()
    {
        [SpiGet.DragFullWindows] = (SpiSet.DragFullWindows, true),
        [SpiGet.FontSmoothing] = (SpiSet.FontSmoothing, true),
        [SpiGet.ClientAreaAnimation] = (SpiSet.ClientAreaAnimation, false),
        [SpiGet.MenuAnimation] = (SpiSet.MenuAnimation, false),
        [SpiGet.TooltipAnimation] = (SpiSet.TooltipAnimation, false),
        [SpiGet.SelectionFade] = (SpiSet.SelectionFade, false),
        [SpiGet.CursorShadow] = (SpiSet.CursorShadow, false),
        [SpiGet.DropShadow] = (SpiSet.DropShadow, false),
        [SpiGet.ComboBoxAnimation] = (SpiSet.ComboBoxAnimation, false),
        [SpiGet.ListBoxSmoothScrolling] = (SpiSet.ListBoxSmoothScrolling, false),
    };

    private static Dictionary<string, string> ActionTitles => new(StringComparer.Ordinal)
    {
        ["M06.keep.drag"] = T("Réafficher le contenu des fenêtres pendant leur déplacement"),
        ["M06.keep.fonts"] = T("Réactiver le lissage des polices"),
        ["M06.client-animation"] = T("Désactiver les animations dans les fenêtres"),
        ["M06.menu-animation"] = T("Désactiver l'animation des menus"),
        ["M06.tooltip-animation"] = T("Désactiver le fondu des infobulles"),
        ["M06.selection-fade"] = T("Désactiver la disparition progressive des menus"),
        ["M06.cursor-shadow"] = T("Désactiver l'ombre sous le pointeur"),
        ["M06.window-shadow"] = T("Désactiver l'ombre sous les fenêtres"),
        ["M06.combobox-animation"] = T("Désactiver l'animation des listes déroulantes"),
        ["M06.smooth-scrolling"] = T("Désactiver le défilement doux des listes"),
    };

    private static readonly Lazy<IReadOnlyList<RegistryRule>> Rules = new(() => EmbeddedCatalog.LoadRegistryRules("m06-visual-rules.json"));

    public string Id => "M06";

    public string Title => T("Interface et effets visuels");

    public int Order => 60;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>(RegistryRuleEvaluator.EvaluateAll(Rules.Value, context.Registry))
        {
            DetectWidgets(context.Registry, context.Windows),
            DetectMinimizeAnimation(context.SystemParameters),
        };

        foreach (var effect in SpiEffects)
        {
            var current = context.SystemParameters.GetBool(effect.Action);
            findings.Add(current is null
                ? Finding.Unknown(effect.Id, effect.Title, T("Lecture du paramètre système impossible."), Category)
                : new Finding
                {
                    Id = effect.Id,
                    Title = effect.Title,
                    Category = effect.Id.StartsWith("M06.keep", StringComparison.Ordinal) ? T("Effets conservés") : Category,
                    Status = current == effect.Expected ? FindingStatus.Ok : FindingStatus.Improvable,
                    Severity = Severity.Low,
                    Current = OnOff(current.Value),
                    Expected = OnOff(effect.Expected),
                    Explanation = effect.Explanation,
                    Fixable = current != effect.Expected,
                });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    /// <summary>
    /// Corrections : SPI par les mêmes API que Windows, registre par le catalogue, Widgets par la stratégie (Pro et plus).
    /// Le mode « personnalisé » (<c>VisualFXSetting</c> = 3) passe en dernier, pour que la boîte de dialogue l'affiche.
    /// </summary>
    public IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        bool Deviates(string id) => byId.TryGetValue(id, out var f) && f.Status == FindingStatus.Improvable;

        var changes = new List<PlannedChange>();
        foreach (var effect in SpiEffects.Where(e => Deviates(e.Id)))
        {
            var (set, uiParam) = SpiSetters[effect.Action];
            changes.Add(Change(
                effect.Id,
                ActionTitles[effect.Id],
                effect.Explanation,
                effect.Id.StartsWith("M06.keep", StringComparison.Ordinal) ? T("Effets conservés") : Category,
                new SettingWrite(SettingKey.Spi(effect.Action, set, uiParam), SettingValue.Bool(effect.Expected))));
        }

        if (Deviates("M06.minimize-animation"))
        {
            changes.Add(Change(
                "M06.minimize-animation",
                T("Désactiver l'animation de réduction et d'agrandissement"),
                T("Les fenêtres réduites ou agrandies apparaissent instantanément."),
                Category,
                new SettingWrite(SettingKey.MinimizeAnimation, SettingValue.Bool(false))));
        }

        if (Deviates("M06.widgets") && !context.Windows.IsHomeEdition)
        {
            changes.Add(Change(
                "M06.widgets",
                T("Désactiver les Widgets (stratégie AllowNewsAndInterests)"),
                T("Coupe tout le panneau Widgets, bouton de la barre des tâches compris. Le réglage de la barre des tâches est protégé par Windows (UCPD) : MAUS passe par la stratégie officielle, sans jamais toucher à cette protection."),
                T("Barre des tâches"),
                new SettingWrite(SettingKey.Registry("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests"), SettingValue.Dword(0))) with
            {
                Gain = T("Moins de contenu en ligne chargé en arrière-plan (actualités, météo, publicités)."),
                Effect = ChangeEffect.ExplorerRestart,
            });
        }

        var fromRules = RegistryRulePlanner.Plan(Id, Rules.Value, findings);
        changes.AddRange(fromRules.Where(c => c.Id != "M06.visualfx-mode"));
        changes.AddRange(fromRules.Where(c => c.Id == "M06.visualfx-mode"));
        return changes;
    }

    private PlannedChange Change(string id, string title, string description, string category, SettingWrite write) => new()
    {
        Id = id,
        ModuleId = Id,
        Title = title,
        Description = description,
        Category = category,
        Gain = T("Gain surtout visuel : Windows paraît plus réactif."),
        Writes = [write],
    };

    /// <summary>
    /// Les Widgets sont coupés soit par la stratégie <c>AllowNewsAndInterests</c> (Pro et plus), soit par <c>TaskbarDa</c>,
    /// que le pilote UCPD protège en écriture sur les builds récents.
    /// </summary>
    private static Finding DetectWidgets(IRegistryReader registry, WindowsInfo windows)
    {
        var title = T("Widgets désactivés");
        try
        {
            var policy = registry.GetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests");
            var taskbar = registry.GetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa");
            var disabled = policy == 0 || taskbar == 0;
            return new Finding
            {
                Id = "M06.widgets",
                Title = title,
                Category = T("Barre des tâches"),
                Status = disabled ? FindingStatus.Ok : FindingStatus.Improvable,
                Severity = Severity.Low,
                Current = disabled ? T("désactivés") : "actifs",
                Expected = T("désactivés"),
                Explanation = T("Le panneau Widgets charge du contenu en ligne en arrière-plan (actualités, météo, publicités)."),
                Advice = disabled ? null : T("Désactiver les Widgets par la stratégie AllowNewsAndInterests (Pro et plus) ou dans Paramètres > Barre des tâches."),
                Fixable = !disabled && !windows.IsHomeEdition,
            };
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired("M06.widgets", title, T("Barre des tâches"));
        }
    }

    private static Finding DetectMinimizeAnimation(ISystemParametersReader parameters)
    {
        const string id = "M06.minimize-animation";
        var title = T("Animation de réduction et d'agrandissement désactivée");
        var current = parameters.GetMinimizeAnimation();
        return current is null
            ? Finding.Unknown(id, title, T("Lecture du paramètre système impossible."), Category)
            : new Finding
            {
                Id = id,
                Title = title,
                Category = Category,
                Status = current.Value ? FindingStatus.Improvable : FindingStatus.Ok,
                Severity = Severity.Low,
                Current = OnOff(current.Value),
                Expected = OnOff(false),
                Fixable = current.Value,
                Explanation = T("L'animation retarde l'apparition des fenêtres réduites ou agrandies."),
            };
    }

    private static string OnOff(bool value) => value ? T("activé") : T("désactivé");
}
