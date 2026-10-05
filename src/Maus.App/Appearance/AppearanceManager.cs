using System.Windows;
using System.Windows.Media;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;
using Microsoft.Win32;

namespace Maus.App.Appearance;

/// <summary>Déclinaison d'une couleur d'accentuation, sur le modèle des clés SystemAccentColor* de WPF.</summary>
internal enum AccentShade
{
    Base,
    Light1,
    Light2,
    Light3,
    Dark1,
    Dark2,
    Dark3,
}

/// <summary>
/// Thème (clair, sombre ou comme Windows), couleur d'accentuation et animations, appliqués à toutes les fenêtres et
/// modifiables à chaud depuis les paramètres. Les couleurs d'état des constats (vert, bleu, or, rouge) ne changent jamais.
/// </summary>
public static class AppearanceManager
{
    /// <summary>
    /// Pinceaux du thème Fluent de WPF (.NET 10) qui dérivent de la couleur d'accentuation de Windows : clé, déclinaison et
    /// opacité en thème clair, puis en thème sombre. Relevés dans dotnet/wpf, branche release/10.0,
    /// Themes/PresentationFramework.Fluent/Resources/Theme/Light.xaml et Dark.xaml.
    /// </summary>
    private static readonly (string Key, AccentShade Light, double LightOpacity, AccentShade Dark, double DarkOpacity)[] FluentAccentBrushes =
    [
        ("AccentButtonBackground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("AccentButtonBackgroundPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("AccentButtonBackgroundPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("AccentFillColorDefaultBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("AccentFillColorSecondaryBrush", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("AccentFillColorSelectedTextBackgroundBrush", AccentShade.Base, 1, AccentShade.Base, 1),
        ("AccentFillColorTertiaryBrush", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("AccentTextFillColorPrimaryBrush", AccentShade.Dark2, 1, AccentShade.Light3, 1),
        ("AccentTextFillColorSecondaryBrush", AccentShade.Dark3, 1, AccentShade.Light3, 1),
        ("AccentTextFillColorTertiaryBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CalendarViewSelectedBackground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CalendarViewSelectedBorderBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CalendarViewTodayBackground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CheckBoxCheckBackgroundFillChecked", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("CheckBoxCheckBackgroundFillCheckedPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("CheckBoxCheckBackgroundFillIndeterminate", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CheckBoxCheckBackgroundFillIndeterminatePointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("CheckBoxCheckBackgroundFillIndeterminatePressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("CheckBoxCheckBackgroundStrokeChecked", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("CheckBoxCheckBackgroundStrokeCheckedPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("CheckBoxCheckBackgroundStrokeIndeterminate", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("CheckBoxCheckBackgroundStrokeIndeterminatePressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("ComboBoxBorderBrushFocused", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ComboBoxItemPillFillBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("DataGridRowSelectedBackgroundThemeBrush", AccentShade.Dark1, 1, AccentShade.Light3, 1),
        ("DatePickerBorderBrushFocused", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("DatePickerFocusedBorderBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("HyperlinkButtonForeground", AccentShade.Dark2, 1, AccentShade.Light3, 1),
        ("HyperlinkButtonForegroundPointerOver", AccentShade.Dark3, 0.9, AccentShade.Light3, 0.9),
        ("HyperlinkButtonForegroundPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("HyperlinkForeground", AccentShade.Dark2, 1, AccentShade.Light3, 1),
        ("HyperlinkForegroundPointerOver", AccentShade.Dark2, 1, AccentShade.Light3, 1),
        ("InfoBarInformationalSeverityIconBackground", AccentShade.Base, 1, AccentShade.Base, 1),
        ("ListBoxItemBackgroundSelectedPressedThemeBrush", AccentShade.Base, 0.7, AccentShade.Base, 0.9),
        ("ListBoxItemSelectedBackgroundPointerOverThemeBrush", AccentShade.Base, 0.6, AccentShade.Base, 0.8),
        ("ListBoxItemSelectedBackgroundPressedThemeBrush", AccentShade.Base, 0.7, AccentShade.Base, 0.9),
        ("ListBoxItemSelectedBackgroundThemeBrush", AccentShade.Base, 0.4, AccentShade.Base, 0.6),
        ("ListViewItemPillFillBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("NavigationViewSelectionIndicatorForeground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ProgressBarForeground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ProgressRingForegroundThemeBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("RadioButtonCheckOuterEllipseCheckedFillPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("RadioButtonCheckOuterEllipseCheckedFillPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("RadioButtonCheckOuterEllipseCheckedStrokePressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("RadioButtonOuterEllipseCheckedFill", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("RadioButtonOuterEllipseCheckedStroke", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("RadioButtonOuterEllipseCheckedStrokePointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("RatingControlSelectedForeground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("SliderThumbBackground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("SliderThumbBackgroundPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("SystemFillColorAttentionBrush", AccentShade.Base, 1, AccentShade.Base, 1),
        ("TextControlFocusedBorderBrush", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("TextControlSelectionHighlightColor", AccentShade.Base, 1, AccentShade.Base, 1),
        ("ThumbRateForeground", AccentShade.Dark1, 1, AccentShade.Light3, 1),
        ("ToggleButtonBackgroundChecked", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ToggleButtonBackgroundCheckedPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("ToggleButtonBackgroundCheckedPressed", AccentShade.Dark1, 0.8, AccentShade.Light2, 0.8),
        ("ToggleSwitchFillOn", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ToggleSwitchFillOnPointerOver", AccentShade.Dark1, 0.9, AccentShade.Light2, 0.9),
        ("ToggleSwitchStrokeOn", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("ToggleSwitchStrokeOnPointerOver", AccentShade.Dark1, 1, AccentShade.Light2, 1),
        ("TreeViewItemSelectionIndicatorForeground", AccentShade.Dark1, 1, AccentShade.Light2, 1),
    ];

    private static readonly (object Key, AccentShade Shade)[] FluentAccentColors =
    [
        ("SystemAccentColor", AccentShade.Base),
        ("SystemAccentColorLight1", AccentShade.Light1),
        ("SystemAccentColorLight2", AccentShade.Light2),
        ("SystemAccentColorLight3", AccentShade.Light3),
        ("SystemAccentColorDark1", AccentShade.Dark1),
        ("SystemAccentColorDark2", AccentShade.Dark2),
        ("SystemAccentColorDark3", AccentShade.Dark3),
        (SystemColors.AccentColorKey, AccentShade.Base),
        (SystemColors.AccentColorLight1Key, AccentShade.Light1),
        (SystemColors.AccentColorLight2Key, AccentShade.Light2),
        (SystemColors.AccentColorLight3Key, AccentShade.Light3),
        (SystemColors.AccentColorDark1Key, AccentShade.Dark1),
        (SystemColors.AccentColorDark2Key, AccentShade.Dark2),
        (SystemColors.AccentColorDark3Key, AccentShade.Dark3),
    ];

    private static bool s_listening;

    /// <summary>Choix d'affichage en vigueur.</summary>
    public static UserPreferences Current { get; private set; } = UserPreferences.Default;

    /// <summary>Marques du processeur et de la carte graphique, lues une fois (CPUID et registre, sans pilote).</summary>
    public static ComponentBrands Brands { get; private set; } = ComponentBrands.Unknown;

    /// <summary>Thème sombre réellement affiché.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Changement de thème, de couleur, d'animations ou de fréquence des mesures.</summary>
    public static event EventHandler? Changed;

    /// <summary>Lit les marques des composants ; à appeler une fois, au démarrage.</summary>
    public static void DetectComponents()
    {
        var cpu = ComponentBrand.Other;
        var graphics = ComponentBrand.Other;
        try
        {
            cpu = ComponentBrands.FromCpuVendor(CpuIdParser.Read(new X86CpuIdSource())?.Vendor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Processeur non identifié : teinte MAUS.
        }

        try
        {
            graphics = GraphicsBrand.Read(new WindowsRegistryReader());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Carte graphique non identifiée : teinte du processeur seule.
        }

        Brands = new ComponentBrands(cpu, graphics);
    }

    /// <summary>Applique les choix à toute l'application (à chaud).</summary>
    public static void Apply(UserPreferences preferences)
    {
        Current = preferences;
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        IsDark = preferences.Theme switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => !SystemUsesLightTheme(),
        };

#pragma warning disable WPF0001
        var mode = preferences.Theme switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (application.ThemeMode != mode)
        {
            application.ThemeMode = mode;
        }
#pragma warning restore WPF0001

        ApplyAccent(application.Resources, preferences.Accent, IsDark);
        Pastel.Apply(application.Resources, IsDark, SystemParameters.HighContrast);

        // Thème sombre : tous les textes en blanc plein (demande du porteur) ; thème clair : textes secondaires atténués.
        application.Resources["MutedOpacity"] = IsDark ? 1.0 : 0.72;
        Motion.IsEnabled = preferences.Animations switch
        {
            AnimationChoice.On => true,
            AnimationChoice.Off => false,
            _ => SystemParameters.ClientAreaAnimation,
        };

        if (!s_listening)
        {
            // Windows passe en clair ou en sombre, change sa couleur ou ses animations : on suit.
            s_listening = true;
            SystemEvents.UserPreferenceChanged += (_, _) => application.Dispatcher.BeginInvoke(() => Apply(Current));
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Teinte principale et secondaire d'un choix, pour l'aperçu dans les paramètres.</summary>
    public static (Color Primary, Color? Secondary) Colors(AccentChoice choice)
    {
        var (primary, secondary) = AccentPalette.For(choice, Brands, WindowsAccent());
        return (ToColor(primary), secondary is { } s ? ToColor(s) : null);
    }

    private static void ApplyAccent(ResourceDictionary resources, AccentChoice choice, bool dark)
    {
        // Contraste élevé : Windows impose ses couleurs, MAUS n'en remplace aucune.
        var highContrast = SystemParameters.HighContrast;
        if (highContrast)
        {
            choice = AccentChoice.Windows;
        }

        var (primary, secondary) = AccentPalette.For(choice, Brands, WindowsAccent());
        var variants = AccentVariants.From(primary);
        resources["MausAccent"] = Frozen(new SolidColorBrush(ToColor(dark ? variants.Light1 : primary)));
        resources["MausAccentStrong"] = Frozen(new SolidColorBrush(ToColor(variants.OnWhiteText)));
        resources["MausAccentSoft"] = Frozen(new SolidColorBrush(WithAlpha(ToColor(primary), 0x26)));
        resources["MausSidebarTint"] = highContrast
            ? Brushes.Transparent
            : Frozen(new LinearGradientBrush(
                WithAlpha(ToColor(primary), dark ? (byte)0x33 : (byte)0x1F),
                WithAlpha(ToColor(secondary ?? primary), dark ? (byte)0x33 : (byte)0x1F),
                90));

        if (choice == AccentChoice.Windows)
        {
            // Couleur de Windows : le thème Fluent la gère lui-même.
            foreach (var (key, _, _, _, _) in FluentAccentBrushes)
            {
                resources.Remove(key);
            }

            foreach (var (key, _) in FluentAccentColors)
            {
                resources.Remove(key);
            }

            return;
        }

        foreach (var (key, shade) in FluentAccentColors)
        {
            resources[key] = ToColor(Shade(variants, shade));
        }

        foreach (var (key, light, lightOpacity, darkShade, darkOpacity) in FluentAccentBrushes)
        {
            var brush = new SolidColorBrush(ToColor(Shade(variants, dark ? darkShade : light))) { Opacity = dark ? darkOpacity : lightOpacity };
            resources[key] = Frozen(brush);
        }
    }

    private static Rgb Shade(AccentVariants variants, AccentShade shade) => shade switch
    {
        AccentShade.Light1 => variants.Light1,
        AccentShade.Light2 => variants.Light2,
        AccentShade.Light3 => variants.Light3,
        AccentShade.Dark1 => variants.Dark1,
        AccentShade.Dark2 => variants.Dark2,
        AccentShade.Dark3 => variants.Dark3,
        _ => variants.Base,
    };

    /// <summary>Couleur d'accentuation de Windows (WPF .NET 9 et suivants), ou <c>null</c> si elle est illisible.</summary>
    private static Rgb? WindowsAccent()
    {
        var color = SystemColors.AccentColor;
        return color.A == 0 ? null : new Rgb(color.R, color.G, color.B);
    }

    /// <summary>Réglage « Mode d'application par défaut » de Windows (valeur AppsUseLightTheme) ; clair s'il est illisible.</summary>
    private static bool SystemUsesLightTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    private static Color ToColor(Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
