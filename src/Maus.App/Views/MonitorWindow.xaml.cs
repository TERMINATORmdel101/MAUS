using System.ComponentModel;
using System.Windows;
using Maus.App.Appearance;
using Maus.App.ViewModels.Workshop;

namespace Maus.App.Views;

/// <summary>Fenêtre de surveillance indépendante, placée en haut à droite de l'écran, à garder ouverte pendant un test ou un jeu.</summary>
public partial class MonitorWindow : Window
{
    private readonly MonitorViewModel _model;

    public MonitorWindow(MonitorViewModel model, bool onTop, Action<bool> onTopChanged)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Measures.ItemsSource = model.Groups;
        Topmost = onTop;
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 16);
        Top = area.Top + 16;
        Height = Math.Min(Height, area.Height - 32);

        var topmost = DependencyPropertyDescriptor.FromProperty(TopmostProperty, typeof(Window));
        EventHandler onTopHandler = (_, _) => onTopChanged(Topmost);
        topmost.AddValueChanged(this, onTopHandler);

        // La case des Paramètres agit aussi quand cette fenêtre est déjà ouverte.
        EventHandler onAppearance = (_, _) => Topmost = AppearanceManager.Current.MonitorOnTop;
        AppearanceManager.Changed += onAppearance;
        Loaded += async (_, _) =>
        {
            Motion.Enter(Page, offset: 16);
            try
            {
                await _model.StartAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // La fenêtre reste ouverte : une lecture ratée ne doit pas arrêter MAUS.
                Maus.Core.Diagnostics.CrashLog.Write(ex, "fenêtre de surveillance");
            }
        };
        Closed += (_, _) =>
        {
            topmost.RemoveValueChanged(this, onTopHandler);
            AppearanceManager.Changed -= onAppearance;
            _model.Dispose();
        };
    }
}
