using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>
/// Liste à défilement placée dans une page qui défile : arrivée en butée, la molette passe à la page. Sans cela, la liste garde
/// la molette même quand elle ne peut plus bouger, et la page ne défile plus.
/// </summary>
public static class WheelChain
{
    public static readonly DependencyProperty PassAtEdgeProperty = DependencyProperty.RegisterAttached(
        "PassAtEdge", typeof(bool), typeof(WheelChain), new PropertyMetadata(false, OnPassAtEdgeChanged));

    public static bool GetPassAtEdge(DependencyObject element) => (bool)element.GetValue(PassAtEdgeProperty);

    public static void SetPassAtEdge(DependencyObject element, bool value) => element.SetValue(PassAtEdgeProperty, value);

    /// <summary>La molette ne peut plus rien faire bouger dans cette direction (<paramref name="delta"/> positif : vers le haut).</summary>
    internal static bool IsAtEdge(double offset, double scrollable, int delta) => delta > 0 ? offset <= 0 : offset >= scrollable;

    private static void OnPassAtEdgeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is UIElement target)
        {
            target.PreviewMouseWheel -= OnPreviewMouseWheel;
            if ((bool)e.NewValue)
            {
                target.PreviewMouseWheel += OnPreviewMouseWheel;
            }
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element || FindScroller(element) is not { } scroller
            || !IsAtEdge(scroller.VerticalOffset, scroller.ScrollableHeight, e.Delta)
            || VisualTreeHelper.GetParent(element) is not UIElement parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
