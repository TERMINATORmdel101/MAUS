using System.Windows;
using System.Windows.Controls;

namespace Maus.App.Controls;

/// <summary>
/// Zone de défilement d'une page. La barre du thème Fluent se pose par-dessus le contenu : tant qu'elle se voit, le contenu lui
/// laisse sa place. La zone ne prend pas le focus : les boutons de la page le prennent, et un cadre autour de toute la page ne dit rien.
/// </summary>
public static class PageScroll
{
    /// <summary>Place laissée à la barre, assez pour sa forme large quand la souris passe dessus.</summary>
    internal const double Gutter = 14;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PageScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ScrollViewer viewer)
        {
            return;
        }

        viewer.ScrollChanged -= OnScrollChanged;
        if ((bool)e.NewValue)
        {
            viewer.Focusable = false;
            viewer.ScrollChanged += OnScrollChanged;
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is ScrollViewer { Content: FrameworkElement content } viewer)
        {
            var right = viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible ? Gutter : 0;
            var margin = content.Margin;
            if (margin.Right != right)
            {
                content.Margin = new Thickness(margin.Left, margin.Top, right, margin.Bottom);
            }
        }
    }
}
