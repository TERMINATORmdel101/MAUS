using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Maus.App.Appearance;
using static Maus.Core.Localization.Texts;

namespace Maus.App;

public partial class MainWindow : Window
{
    private const double ZoomStep = 0.05;

    private readonly ScaleTransform _zoom = new(1, 1);

    private void OnBenchmarkDetail(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind } && DataContext is ViewModels.MainViewModel { Workshop: { } workshop } && workshop.BenchmarkDetailOf(kind) is { } detail)
        {
            BenchmarkOverlay.ShowDetail(detail);
        }
    }

    private void OnBenchmarkPicture(object sender, RoutedEventArgs e) =>
        BenchmarkOverlay.ShowPicture((DataContext as ViewModels.MainViewModel)?.Workshop?.BenchmarkFullImage() ?? BenchmarkPicture.Source);

    public MainWindow()
    {
        InitializeComponent();
        ((FrameworkElement)Content).LayoutTransform = _zoom;
        ShowZoom();
        AppearanceManager.Changed += OnAppearanceChanged;
        Closed += (_, _) =>
        {
            AppearanceManager.Changed -= OnAppearanceChanged;
            (DataContext as ViewModels.MainViewModel)?.Shutdown();
        };
        SourceInitialized += (_, _) => RestorePlacement();
        Loaded += (_, _) => (DataContext as ViewModels.MainViewModel)?.RestoreView(Maus.App.Appearance.AppearanceManager.Current);
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => ShowZoom();

    private void ShowZoom() => _zoom.ScaleX = _zoom.ScaleY = AppearanceManager.Current.UiZoomUsed;

    /// <summary>Ctrl + molette : plus grand ou plus petit (enregistré à la fermeture, avec la place de la fenêtre).</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPreviewMouseWheel(e);
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetZoom(AppearanceManager.Current.UiZoomUsed + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            e.Handled = true;
        }
    }

    /// <summary>Ctrl + 0 : taille normale.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            SetZoom(1);
            e.Handled = true;
        }
    }

    private void SetZoom(double zoom)
    {
        AppearanceManager.SetZoom(Math.Round(zoom / ZoomStep) * ZoomStep);
        ShowZoom();
    }

    /// <summary>Remet la fenêtre à sa dernière place, si elle reste visible (écran débranché, fichier modifié à la main).</summary>
    private void RestorePlacement()
    {
        // Première ouverture sur un petit écran : la barre de titre ne doit pas sortir par le haut.
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        if (Maus.App.Appearance.AppearanceManager.Current.MainWindow is not { } saved
            || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height))
        {
            return;
        }

        var inside = saved.Left >= SystemParameters.VirtualScreenLeft
            && saved.Top >= SystemParameters.VirtualScreenTop
            && saved.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && saved.Top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        // Le rectangle englobant a des trous quand les écrans sont de tailles différentes : le titre doit être sur un vrai écran.
        var scale = VisualTreeHelper.GetDpi(this);
        if (inside && Maus.Core.Platform.ScreenLocator.IsOnAScreen((int)((saved.Left + 100) * scale.DpiScaleX), (int)((saved.Top + 16) * scale.DpiScaleY)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
        }

        Width = Maus.Core.Preferences.WindowPlacement.FitSize(saved.Width, Math.Max(MinWidth, 640), SystemParameters.VirtualScreenWidth);
        Height = Maus.Core.Preferences.WindowPlacement.FitSize(saved.Height, Math.Max(MinHeight, 480), SystemParameters.VirtualScreenHeight);
        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Fermer pendant des corrections couperait une écriture en cours : on demande d'abord. Pas de refus pur et simple, une
    /// écriture bloquée ne doit pas rendre MAUS impossible à fermer ; l'écriture interrompue reste annulable dans l'Historique.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (DataContext is ViewModels.MainViewModel { IsWriting: true }
            && !Views.QuestionWindow.Ask(T("Fermer MAUS ?"), T("Des corrections sont en cours d'écriture. Fermer maintenant peut laisser une correction à moitié faite ; elle restera annulable dans l'Historique au prochain lancement.") + Environment.NewLine + Environment.NewLine + T("Fermer quand même ?"), Views.QuestionTone.Warning))
        {
            e.Cancel = true;
        }

        if (!e.Cancel && Views.ToolWindow.IsRunning
            && !Views.QuestionWindow.Ask(T("Fermer MAUS ?"), T("Un outil de Windows travaille encore (réparation ou pilote). Si vous fermez MAUS, la suite ne sera pas lancée et son résultat ne s'affichera plus. Fermer quand même ?"), Views.QuestionTone.Warning))
        {
            e.Cancel = true;
        }

        if (!e.Cancel && DataContext is ViewModels.MainViewModel point && (point.IsCreatingRestorePoint || point.Workshop.IsCreatingRestorePoint)
            && !Views.QuestionWindow.Ask(T("Fermer MAUS ?"), T("Un point de restauration est en cours de création. Si vous fermez MAUS, vous ne saurez pas s'il a réussi. Fermer quand même ?"), Views.QuestionTone.Warning))
        {
            e.Cancel = true;
        }

        if (!e.Cancel && DataContext is ViewModels.MainViewModel view)
        {
            view.RememberView(CurrentPlacement());
        }

        base.OnClosing(e);
    }

    /// <summary>Place et taille actuelles de la fenêtre.</summary>
    internal Maus.Core.Preferences.WindowPlacement CurrentPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return new Maus.Core.Preferences.WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }
}
