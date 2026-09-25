using System.Globalization;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M14Display;

/// <summary>
/// Module 14 — Écran : fréquence et HDR. Vérifie que chaque écran tourne à sa fréquence maximale, sans que le HDR
/// force une compression des couleurs en 4:2:2 ou 4:2:0, et qu'il est relié à la bonne carte graphique.
/// </summary>
public sealed class DisplayModule : IAuditModule
{
    private static string GlobalCategory => T("Réglages graphiques de Windows");
    private const string DirectXPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static string SettingsLinks => T("Paramètres > Système > Écran (ms-settings:display) et Graphiques (ms-settings:display-advancedgraphics)");

    /// <summary>Tolérance entre la fréquence exacte (59,94 Hz) et la valeur entière des modes (60 Hz).</summary>
    private const double RefreshTolerance = 1.0;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly IDisplayConfigReader _reader;

    public DisplayModule()
        : this(new Win32DisplayConfigReader())
    {
    }

    internal DisplayModule(IDisplayConfigReader reader)
    {
        _reader = reader;
    }

    public string Id => "M14";

    public string Title => T("Écran : fréquence et HDR");

    public int Order => 140;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var paths = _reader.ReadActivePaths();
        if (paths is null || paths.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Finding>>(
            [
                Finding.Unknown(
                    "M14.displays",
                    T("Écrans actifs"),
                    paths is null
                        ? T("Lecture de la configuration d'affichage impossible (session sans écran, bureau à distance ou API indisponible).")
                        : T("Aucun écran actif n'a été trouvé : impossible de contrôler la fréquence et le HDR."),
                    GlobalCategory),
                DetectWindowsGraphicsSettings(context.Registry),
            ]);
        }

        var screens = paths.Select((path, index) => new Screen(path, index + 1, Label(path, index + 1))).ToList();
        var findings = new List<Finding>();
        findings.AddRange(screens.Select(s => DetectAdapter(context.Hardware, s)).OfType<Finding>());
        findings.AddRange(screens.Select(s => DetectRefresh(context.Hardware, s)));
        findings.AddRange(screens.Where(s => s.Path.Color is not null).Select(DetectEncoding));
        findings.AddRange(screens.Where(s => s.Path.Color is { HdrActive: true }).Select(DetectBitDepth));
        findings.AddRange(screens.Select(DetectHdr));
        findings.AddRange(screens.Select(DetectConnector));
        findings.AddRange(screens.Select(DetectResolution).OfType<Finding>());

        if (screens.Any(s => MaxRefresh(s.Path) >= 100))
        {
            findings.Add(VariableRefreshReminder());
        }

        findings.Add(DetectWindowsGraphicsSettings(context.Registry));
        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    // ----- Carte graphique qui pilote l'écran -----

