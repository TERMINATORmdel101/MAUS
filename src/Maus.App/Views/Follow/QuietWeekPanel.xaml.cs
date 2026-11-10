using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Maus.App.ViewModels;
using Maus.Core.Diagnostics;

namespace Maus.App.Views.Follow;

/// <summary>Carte « Les audits de la semaine » : la visite ne compte que si la carte est restée à l'écran, fenêtre au premier plan.</summary>
public partial class QuietWeekPanel : UserControl
{
    /// <summary>Haut de la carte qui doit être à l'écran : le titre et le bilan.</summary>
    private const double ReadableTop = 60;

    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _wasOnScreen;

    public QuietWeekPanel()
    {
        InitializeComponent();
        _watch.Tick += OnWatch;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _watch.Stop();
    }

    /// <summary>La carte est-elle assez à l'écran pour être lue ? Mesures en pixels, relatives à la zone qui défile.</summary>
    internal static bool IsReadable(double top, double height, double viewport) =>
        height > 0 && top + height > 0 && top >= -height + ReadableTop && top + Math.Min(height, ReadableTop) <= viewport;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel view)
        {
            view.ShowQuietWeekAsync().Forget("suivi : audits de la semaine");
            _wasOnScreen = false;
            _watch.Start();
        }
    }

    /// <summary>Deux relevés de suite, à une seconde d'écart : passer sur l'onglet sans s'arrêter n'est pas une visite.</summary>
    private void OnWatch(object? sender, EventArgs e)
    {
        var onScreen = IsOnScreen();
        if (onScreen && _wasOnScreen && DataContext is MainViewModel { IsQuietWeekReady: true } view)
        {
            _watch.Stop();
            view.MarkQuietWeekSeenAsync().Forget("suivi : date de la visite");
        }

        _wasOnScreen = onScreen;
    }

    private bool IsOnScreen()
    {
        if (!IsVisible || Window.GetWindow(this) is not { IsActive: true, WindowState: not WindowState.Minimized })
        {
            return false;
        }

        DependencyObject? parent = this;
        while (parent is not (null or ScrollViewer))
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        return parent is not ScrollViewer scroller
            || IsReadable(TransformToAncestor(scroller).Transform(default).Y, ActualHeight, scroller.ViewportHeight);
    }

    /// <summary>Mène au réglage, au bas de « Corrections », et le montre : il est sinon à plusieurs écrans de là.</summary>
    private void OnOpenSetting(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel view || Window.GetWindow(this) is not { } window)
        {
            return;
        }

        view.SelectedTab = MainViewModel.TabFixes;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindSetting(window, view) is { } setting)
            {
                setting.BringIntoView();
                setting.Focus();
            }
        });
    }

    private static ComboBox? FindSetting(DependencyObject root, MainViewModel view)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox box && ReferenceEquals(box.ItemsSource, view.ScheduleOptions))
            {
                return box;
            }

            if (FindSetting(child, view) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
