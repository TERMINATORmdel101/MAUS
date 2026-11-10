using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Maus.App.Appearance;

/// <summary>
/// Animations discrètes de l'interface : changement de page, apparition des cartes, fenêtres qui s'ouvrent, jauges.
/// Toutes passent par ici et s'arrêtent d'un coup si l'utilisateur les coupe (paramètres, ou « Effets d'animation » de Windows).
/// </summary>
public static partial class Motion
{
    /// <summary>Durée d'une transition de page.</summary>
    public static readonly Duration PageDuration = new(TimeSpan.FromMilliseconds(220));

    /// <summary>Décalage entre l'apparition de deux cartes successives.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(45);

    private const int MaxStaggered = 8;

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Animations autorisées (réglé par <see cref="AppearanceManager"/>).</summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>Anime le contenu d'un <see cref="TabControl"/> quand l'onglet choisi change.</summary>
    /// <summary>Quand la valeur passe à vrai, la page défile jusqu'à cet élément (carte choisie depuis un autre onglet).</summary>
    public static readonly DependencyProperty BringIntoViewWhenProperty = DependencyProperty.RegisterAttached(
        "BringIntoViewWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnBringIntoViewWhenChanged));

    public static bool GetBringIntoViewWhen(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(BringIntoViewWhenProperty);
    }

    public static void SetBringIntoViewWhen(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BringIntoViewWhenProperty, value);
    }

