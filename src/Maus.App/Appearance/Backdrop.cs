using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Maus.Core.Platform;

namespace Maus.App.Appearance;

/// <summary>
/// Fond Mica de la fenêtre principale (0.8.4), au choix de l'utilisateur (Paramètres). Il n'est posé que si Windows le
/// permet : Windows 11 22H2 ou plus, transparence activée dans Windows (<c>UISettings.AdvancedEffectsEnabled</c>, Microsoft
/// Learn), pas de contraste élevé, rendu par la carte graphique. Sinon la fenêtre garde son fond plein habituel.
/// </summary>
internal static class Backdrop
{
    /// <summary>Suit la fenêtre : posé à sa création, revu à chaque changement d'apparence, oublié à sa fermeture.</summary>
    public static void Attach(Window window)
    {
        Color? original = null;
        var transparent = false;
        void Update()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0 || HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target)
            {
                return;
            }

            original ??= target.BackgroundColor;
            var wanted = Wanted();
            if (!wanted && !transparent)
            {
                // Jamais posé : on ne touche à rien (le fond vient du thème de Windows).
                return;
            }

            var applied = WindowBackdrop.Apply(handle, wanted, AppearanceManager.IsDark) && wanted;
            if (applied)
            {
                window.Background = Brushes.Transparent;
                target.BackgroundColor = Colors.Transparent;
                transparent = true;
            }
            else
            {
                // Retour au fond plein du thème (clé de ressource de WPF Fluent, suivie en cas de changement de thème).
                window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                target.BackgroundColor = original.Value;
                transparent = false;
            }
        }

        void OnChanged(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(Update);
        // Le thème Fluent de WPF repose le fond de la fenêtre en la chargeant : Mica est donc revu après le chargement.
        window.SourceInitialized += (_, _) => Update();
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Update);
        AppearanceManager.Changed += OnChanged;
        window.Closed += (_, _) => AppearanceManager.Changed -= OnChanged;
    }

    private static bool Wanted() =>
        AppearanceManager.Current.MicaBackdrop
        && WindowBackdrop.IsSupported
        && !SystemParameters.HighContrast
        && RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly
        && (RenderCapability.Tier >> 16) >= 2
        && TransparencyEnabled();

    /// <summary>Réglage « Effets de transparence » de Windows, lu par l'API de Windows (jamais modifié).</summary>
    private static bool TransparencyEnabled()
    {
        try
        {
            return new global::Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