    private static Finding? DetectAdapter(HardwareProfile hardware, Screen screen)
    {
        // Sur portable, la dalle interne (et souvent la sortie HDMI) passe par le graphique intégré : c'est normal.
        if (hardware.FormFactor != FormFactor.Desktop || !hardware.HasDedicatedGpu || IsIndirect(screen.Path.Output))
        {
            return null;
        }

        var id = $"M14.adapter.{screen.Slug}";
        var title = T("Écran branché sur la carte graphique : {0}", screen.Label);
        var gpu = FindGpu(hardware, screen.Path);
        if (gpu is null)
        {
            return Finding.Unknown(id, title, T("Impossible de relier cet écran à l'une des cartes graphiques détectées."), screen.Category);
        }

        var dedicated = hardware.Gpus.First(g => !g.IsIntegrated);
        if (!gpu.IsIntegrated)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = screen.Category,
                Status = FindingStatus.Ok,
                Severity = Severity.High,
                Current = T("branché sur {0}", gpu.Name),
                Expected = T("une sortie de la carte graphique dédiée"),
                Explanation = T("L'écran est relié à la carte graphique dédiée : les jeux et les applications 3D profitent de toute sa puissance."),
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = screen.Category,
            Status = FindingStatusExtensions.ForDeviation(Severity.High),
            Severity = Severity.High,
            Current = T("branché sur {0} (graphique intégré au processeur, sortie de la carte mère)", gpu.Name),
            Expected = T("branché sur {0}", dedicated.Name),
            Explanation = T("Votre écran est branché sur la carte mère. L'image passe alors par le graphique intégré au processeur, bien moins puissant : " +
                          "jeux saccadés, fréquences élevées parfois indisponibles, et la carte graphique dédiée travaille peu ou pas du tout."),
            Advice = T("Branchez-le sur une sortie de la carte graphique, plus bas à l'arrière du boîtier (prises horizontales sous les ports USB de la carte mère)."),
        };
    }

    // ----- Fréquence de rafraîchissement -----

    private static Finding DetectRefresh(HardwareProfile hardware, Screen screen)
    {
        var id = $"M14.refresh.{screen.Slug}";
        var title = T("Fréquence de rafraîchissement : {0}", screen.Label);
        var path = screen.Path;
        if (path.RefreshHz is not { } current || current <= 0)
        {
            return Finding.Unknown(id, title, T("Fréquence actuelle de l'écran illisible."), screen.Category);
        }

        var native = Native(path);
        var atCurrent = path.Width > 0 ? DisplayParsers.MaxRefreshAt(path.Modes, path.Width, path.Height) : null;
        var reference = atCurrent ?? (native is { } n ? DisplayParsers.MaxRefreshAt(path.Modes, n.Width, n.Height) : null);
        if (reference is not { } max)
        {
            return Finding.Unknown(id, title, T("Liste des fréquences proposées par l'écran illisible."), screen.Category);
        }

        var resolution = atCurrent is not null ? $"{path.Width}×{path.Height}" : native is { } nat ? $"{nat.Width}×{nat.Height}" : T("résolution actuelle");
        var lower = current < max - RefreshTolerance;
        var color = path.Color;
        var explanation = T("La fréquence de rafraîchissement est le nombre d'images que l'écran affiche par seconde. " +
                                   "Plus elle est haute, plus le mouvement de la souris, le défilement et les jeux sont fluides.");
        if (!lower)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = screen.Category,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = $"{Hz(current)} Hz",
                Expected = T("{0} Hz (maximum en {1})", max, resolution),
                Explanation = explanation,
            };
        }

        // En HDR avec des couleurs complètes, une fréquence plus basse peut être un choix délibéré pour éviter le 4:2:2.
        var deliberateHdr = color is { HdrActive: true } && !DisplayParsers.IsChromaSubsampled(color.Encoding);
        var severity = deliberateHdr ? Severity.Low : Severity.Medium;
        var advice = T("Paramètres > Système > Écran > Écran avancé (ms-settings:display-advanced) : choisissez {0} Hz. " +
                     "Si l'écran ne propose pas cette valeur, vérifiez le câble (DisplayPort ou HDMI certifié) et le menu de l'écran.", max);
        if (deliberateHdr)
        {
            advice += T(" En HDR, vérifiez ensuite que l'encodage reste en RGB : sinon, gardez la fréquence actuelle ou passez en DisplayPort.");
        }

        if (hardware.IsLaptop && DisplayParsers.IsInternal(path.Output))
        {
            advice += T(" Sur un portable, la fréquence maximale est conseillée sur secteur ; la fréquence dynamique (DRR) la baisse d'elle-même sur batterie (voir Module 5).");
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = screen.Category,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = $"{Hz(current)} Hz",
            Expected = T("{0} Hz (maximum en {1})", max, resolution),
            Explanation = T("Votre écran accepte {0} Hz mais tourne à {1} Hz. Vous gagnez en fluidité partout, mais pas en images par seconde dans les jeux. " +
                          "En HDR, le débit du câble est limité : une fréquence plus basse donne parfois une image plus nette.", max, Hz(current)),
            Advice = advice,
            Fixable = true,
        };
    }

    // ----- Encodage des couleurs -----

    private static Finding DetectEncoding(Screen screen)
    {
        var color = screen.Path.Color!;
        var subsampled = DisplayParsers.IsChromaSubsampled(color.Encoding);
        var hdmi = screen.Path.Output == OutputTechnology.Hdmi;
        string? advice = null;
        if (subsampled)
        {
            advice = color.HdrActive
                ? T("Baissez la fréquence d'un palier (celui qui rétablit le RGB n'est pas forcément 120 Hz), ")
                : T("Baissez la fréquence ou la profondeur de couleur, ");
            advice += hdmi
                ? T("passez en DisplayPort, ou utilisez un câble HDMI certifié Ultra High Speed.")
                : T("activez la compression DSC dans le menu de l'écran si elle existe, ou utilisez un câble certifié.");
        }

        return new Finding
        {
            Id = $"M14.color-encoding.{screen.Slug}",
            Title = T("Couleurs transmises sans compression : {0}", screen.Label),
            Category = screen.Category,
            Status = subsampled ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = Severity.Medium,
            Current = $"{DisplayParsers.EncodingLabel(color.Encoding)}, {color.BitsPerColorChannel} bits, {(color.HdrActive ? "HDR" : "SDR")}",
            Expected = T("RGB ou YCbCr 4:4:4"),
            Explanation = subsampled
                ? T("En {0} à {1} Hz, votre câble compresse les couleurs et le texte bave (franges colorées). " +
                  "Le débit du câble est fixe et le HDR exige 10 bits par couleur au lieu de 8. Une fréquence plus basse ou le DisplayPort donneront une image plus nette.", (color.HdrActive ? "HDR" : "SDR"), Hz(screen.Path.RefreshHz ?? 0))
                : T("Les couleurs passent sans compression : le texte reste net."),
            Advice = advice,
            Fixable = subsampled,
        };
    }

    private static Finding DetectBitDepth(Screen screen)
    {
        var color = screen.Path.Color!;
        var enough = color.BitsPerColorChannel >= 10;
        return new Finding
        {
            Id = $"M14.bit-depth.{screen.Slug}",
            Title = T("Profondeur de couleur en HDR : {0}", screen.Label),
            Category = screen.Category,
            Status = enough ? FindingStatus.Ok : FindingStatus.Info,
            Current = T("{0} bits par couleur", color.BitsPerColorChannel),
            Expected = T("10 bits ou plus en HDR"),
            Explanation = enough
                ? T("Le HDR dispose de 10 bits par couleur : les dégradés restent fluides.")
                : T("En HDR sur 8 bits, les dégradés (ciel, ombres) peuvent apparaître en escalier (effet « banding »). Le pilote atténue souvent cet effet par tramage."),
            Advice = enough
                ? null
                : T("Dans le panneau du pilote graphique, choisissez 10 bits de profondeur de couleur de sortie si le câble le permet, quitte à baisser la fréquence d'un palier."),
        };
    }

    // ----- HDR -----

    private static Finding DetectHdr(Screen screen)
    {
        var color = screen.Path.Color;
        var id = $"M14.hdr.{screen.Slug}";
        var title = $"HDR : {screen.Label}";
        if (color is null)
        {
            return Finding.Unknown(id, title, T("État de la couleur avancée (HDR) illisible pour cet écran."), screen.Category);
        }

        var current = !color.HdrSupported
            ? T("non pris en charge (ou désactivé dans le menu de l'écran)")
            : color.HdrActive ? T("activé") : T("désactivé");
        return new Finding
        {
            Id = id,
            Title = title,
            Category = screen.Category,
            Status = FindingStatus.Info,
            Current = current,
            Expected = T("au choix de l'utilisateur"),
            Explanation = T("Le HDR élargit la luminosité et les couleurs dans les jeux et vidéos compatibles. MAUS ne l'active ni ne le coupe jamais : " +
                          "c'est un choix de confort, qui peut dégrader l'affichage du bureau sur certains écrans."),
            Advice = color.HdrSupported
                ? T("Réglages HDR, Auto HDR et fréquence variable : {0}. " +
                  "Pour un HDR juste, lancez l'application gratuite « Windows HDR Calibration » (Microsoft Store).", SettingsLinks)
                : null,
        };
    }

    // ----- Connecteur -----

    private static Finding DetectConnector(Screen screen)
    {
        var path = screen.Path;
        var subsampledHdmi = path.Output == OutputTechnology.Hdmi && path.Color is { } color && DisplayParsers.IsChromaSubsampled(color.Encoding);
        var healthy = path.Output is OutputTechnology.DisplayPortExternal or OutputTechnology.DisplayPortUsbTunnel or OutputTechnology.Hdmi
                      || DisplayParsers.IsInternal(path.Output);
        string? advice = null;
        if (subsampledHdmi)
        {
            advice = T("Le DisplayPort (ou un câble HDMI 2.1 certifié Ultra High Speed) offre plus de débit : les couleurs ne seraient plus compressées.");
        }
        else if (path.Output is OutputTechnology.Hd15 or OutputTechnology.Dvi)
        {
            advice = T("Un câble DisplayPort ou HDMI permet des fréquences plus élevées et une image numérique plus nette.");
        }

        return new Finding
        {
            Id = $"M14.connector.{screen.Slug}",
            Title = T("Connecteur : {0}", screen.Label),
            Category = screen.Category,
            Status = healthy && !subsampledHdmi ? FindingStatus.Ok : FindingStatus.Info,
            Current = DisplayParsers.ConnectorLabel(path.Output),
            Expected = T("DisplayPort, ou HDMI avec câble Ultra High Speed"),
            Explanation = T("Débit maximal selon le câble : HDMI 2.0 18 Gbit/s, HDMI 2.1 48 Gbit/s, DisplayPort 1.4 environ 26 Gbit/s utiles, DisplayPort 2.1 jusqu'à 80 Gbit/s. " +
                          "Au-delà, la fréquence baisse ou les couleurs sont compressées."),
            Advice = advice,
        };
    }

    // ----- Résolution -----

    private static Finding? DetectResolution(Screen screen)
    {
        var path = screen.Path;
        if (path.NativeWidth is not { } nativeWidth || path.NativeHeight is not { } nativeHeight || path.Width <= 0)
        {
            return null;
        }

        var native = (path.Width == nativeWidth && path.Height == nativeHeight) || (path.Width == nativeHeight && path.Height == nativeWidth);
        return new Finding
        {
            Id = $"M14.resolution.{screen.Slug}",
            Title = T("Résolution native : {0}", screen.Label),
            Category = screen.Category,
            Status = native ? FindingStatus.Ok : FindingStatus.Info,
            Current = $"{path.Width}×{path.Height}",
            Expected = $"{nativeWidth}×{nativeHeight}",
            Explanation = native
                ? T("L'écran affiche sa résolution native : chaque pixel de l'image correspond à un pixel de la dalle.")
                : T("En dehors de sa résolution native, l'image est étirée et devient floue. Pour agrandir le texte, préférez la mise à l'échelle (100 %, 125 %…)."),
            Advice = native ? null : T("Paramètres > Système > Écran : choisissez la résolution marquée « (recommandé) », puis ajustez la mise à l'échelle."),
        };
    }

    // ----- Rappels et réglages globaux -----

    private static Finding VariableRefreshReminder() => new()
    {
        Id = "M14.vrr",
        Title = T("Fréquence variable (G-SYNC, FreeSync)"),
        Category = GlobalCategory,
        Status = FindingStatus.Info,
        Current = T("non vérifiable par Windows"),
        Expected = T("activée si l'écran la gère"),
        Explanation = T("La fréquence variable synchronise l'écran sur les images produites par le jeu : moins de saccades et pas de déchirure d'image."),
        Advice = T("Vérifiez qu'elle est activée dans le panneau du pilote (NVIDIA : « Configurer G-SYNC » ; AMD : Adrenalin > Affichage > FreeSync) " +
                 "et dans le menu de l'écran (Adaptive-Sync ou FreeSync)."),
    };

    private static Finding DetectWindowsGraphicsSettings(IRegistryReader registry)
    {
        const string id = "M14.windows-graphics-settings";
        var title = T("Auto HDR et optimisations des jeux en fenêtre");
        string? raw;
        try
        {
            raw = registry.GetString(RegistryHive.CurrentUser, DirectXPreferencesKey, "DirectXUserGlobalSettings");
        }
        catch (MausAccessDeniedException)
        {
            return Finding.Unknown(id, title, T("Lecture des préférences graphiques de Windows refusée."), GlobalCategory);
        }

        var settings = DisplayParsers.ParseDirectXSettings(raw);
        var labels = new List<string>();
        AddSetting(labels, settings, "AutoHDREnable", "Auto HDR");
        AddSetting(labels, settings, "SwapEffectUpgradeEnable", T("optimisations des jeux en fenêtre"));
        AddSetting(labels, settings, "VRROptimizeEnable", T("fréquence variable Windows"));

        return new Finding
        {
            Id = id,
            Title = title,
            Category = GlobalCategory,
            Status = FindingStatus.Info,
            Current = labels.Count == 0 ? T("réglages par défaut de Windows") : string.Join(", ", labels),
            Expected = T("au choix de l'utilisateur (défaut Windows)"),
            Explanation = T("Ces options de Windows complètent le HDR et la fréquence variable dans les jeux. MAUS les lit sans jamais les modifier."),
            Advice = T("Pour les changer : {0}.", SettingsLinks),
        };
    }

    private static void AddSetting(List<string> labels, IReadOnlyDictionary<string, string> settings, string key, string label)
    {
        if (settings.TryGetValue(key, out var value))
        {
            labels.Add($"{label} {(value == "0" ? "désactivé" : "activé")}");
        }
    }

    // ----- Outils -----

    private static GpuInfo? FindGpu(HardwareProfile hardware, DisplayPath path)
    {
        var pnp = DisplayParsers.PnpIdFromAdapterPath(path.AdapterDevicePath);
        return hardware.Gpus.FirstOrDefault(g => pnp is not null && string.Equals(g.PnpDeviceId, pnp, StringComparison.OrdinalIgnoreCase))
               ?? hardware.Gpus.FirstOrDefault(g => path.AdapterName is { Length: > 0 } name && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static (int Width, int Height)? Native(DisplayPath path) =>
        path.NativeWidth is { } w && path.NativeHeight is { } h ? (w, h) : DisplayParsers.LargestResolution(path.Modes);

    private static int MaxRefresh(DisplayPath path) => path.Modes.Count == 0 ? 0 : path.Modes.Max(m => m.RefreshHz);

    private static bool IsIndirect(OutputTechnology output) =>
        output is OutputTechnology.IndirectWired or OutputTechnology.IndirectVirtual or OutputTechnology.Miracast;

    private static string Label(DisplayPath path, int index)
    {
        if (!string.IsNullOrWhiteSpace(path.MonitorName))
        {
            return path.MonitorName;
        }

        return DisplayParsers.IsInternal(path.Output) ? T("écran intégré") : T("écran {0}", index);
    }

    private static string Hz(double value) => Math.Round(value, 2).ToString("0.##", French);

    private sealed record Screen(DisplayPath Path, int Index, string Label)
    {
        public string Slug => $"display-{Index}";

        public string Category => T("Écran {0} : {1}", Index, Label);
    }
}