    private static void OnBringIntoViewWhenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && element is FrameworkElement target)
        {
            // Une fois l'onglet ouvert et la page mise en place : avant, l'élément n'a pas encore de position.
            target.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(target.BringIntoView));
        }
    }

    public static readonly DependencyProperty AnimateSelectionProperty = DependencyProperty.RegisterAttached(
        "AnimateSelection", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateSelectionChanged));

    public static bool GetAnimateSelection(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(AnimateSelectionProperty);
    }

    public static void SetAnimateSelection(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AnimateSelectionProperty, value);
    }

    /// <summary>Fait apparaître un élément : fondu et léger glissement vers le haut.</summary>
    public static void Enter(UIElement element, TimeSpan delay = default, double offset = 12)
    {
        ArgumentNullException.ThrowIfNull(element);

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
        if (Parts(element) is not { } parts)
        {
            return;
        }

        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(offset, KeyTime.FromTimeSpan(delay)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + PageDuration.TimeSpan), Ease));

        // Le cadre de focus s'est placé au début du glissement, et une transformation ne le déplace pas : il se replace à la fin.
        slide.Completed += (_, _) => AdornerLayer.GetAdornerLayer(element)?.Update();
        parts.Translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Élément qui ne s'affiche que dans un certain état : apparaît quand la valeur liée passe à vrai, pas à chaque retour sur
    /// la page. Les enfants d'un panneau apparaissent l'un après l'autre.
    /// </summary>
    public static readonly DependencyProperty EnterWhenProperty = DependencyProperty.RegisterAttached(
        "EnterWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterWhenChanged));

    public static bool GetEnterWhen(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnterWhenProperty);
    }

    public static void SetEnterWhen(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnterWhenProperty, value);
    }

    private static void OnEnterWhenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            return;
        }

        if (element is Panel panel)
        {
            for (var i = 0; i < panel.Children.Count; i++)
            {
                Enter(panel.Children[i], TimeSpan.FromMilliseconds(120 * i), offset: 6);
            }
        }
        else if (element is UIElement target)
        {
            Enter(target, offset: 6);
        }
    }

    /// <summary>Toutes les zones repliables s'ouvrent en douceur au lieu de sauter.</summary>
    internal static void WatchExpanders() =>
        EventManager.RegisterClassHandler(typeof(Expander), Expander.ExpandedEvent, new RoutedEventHandler(OnExpanded));

    private static void OnExpanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Expander { Content: UIElement content } expander)
        {
            // Depuis le bas : le contenu qui arrive ne passe jamais sur la barre de la catégorie.
            Enter(content, offset: 8);
            if (IsEnabled)
            {
                expander.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Cascade(content));
            }
        }
    }

    /// <summary>Changement de thème : une image de la fenêtre telle qu'elle était s'efface au-dessus de la nouvelle apparence.</summary>
    internal static void CrossFade(Window window)
    {
        if (!IsEnabled || !window.IsVisible || window.Content is not FrameworkElement { ActualWidth: >= 1, ActualHeight: >= 1 } content
            || AdornerLayer.GetAdornerLayer(content) is not { } layer)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(content);
        var image = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(content);
        image.Freeze();

        var veil = new SnapshotAdorner(content, image, window.Background?.CloneCurrentValue());
        layer.Add(veil);
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(220))) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => layer.Remove(veil);
        veil.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private sealed class SnapshotAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly Brush? _background;

        public SnapshotAdorner(UIElement adorned, ImageSource image, Brush? background)
            : base(adorned)
        {
            _image = image;
            _background = background;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var area = new Rect(AdornedElement.RenderSize);
            drawingContext.DrawRectangle(_background, null, area);
            drawingContext.DrawImage(_image, area);
        }
    }

    /// <summary>Carte cliquable : se soulève légèrement au survol de la souris.</summary>
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(HoverLiftProperty);
    }

    public static void SetHoverLift(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverLiftProperty, value);
    }

    /// <summary>Bouton : s'enfonce légèrement pendant l'appui.</summary>
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressChanged));

    public static bool GetPress(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(PressProperty);
    }

    public static void SetPress(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PressProperty, value);
    }

    /// <summary>Élément d'une liste (fiche, constat) : apparaît en cascade quand il s'affiche, les premiers seulement.</summary>
    public static readonly DependencyProperty EnterOnLoadProperty = DependencyProperty.RegisterAttached(
        "EnterOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterOnLoadChanged));

    public static bool GetEnterOnLoad(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnterOnLoadProperty);
    }

    public static void SetEnterOnLoad(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnterOnLoadProperty, value);
    }

    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(160));

    private const double Lift = -3;

    private const double Pressed = 0.97;

    private static void OnHoverLiftChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.MouseEnter -= OnLiftEnter;
        target.MouseLeave -= OnLiftLeave;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnLiftEnter;
            target.MouseLeave += OnLiftLeave;
        }
    }

    private static void OnLiftEnter(object sender, MouseEventArgs e) => Glide((UIElement)sender, Lift);

    private static void OnLiftLeave(object sender, MouseEventArgs e) => Glide((UIElement)sender, 0);

    /// <summary>Déplace l'élément verticalement jusqu'à <paramref name="to"/> (0 = position normale).</summary>
    private static void Glide(UIElement element, double to)
    {
        if (Parts(element) is not { } parts)
        {
            return;
        }

        parts.Translate.BeginAnimation(TranslateTransform.YProperty, IsEnabled && (to == 0 || element.IsEnabled)
            ? new DoubleAnimation(to, HoverDuration) { EasingFunction = Ease }
            : null);
    }

    private static void OnPressChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.PreviewMouseLeftButtonDown -= OnPressDown;
        target.PreviewMouseLeftButtonUp -= OnPressUp;
        target.MouseLeave -= OnPressUp;
        target.LostMouseCapture -= OnPressUp;
        if ((bool)e.NewValue)
        {
            target.PreviewMouseLeftButtonDown += OnPressDown;
            target.PreviewMouseLeftButtonUp += OnPressUp;
            target.MouseLeave += OnPressUp;
            target.LostMouseCapture += OnPressUp;
        }
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e) => Squeeze((UIElement)sender, Pressed, 90);

    private static void OnPressUp(object sender, MouseEventArgs e)
    {
        var element = (UIElement)sender;
        Squeeze(element, GetHoverGrow(element) && element.IsMouseOver ? Grown(element) : 1, 160);
    }

    private static void Squeeze(UIElement element, double to, int milliseconds)
    {
        if (Parts(element) is not { } parts)
        {
            return;
        }

        if (!IsEnabled || !element.IsEnabled)
        {
            parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        var animation = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(milliseconds))) { EasingFunction = Ease };
        parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void OnEnterOnLoadChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement target)
        {
            return;
        }

        target.Loaded -= OnItemLoaded;
        if ((bool)e.NewValue)
        {
            target.Loaded += OnItemLoaded;
        }
    }

    private static void OnItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !IsEnabled)
        {
            return;
        }

        // Plus bas que la partie visible : il apparaîtra quand le défilement l'atteindra.
        if (ScrollOwner(element) is { } scroll && IsBelow(element, scroll))
        {
            Hold(element, scroll);
        }
        else if (ItemIndex(element) is { } index)
        {
            EnterItem(element, Math.Min(index, MaxStaggered));
        }
    }

    /// <summary>Rang de l'élément dans sa liste (celle dont il est le conteneur ou le contenu), ou <c>null</c>.</summary>
    private static int? ItemIndex(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ItemsControl.ItemsControlFromItemContainer(current) is { } owner)
            {
                var index = owner.ItemContainerGenerator.IndexFromContainer(current);
                return index >= 0 ? index : null;
            }
        }

        return null;
    }

    private sealed record TransformParts(ScaleTransform Scale, TranslateTransform Translate);

    /// <summary>Déplacement nul (hors animation en cours) : on peut le remplacer sans rien décaler.</summary>
    private static bool AtRest(TranslateTransform transform) => transform.IsFrozen
        ? transform.X == 0 && transform.Y == 0
        : (double)transform.GetAnimationBaseValue(TranslateTransform.XProperty) == 0 && (double)transform.GetAnimationBaseValue(TranslateTransform.YProperty) == 0;

    /// <summary>
    /// Agrandissement et déplacement de l'élément, posés une fois pour toutes (groupe « échelle puis déplacement »).
    /// <c>null</c> si l'élément a déjà sa propre transformation, que les animations ne doivent pas écraser.
    /// </summary>
    private static TransformParts? Parts(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TransformGroup { IsFrozen: false, Children: [ScaleTransform scale, TranslateTransform translate] }:
                return new TransformParts(scale, translate);
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
            case TranslateTransform existing when AtRest(existing):
                var parts = new TransformParts(new ScaleTransform(), new TranslateTransform());
                element.RenderTransform = new TransformGroup { Children = { parts.Scale, parts.Translate } };
                if (element.RenderTransformOrigin == default)
                {
                    element.RenderTransformOrigin = new Point(0.5, 0.5);
                }

                return parts;
            default:
                return null;
        }
    }

    /// <summary>Arrivée d'un panneau : il grandit légèrement en apparaissant.</summary>
    internal static void Pop(UIElement element)
    {
        Enter(element, offset: 16);
        if (IsEnabled && Parts(element) is { } parts)
        {
            var grow = new DoubleAnimation(0.94, 1, new Duration(TimeSpan.FromMilliseconds(280))) { EasingFunction = Ease };
            parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>Remplit une barre depuis zéro, après <paramref name="delay"/>.</summary>
    internal static void FillTo(RangeBase bar, double to, TimeSpan delay)
    {
        if (!IsEnabled)
        {
            bar.Value = to;
            return;
        }

        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(0, to, new Duration(TimeSpan.FromMilliseconds(750))) { BeginTime = delay, EasingFunction = Ease });
    }

    /// <summary>Anime une valeur numérique d'un élément (par exemple la jauge du score) vers <paramref name="to"/>.</summary>
    public static void To(IAnimatable element, DependencyProperty property, double to, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(element);

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
            tabs.Loaded += OnTabsLoaded;
        }
        else
        {
            tabs.SelectionChanged -= OnSelectionChanged;
            tabs.Loaded -= OnTabsLoaded;
        }
    }

    private static void OnTabsLoaded(object sender, RoutedEventArgs e)
    {
        var tabs = (TabControl)sender;
        tabs.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveNavMark(tabs, animated: false));
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Les listes et menus déroulants de la page remontent aussi cet événement : seul l'onglet compte.
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl tabs || !tabs.IsLoaded)
        {
            return;
        }

        var direction = Direction(tabs, e);
        tabs.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            MoveNavMark(tabs, animated: true);
            if (!IsEnabled || tabs.Template?.FindName("PART_SelectedContentHost", tabs) is not ContentPresenter host)
            {
                return;
            }

            Enter(host, offset: 14 * direction);
            var index = 0;
            foreach (var card in Cards(host))
            {
                Enter(card, Stagger * (index + 1), 18 * direction);
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
