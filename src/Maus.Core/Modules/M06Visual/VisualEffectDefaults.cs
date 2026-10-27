using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M06Visual;

/// <summary>Une case de « Options de performances » : réglage à écrire et valeur par défaut déclarée par Windows.</summary>
internal sealed record VisualEffectDefault(string Name, SettingKey Key, SettingValue Value);

/// <summary>
/// « Valeurs Windows » des effets visuels : Windows déclare chaque case de « Options de performances » sous
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\&lt;nom&gt;</c> (<c>DefaultValue</c>, puis
/// <c>SPIActionGet</c> / <c>SPIActionSet</c> ou <c>RegPath</c> / <c>ValueName</c> avec <c>CheckedValue</c> / <c>UncheckedValue</c>).
/// MAUS lit ces définitions sur le PC au lieu de coder des valeurs en dur. Seuls les codes SPI que MAUS connaît déjà sont
/// suivis, et seules les valeurs du profil (HKCU) ; la case des miniatures (gestionnaire CLSID) et <c>VisualFXSetting</c>
/// (sens des valeurs non publié) ne sont pas touchées. La stratégie Widgets à 0 est retirée (stratégie absente = réglage de Windows).
/// </summary>
internal static class VisualEffectDefaults
{
    internal const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    internal const string WidgetsPolicy = @"SOFTWARE\Policies\Microsoft\Dsh";
    private const int CurrentUserRoot = unchecked((int)0x80000001);
    private const int MaxEffects = 64;

    /// <summary>Définitions lisibles et sûres ; liste vide si Windows n'en déclare aucune.</summary>
    public static IReadOnlyList<VisualEffectDefault> Read(IRegistryReader registry, IReadOnlyDictionary<uint, (uint Set, bool UiParam)> knownSpi)
    {
        var found = new List<VisualEffectDefault>();
        foreach (var name in registry.GetSubKeyNames(RegistryHive.LocalMachine, Root).Take(MaxEffects))
        {
            var path = $@"{Root}\{name}";
            if (!string.Equals(registry.GetString(RegistryHive.LocalMachine, path, "Type"), "checkbox", StringComparison.OrdinalIgnoreCase)
                || registry.GetDword(RegistryHive.LocalMachine, path, "DefaultValue") is not { } defaultValue)
            {
                continue;
            }

            var on = defaultValue != 0;
            if (registry.GetDword(RegistryHive.LocalMachine, path, "SPIActionGet") is { } get
                && registry.GetDword(RegistryHive.LocalMachine, path, "SPIActionSet") is { } set)
            {
                if ((uint)get == SpiGet.Animation)
                {
                    found.Add(new(name, SettingKey.MinimizeAnimation, SettingValue.Bool(on)));
                }
                else if (knownSpi.TryGetValue((uint)get, out var known) && known.Set == (uint)set)
                {
                    found.Add(new(name, SettingKey.Spi((uint)get, known.Set, known.UiParam), SettingValue.Bool(on)));
                }

                continue;
            }

            var regPath = registry.GetString(RegistryHive.LocalMachine, path, "RegPath");
            var valueName = registry.GetString(RegistryHive.LocalMachine, path, "ValueName");
            var root = registry.GetDword(RegistryHive.LocalMachine, path, "HKeyRoot") ?? CurrentUserRoot;
            var value = registry.GetDword(RegistryHive.LocalMachine, path, on ? "CheckedValue" : "UncheckedValue");
            if (!string.IsNullOrWhiteSpace(regPath) && !string.IsNullOrWhiteSpace(valueName) && root == CurrentUserRoot && value is not null)
            {
                found.Add(new(name, SettingKey.Registry("HKCU", regPath, valueName), SettingValue.Dword(value.Value)));
            }
        }

        return found;
    }

    /// <summary>Vrai si le réglage diffère de la valeur de Windows ; une valeur absente ou illisible n'est pas comptée.</summary>
    public static bool Differs(VisualEffectDefault effect, IRegistryReader registry, ISystemParametersReader parameters)
    {
        var expected = effect.Value.Data == "1";
        return effect.Key.Kind switch
        {
            SettingKind.MinimizeAnimation => parameters.GetMinimizeAnimation() is { } current && current != expected,
            SettingKind.SystemParameter => parameters.GetBool(effect.Key.SpiGet) is { } current && current != expected,
            SettingKind.Registry => registry.GetDword(RegistryHive.CurrentUser, effect.Key.Path!, effect.Key.Name!) is { } current
                && current.ToString(System.Globalization.CultureInfo.InvariantCulture) != effect.Value.Data,
            _ => false,
        };
    }

    public static Finding Detect(IRegistryReader registry, ISystemParametersReader parameters, IReadOnlyDictionary<uint, (uint Set, bool UiParam)> knownSpi)
    {
        var effects = Read(registry, knownSpi);
        var different = effects.Count(e => Differs(e, registry, parameters));
        var widgetsPolicy = WidgetsBlockedByPolicy(registry);
        var total = different + (widgetsPolicy ? 1 : 0);
        return new Finding
        {
            Id = "M06.windows-defaults",
            Title = T("Effets visuels et valeurs de Windows"),
            Category = T("Effets"),
            Status = FindingStatus.Info,
            Severity = Severity.Info,
            Current = total == 0
                ? T("identiques aux valeurs de Windows ({0} cases comparées)", effects.Count)
                : T("{0} réglage(s) différent(s) des valeurs de Windows ({1} cases comparées)", total, effects.Count),
            Expected = T("au choix : vos réglages ou ceux de Windows"),
            Explanation = T("Windows déclare lui-même la valeur par défaut de chaque case de « Options de performances » ; MAUS la lit sur ce PC et la compare à vos réglages.")
                + (widgetsPolicy ? T(" Une stratégie coupe aussi les Widgets.") : string.Empty),
            Advice = total == 0
                ? null
                : T("Pour tout remettre comme Windows l'a prévu, cochez « Remettre les effets visuels aux valeurs de Windows » dans l'onglet Corrections (option avancée). « Annuler » rétablit vos réglages actuels."),
            Fixable = total > 0,
        };
    }

    /// <summary>Stratégie Widgets à 0, la valeur que pose MAUS : la retirer rend la main au réglage de Windows.</summary>
    private static bool WidgetsBlockedByPolicy(IRegistryReader registry) =>
        registry.GetDword(RegistryHive.LocalMachine, WidgetsPolicy, "AllowNewsAndInterests") == 0;

    public static PlannedChange? Plan(string moduleId, IRegistryReader registry, ISystemParametersReader parameters,
        IReadOnlyDictionary<uint, (uint Set, bool UiParam)> knownSpi, IReadOnlyList<Finding> findings)
    {
        if (!findings.Any(f => f.Id == "M06.windows-defaults" && f.Fixable))
        {
            return null;
        }

        var writes = Read(registry, knownSpi)
            .Where(e => Differs(e, registry, parameters))
            .Select(e => new SettingWrite(e.Key, e.Value))
            .ToList();
        if (WidgetsBlockedByPolicy(registry))
        {
            writes.Add(new SettingWrite(SettingKey.Registry("HKLM", WidgetsPolicy, "AllowNewsAndInterests"), null));
        }

        return writes.Count == 0
            ? null
            : new PlannedChange
            {
                Id = "M06.windows-defaults",
                ModuleId = moduleId,
                Title = T("Remettre les effets visuels aux valeurs de Windows"),
                Description = T("Remet chaque case de « Options de performances » à la valeur que Windows déclare par défaut sur ce PC, et retire la stratégie des Widgets si elle existe. " +
                    "Le mode « Paramètres personnalisés » de la fenêtre Windows n'est pas changé."),
                Gain = T("Aucun gain de vitesse : retour à l'apparence prévue par Windows."),
                Risk = T("Défait les optimisations visuelles déjà faites (animations, ombres, Widgets)."),
                Category = T("Effets"),
                Effect = ChangeEffect.ExplorerRestart,
                Recommended = false,
                Advanced = true,
                Writes = writes,
            };
    }
}
