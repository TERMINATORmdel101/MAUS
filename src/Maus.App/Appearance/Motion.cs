using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Maus.App.Appearance;

/// <summary>
/// Animations discrètes de l'interface : changement de page, apparition des cartes, fenêtres qui s'ouvrent, jauges.
/// Toutes passent par ici et s'arrêtent d'un coup si l'utilisateur les coupe (paramètres, ou « Effets d'animation » de Windows).
/// </summary>
public static class Motion
{
    /// <summary>Durée d'une transition de page.</summary>
    public static readonly Duration PageDuration = new(TimeSpan.FromMilliseconds(260));

    /// <summary>Décalage entre l'apparition de deux cartes successives.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(40);

    private const int MaxStaggered = 8;

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Animations autorisées (réglé par <see cref="AppearanceManager"/>).</summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>Anime le contenu d'un <see cref="TabControl"/> quand l'onglet choisi change.</summary>
    public static readonly DependencyProperty AnimateSelectionProperty = DependencyProperty.RegisterAttached(
        "AnimateSelection", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateSelectionChanged));

    public static bool GetAnimateSelection(DependencyObject element) => (bool)element.GetValue(AnimateSelectionProperty);

    public static void SetAnimateSelection(DependencyObject element, bool value) => element.SetValue(AnimateSelectionProperty, value);

    /// <summary>Fait apparaître un élément : fondu et léger glissement vers le haut.</summary>
    public static void Enter(UIElement element, TimeSpan delay = default, double offset = 12)
    {
        // L'animation part de la valeur actuelle (un texte « discret » garde son opacité réduite) et s'efface à la fin :
        // aucune valeur locale n'est laissée sur l'élément.
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!IsEnabled)
        {
            return;
        }

        var opacity = element.Opacity;
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(delay + PageDuration.TimeSpan), Ease));
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        // Glissement seulement si l'élément n'a pas déjà sa propre transformation.
        var translate = element.RenderTransform as TranslateTransform;
        if (translate is null && element.RenderTransform is { } existing && existing != Transform.Identity)
        {
            return;
        }

        if (translate is null || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(offset, KeyTime.FromTimeSpan(delay)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + PageDuration.TimeSpan), Ease));
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Anime une valeur numérique d'un élément (par exemple la jauge du score) vers <paramref name="to"/>.</summary>
    public static void To(IAnimatable element, DependencyProperty property, double to, TimeSpan duration)
    {
        if (!IsEnabled)
        {
            element.BeginAnimation(property, null);
            if (element is DependencyObject target)
            {
                target.SetValue(property, to);
            }

            return;
        }

        element.BeginAnimation(property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = Ease });
    }

    private static void OnAnimateSelectionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TabControl tabs)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            tabs.SelectionChanged += OnSelectionChanged;
        }
        else
        {
            tabs.SelectionChanged -= OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Les listes et menus déroulants de la page remontent aussi cet événement : seul l'onglet compte.
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl tabs || !IsEnabled || !tabs.IsLoaded)
        {
            return;
        }

        tabs.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is not ContentPresenter host)
            {
                return;
            }

            Enter(host, offset: 10);
            var index = 0;
            foreach (var card in Cards(host))
            {
                Enter(card, Stagger * (index + 1), 14);
                if (++index >= MaxStaggered)
                {
                    break;
                }
            }
        });
    }

    /// <summary>Les premiers éléments de la page (cartes), sous un éventuel ScrollViewer et son StackPanel.</summary>
    private static IEnumerable<UIElement> Cards(ContentPresenter host)
    {
        var content = host.Content as DependencyObject;
        if (content is ScrollViewer { Content: DependencyObject inner })
        {
            content = inner;
        }

        return content is Panel panel ? panel.Children.OfType<UIElement>().Where(c => c.Visibility == Visibility.Visible) : [];
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
