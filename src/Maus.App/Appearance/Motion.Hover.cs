using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Maus.App.Appearance;

/// <summary>
/// Effets de survol et effets du contenu (demande du porteur du 1/11/2026 : très animé, très joli, économe en mémoire).
/// Seules la position, la transparence et de petits dégradés bougent : aucune image, aucun flou animé, une seule lueur à la fois.
/// Les effets qui tournent en continu s'arrêtent dès qu'ils ne se voient plus.
/// </summary>
public static partial class Motion
{
    /// <summary>Grande carte cliquable : une lueur à peine visible suit la souris (demande du porteur : ultra légère, et pas partout).</summary>
    public static readonly DependencyProperty SpotlightProperty = DependencyProperty.RegisterAttached(
        "Spotlight", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSpotlightChanged));

    public static bool GetSpotlight(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(SpotlightProperty);
    }

    public static void SetSpotlight(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SpotlightProperty, value);
    }

    /// <summary>Grande catégorie : s'agrandit très légèrement au survol.</summary>
    public static readonly DependencyProperty HoverGrowProperty = DependencyProperty.RegisterAttached(
        "HoverGrow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverGrowChanged));

    public static bool GetHoverGrow(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(HoverGrowProperty);
    }

    public static void SetHoverGrow(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverGrowProperty, value);
    }

    /// <summary>Entrée du menu : glisse de quelques pixels au survol.</summary>
    public static readonly DependencyProperty HoverNudgeProperty = DependencyProperty.RegisterAttached(
        "HoverNudge", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverNudgeChanged));

    public static bool GetHoverNudge(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(HoverNudgeProperty);
    }

    public static void SetHoverNudge(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverNudgeProperty, value);
    }

    /// <summary>Élément avec une icône : l'icône grossit un instant quand la souris arrive.</summary>
    public static readonly DependencyProperty PopIconProperty = DependencyProperty.RegisterAttached(
        "PopIcon", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopIconChanged));

    public static bool GetPopIcon(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(PopIconProperty);
    }

    public static void SetPopIcon(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PopIconProperty, value);
    }

    /// <summary>Bouton principal : un reflet le traverse une fois quand la souris arrive (élément « Sheen » de son gabarit).</summary>
    public static readonly DependencyProperty SheenProperty = DependencyProperty.RegisterAttached(
        "Sheen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSheenChanged));

    public static bool GetSheen(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(SheenProperty);
    }

    public static void SetSheen(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SheenProperty, value);
    }

    /// <summary>Texte d'un <see cref="TextBlock"/> : son premier nombre monte jusqu'à sa nouvelle valeur au lieu de sauter.</summary>
    public static readonly DependencyProperty RollingTextProperty = DependencyProperty.RegisterAttached(
        "RollingText", typeof(string), typeof(Motion), new PropertyMetadata(null, OnRollingTextChanged));

    public static string? GetRollingText(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(RollingTextProperty);
    }

    public static void SetRollingText(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RollingTextProperty, value);
    }

    /// <summary>Tourne tant que la valeur est vraie (arc de l'audit en cours).</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSpinChanged));

    public static bool GetSpin(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(SpinProperty);
    }

    public static void SetSpin(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SpinProperty, value);
    }

    /// <summary>Fond qui change très lentement de teinte, seulement quand il se voit et que la fenêtre est active.</summary>
    public static readonly DependencyProperty AmbientProperty = DependencyProperty.RegisterAttached(
        "Ambient", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAmbientChanged));

    public static bool GetAmbient(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(AmbientProperty);
    }

    public static void SetAmbient(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AmbientProperty, value);
    }

    /// <summary>Carte d'une liste (constats) : apparition plus marquée, une carte après l'autre.</summary>
    public static readonly DependencyProperty LivelyProperty = DependencyProperty.RegisterAttached(
        "Lively", typeof(bool), typeof(Motion));

    public static bool GetLively(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(LivelyProperty);
    }

    public static void SetLively(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LivelyProperty, value);
    }

    /// <summary>Flèche d'une catégorie : tourne d'un quart de tour quand la valeur est vraie.</summary>
    public static readonly DependencyProperty TurnWhenProperty = DependencyProperty.RegisterAttached(
        "TurnWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTurnWhenChanged));

    public static bool GetTurnWhen(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(TurnWhenProperty);
    }

    public static void SetTurnWhen(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TurnWhenProperty, value);
    }

    /// <summary>Case de chargement : un reflet la traverse tant qu'elle se voit.</summary>
    public static readonly DependencyProperty ShimmerProperty = DependencyProperty.RegisterAttached(
        "Shimmer", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnShimmerChanged));

    public static bool GetShimmer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShimmerProperty);
    }

    public static void SetShimmer(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShimmerProperty, value);
    }

    private static readonly DependencyProperty RollProperty = DependencyProperty.RegisterAttached(
        "Roll", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnRollChanged));

    private static readonly DependencyProperty RollPartsProperty = DependencyProperty.RegisterAttached(
        "RollParts", typeof(string[]), typeof(Motion));

    private static readonly Duration RollDuration = new(TimeSpan.FromMilliseconds(800));

    private const double Nudge = 3;

    /// <summary>Agrandissement au survol, en pixels sur la largeur : le même sur une petite tuile et sur une grande carte.</summary>
    private const double GrownPixels = 4;

    private const int HelpDelay = 2000;

    private static SpotlightAdorner? s_light;

    /// <summary>Petits messages d'aide permis (réglé par <see cref="AppearanceManager"/>).</summary>
    public static bool HelpTips { get; set; } = true;

    /// <summary>
    /// Messages d'aide : ils attendent deux secondes sur place, puis apparaissent en fondu ; le suivant vient presque tout de suite
    /// tant que la souris reste dans la zone.
    /// </summary>
    internal static void WatchToolTips()
    {
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(HelpDelay));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(400));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(20_000));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) => Enter((UIElement)sender, offset: 6)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnHelpOpening));
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnHelpOpening));
    }

    private static void OnHelpOpening(object sender, ToolTipEventArgs e) => e.Handled = !HelpTips;

    /// <summary>Fait apparaître une valeur dessinée (courbe) de gauche à droite, de 0 à 1.</summary>
    internal static void Reveal(IAnimatable element, DependencyProperty property)
    {
        if (IsEnabled)
        {
            element.BeginAnimation(property, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }
    }

    /// <summary>Léger halo : la valeur va de 0 à 1 puis revient à 0, après <paramref name="delay"/>.</summary>
    internal static void Halo(IAnimatable element, DependencyProperty property, TimeSpan delay)
    {
        if (!IsEnabled)
        {
            return;
        }

        var glow = new DoubleAnimationUsingKeyFrames { BeginTime = delay, FillBehavior = FillBehavior.Stop };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Ease));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(620)), Ease));
        element.BeginAnimation(property, glow);
    }

    /// <summary>La marque du menu glisse jusqu'à l'entrée choisie (élément « PART_NavMark » du gabarit, s'il existe).</summary>
    private static void MoveNavMark(TabControl tabs, bool animated)
    {
        if (tabs.Template?.FindName("PART_NavMark", tabs) is not FrameworkElement mark
            || tabs.ItemContainerGenerator.ContainerFromIndex(tabs.SelectedIndex) is not FrameworkElement item
            || VisualTreeHelper.GetParent(mark) is not UIElement parent)
        {
            return;
        }

        var y = item.TranslatePoint(new Point(0, (item.ActualHeight - mark.Height) / 2), parent).Y;
        if (mark.RenderTransform is not TranslateTransform move)
        {
            mark.RenderTransform = move = new TranslateTransform();
        }

        mark.Visibility = Visibility.Visible;
        if (animated && IsEnabled)
        {
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = Ease });
        }
        else
        {
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.Y = y;
        }
    }

    /// <summary>Le contenu vient du bas si on descend dans le menu, du haut si on remonte.</summary>
    private static int Direction(TabControl tabs, SelectionChangedEventArgs e) =>
        e.AddedItems.Count > 0 && e.RemovedItems.Count > 0 && tabs.Items.IndexOf(e.AddedItems[0]) < tabs.Items.IndexOf(e.RemovedItems[0]) ? -1 : 1;

    private static void OnSpotlightChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement target)
        {
            return;
        }

        target.MouseEnter -= OnSpotlightEnter;
        target.MouseMove -= OnSpotlightMove;
        target.MouseLeave -= OnSpotlightLeave;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnSpotlightEnter;
            target.MouseMove += OnSpotlightMove;
            target.MouseLeave += OnSpotlightLeave;
        }
    }

    private static void OnSpotlightEnter(object sender, MouseEventArgs e)
    {
        if (!IsEnabled || SystemParameters.HighContrast || sender is not FrameworkElement element || AdornerLayer.GetAdornerLayer(element) is not { } layer)
        {
            return;
        }

        s_light?.Close();
        s_light = new SpotlightAdorner(element, layer, e.GetPosition(element));
        layer.Add(s_light);
    }

    private static void OnSpotlightMove(object sender, MouseEventArgs e)
    {
        if (s_light is { } light && ReferenceEquals(light.AdornedElement, sender))
        {
            light.MoveTo(e.GetPosition((IInputElement)sender));
        }
    }

    private static void OnSpotlightLeave(object sender, MouseEventArgs e)
    {
        if (s_light is { } light && ReferenceEquals(light.AdornedElement, sender))
        {
            light.Close();
            s_light = null;
        }
    }

    private sealed class SpotlightAdorner : Adorner
    {
        private readonly AdornerLayer _layer;
        private readonly RadialGradientBrush _light;
        private readonly ScrollViewer? _scroll;

        public SpotlightAdorner(FrameworkElement adorned, AdornerLayer layer, Point at)
            : base(adorned)
        {
            _layer = layer;
            _scroll = ScrollOwner(adorned);
            IsHitTestVisible = false;
            var color = adorned.TryFindResource("MausAccent") is SolidColorBrush accent ? accent.Color : Colors.White;
            _light = new RadialGradientBrush(Color.FromArgb(0x12, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B))
            {
                MappingMode = BrushMappingMode.Absolute,
                RadiusX = 140,
                RadiusY = 140,
                Center = at,
                GradientOrigin = at,
            };
            adorned.Unloaded += OnAdornedUnloaded;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, HoverDuration));
        }

        public void MoveTo(Point at)
        {
            _light.Center = at;
            _light.GradientOrigin = at;
            InvalidateVisual();
        }

        public void Close()
        {
            ((FrameworkElement)AdornedElement).Unloaded -= OnAdornedUnloaded;
            var fade = new DoubleAnimation(0, HoverDuration);
            fade.Completed += (_, _) => _layer.Remove(this);
            BeginAnimation(OpacityProperty, fade);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var area = new Rect(AdornedElement.RenderSize);
            var corner = AdornedElement is Border border ? border.CornerRadius.TopLeft : 14;

            // Carte à moitié cachée par le défilement : la lueur reste dans la partie visible.
            if (_scroll is not null && _scroll.IsAncestorOf(AdornedElement))
            {
                area.Intersect(_scroll.TransformToDescendant(AdornedElement).TransformBounds(new Rect(0, 0, _scroll.ViewportWidth, _scroll.ViewportHeight)));
            }

            if (!area.IsEmpty)
            {
                drawingContext.DrawRoundedRectangle(_light, null, area, corner, corner);
            }
        }

        private void OnAdornedUnloaded(object sender, RoutedEventArgs e)
        {
            ((FrameworkElement)AdornedElement).Unloaded -= OnAdornedUnloaded;
            _layer.Remove(this);
            if (ReferenceEquals(s_light, this))
            {
                s_light = null;
            }
        }
    }

    private static void OnHoverGrowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.MouseEnter -= OnGrowEnter;
        target.MouseLeave -= OnGrowLeave;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnGrowEnter;
            target.MouseLeave += OnGrowLeave;
        }
    }

    private static void OnGrowEnter(object sender, MouseEventArgs e) => Squeeze((UIElement)sender, Grown((UIElement)sender), 180);

    /// <summary>
    /// Agrandissement au survol, sans jamais dépasser de la colonne de la page : une carte collée à un bord grandit vers l'intérieur,
    /// une carte sur toute la largeur ne grandit pas. Le haut ne bouge pas, pour ne rien cacher de ce qui est au-dessus.
    /// </summary>
    private static double Grown(UIElement element)
    {
        var grown = 1 + (GrownPixels / Math.Max(100, element.RenderSize.Width));
        if (ScrollOwner(element)?.Content is not FrameworkElement page || !page.IsAncestorOf(element))
        {
            return grown;
        }

        var left = element.TranslatePoint(default, page).X;
        var right = page.ActualWidth - left - element.RenderSize.Width;
        if (left < GrownPixels && right < GrownPixels)
        {
            return 1;
        }

        element.RenderTransformOrigin = new Point(left < GrownPixels ? 0 : right < GrownPixels ? 1 : 0.5, 0);
        return grown;
    }

    private static void OnGrowLeave(object sender, MouseEventArgs e) => Squeeze((UIElement)sender, 1, 180);

    private static void OnHoverNudgeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.MouseEnter -= OnNudgeEnter;
        target.MouseLeave -= OnNudgeLeave;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnNudgeEnter;
            target.MouseLeave += OnNudgeLeave;
        }
    }

    private static void OnNudgeEnter(object sender, MouseEventArgs e) => Slide((UIElement)sender, Nudge);

    private static void OnNudgeLeave(object sender, MouseEventArgs e) => Slide((UIElement)sender, 0);

    private static void Slide(UIElement element, double to)
    {
        if (Parts(element) is { } parts)
        {
            parts.Translate.BeginAnimation(TranslateTransform.XProperty, IsEnabled ? new DoubleAnimation(to, HoverDuration) { EasingFunction = Ease } : null);
        }
    }

    private static void OnPopIconChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.MouseEnter -= OnPopEnter;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnPopEnter;
        }
    }

    private static void OnPopEnter(object sender, MouseEventArgs e)
    {
        if (IsEnabled && FirstIcon((DependencyObject)sender) is { } icon)
        {
            Pop(icon, TimeSpan.Zero);
        }
    }

    private static void Pop(UIElement icon, TimeSpan delay)
    {
        if (Parts(icon) is not { } parts)
        {
            return;
        }

        var pop = new DoubleAnimationUsingKeyFrames { BeginTime = delay, FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), Ease));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), Ease));
        parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Première icône (glyphe de la police d'icônes) sous l'élément.</summary>
    private static TextBlock? FirstIcon(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock icon && icon.FontFamily.Source.Contains("Fluent Icons", StringComparison.Ordinal))
            {
                return icon;
            }

            if (FirstIcon(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void OnSheenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.MouseEnter -= OnSheenEnter;
        if ((bool)e.NewValue)
        {
            target.MouseEnter += OnSheenEnter;
        }
    }

    private static void OnSheenEnter(object sender, MouseEventArgs e)
    {
        if (IsEnabled && sender is Control { IsEnabled: true } button && button.Template?.FindName("Sheen", button) is Border sheen)
        {
            sheen.Background = PassingLight(new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromMilliseconds(620))) { EasingFunction = Ease }, 0x48);
        }
    }

    /// <summary>Bande claire qui traverse l'élément de gauche à droite.</summary>
    private static LinearGradientBrush PassingLight(DoubleAnimation move, byte strength)
    {
        var offset = new TranslateTransform(-1, 0);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = offset,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.2),
                new GradientStop(Color.FromArgb(strength, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.8),
            },
        };
        offset.BeginAnimation(TranslateTransform.XProperty, move);
        return brush;
    }

    private static void OnRollingTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        var text = (string?)e.NewValue ?? string.Empty;
        var next = NumberPattern().Match(text);
        if (!IsEnabled || !next.Success)
        {
            block.BeginAnimation(RollProperty, null);
            block.Text = text;
            return;
        }

        var previous = NumberPattern().Match((string?)e.OldValue ?? string.Empty);
        var from = previous.Success ? Digits(previous.Groups[2].Value) : 0;
        var separator = new string(next.Groups[2].Value.Where(c => !char.IsAsciiDigit(c)).Take(1).ToArray());
        block.SetValue(RollPartsProperty, new[] { next.Groups[1].Value, next.Groups[3].Value, separator });
        block.BeginAnimation(RollProperty, new DoubleAnimation(from, Digits(next.Groups[2].Value), RollDuration) { EasingFunction = Ease });
    }

    private static double Digits(string number) =>
        double.Parse(new string(number.Where(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);

    private static void OnRollChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBlock block && block.GetValue(RollPartsProperty) is string[] parts)
        {
            var format = new NumberFormatInfo { NumberGroupSeparator = parts[2], NumberGroupSizes = [parts[2].Length > 0 ? 3 : 0] };
            block.Text = parts[0] + Math.Round((double)e.NewValue).ToString("#,0", format) + parts[1];
        }
    }

    [GeneratedRegex(@"^([^0-9]*)([0-9]{1,3}(?:[\s\u00A0\u202F,.][0-9]{3})+|[0-9]{1,9})(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NumberPattern();

    private static void OnSpinChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        if (target.RenderTransform is not RotateTransform turn)
        {
            target.RenderTransform = turn = new RotateTransform();
            target.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!(bool)e.NewValue || !IsEnabled)
        {
            turn.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }

        var loop = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.6))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(loop, 30);
        turn.BeginAnimation(RotateTransform.AngleProperty, loop);
    }

    private static void OnAmbientChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Control target && (bool)e.NewValue)
        {
            target.Loaded += OnAmbientLoaded;
        }
    }

    private static void OnAmbientLoaded(object sender, RoutedEventArgs e)
    {
        var target = (Control)sender;
        target.Loaded -= OnAmbientLoaded;
        if (Window.GetWindow(target) is not { } window)
        {
            return;
        }

        var color = target.TryFindResource("MausAccent") is SolidColorBrush accent ? accent.Color : Colors.Transparent;
        var brush = new LinearGradientBrush(Color.FromArgb(0x16, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), new Point(0, 0), new Point(1, 1));
        var start = AmbientClock(new Point(0, 0), new Point(1, 0));
        var end = AmbientClock(new Point(1, 1), new Point(0, 1));
        brush.ApplyAnimationClock(LinearGradientBrush.StartPointProperty, start);
        brush.ApplyAnimationClock(LinearGradientBrush.EndPointProperty, end);
        target.Background = brush;

        void Update()
        {
            if (IsEnabled && target.IsVisible && window.IsActive && window.WindowState != WindowState.Minimized)
            {
                start.Controller?.Resume();
                end.Controller?.Resume();
            }
            else
            {
                start.Controller?.Pause();
                end.Controller?.Pause();
            }
        }

        window.Activated += (_, _) => Update();
        window.Deactivated += (_, _) => Update();
        window.StateChanged += (_, _) => Update();
        target.IsVisibleChanged += (_, _) => Update();
        Update();
    }

    private static AnimationClock AmbientClock(Point from, Point to)
    {
        var drift = new PointAnimation(from, to, new Duration(TimeSpan.FromSeconds(24))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(drift, 10);
        return drift.CreateClock();
    }

    private static void OnShimmerChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Border target && (bool)e.NewValue)
        {
            target.IsVisibleChanged += OnShimmerVisible;
        }
    }

    private static void OnShimmerVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        var target = (Border)sender;
        if (!(bool)e.NewValue || !IsEnabled)
        {
            target.Child = null;
            return;
        }

        var loop = new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromSeconds(1.5))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(loop, 30);
        target.Child = new Border { CornerRadius = target.CornerRadius, Background = PassingLight(loop, 0x30) };
    }

    private static readonly TimeSpan LivelyStagger = TimeSpan.FromMilliseconds(70);

    private static readonly ConditionalWeakTable<ScrollViewer, List<FrameworkElement>> s_waiting = new();

    private const int MaxCascade = 12;

    private const int MaxRevealed = 6;

    private static ScrollViewer? ScrollOwner(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroll)
            {
                return scroll;
            }
        }

        return null;
    }

    private static bool IsBelow(UIElement element, ScrollViewer scroll) =>
        scroll.ViewportHeight > 0 && element.TranslatePoint(default, scroll).Y > scroll.ViewportHeight;

    /// <summary>Carte d'une liste qui apparaît : les cartes « vivantes » glissent de plus loin, grandissent, et leur icône s'anime.</summary>
    private static void EnterItem(FrameworkElement element, int rank)
    {
        if (!GetLively(element))
        {
            Enter(element, Stagger * (rank + 1), 18);
            return;
        }

        var delay = LivelyStagger * (rank + 1);
        Enter(element, delay, 26);
        if (Parts(element) is { } parts)
        {
            var grow = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(delay)));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + PageDuration.TimeSpan), Ease));
            parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        if (FirstIcon(element) is { } icon)
        {
            Pop(icon, delay + TimeSpan.FromMilliseconds(180));
        }
    }

    /// <summary>Carte encore hors de vue : transparente sans toucher à sa valeur, en attendant le défilement.</summary>
    private static void Hold(FrameworkElement element, ScrollViewer scroll)
    {
        if (!s_waiting.TryGetValue(scroll, out var waiting))
        {
            waiting = [];
            s_waiting.Add(scroll, waiting);
            scroll.ScrollChanged += OnScrolled;
        }

        if (!waiting.Contains(element))
        {
            waiting.Add(element);
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.Zero)));
    }

    private static void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not ScrollViewer scroll || !s_waiting.TryGetValue(scroll, out var waiting))
        {
            return;
        }

        var rank = 0;
        for (var i = 0; i < waiting.Count; i++)
        {
            var element = waiting[i];
            if (!element.IsLoaded || !scroll.IsAncestorOf(element))
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                waiting.RemoveAt(i--);
            }
            else if (!IsEnabled || !IsBelow(element, scroll))
            {
                waiting.RemoveAt(i--);
                element.BeginAnimation(UIElement.OpacityProperty, null);
                if (IsEnabled)
                {
                    EnterItem(element, Math.Min(rank++, MaxRevealed));
                }
            }
        }
    }

    /// <summary>Une catégorie s'ouvre : ce qu'elle contient apparaît un élément après l'autre.</summary>
    private static void Cascade(UIElement content)
    {
        var index = 0;
        foreach (var child in Contents(content).Take(MaxCascade))
        {
            Enter(child, Stagger * ++index, 16);
        }
    }

    private static IEnumerable<UIElement> Contents(UIElement content) => content switch
    {
        Panel panel => panel.Children.OfType<UIElement>().Where(c => c.Visibility == Visibility.Visible),
        Border { Child: { } child } => Contents(child),
        ItemsControl items => Enumerable.Range(0, items.Items.Count).Select(i => items.ItemContainerGenerator.ContainerFromIndex(i)).OfType<UIElement>(),
        _ => [],
    };

    private static void OnTurnWhenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        if (target.RenderTransform is not RotateTransform turn)
        {
            target.RenderTransform = turn = new RotateTransform();
            target.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        To(turn, RotateTransform.AngleProperty, (bool)e.NewValue ? 90 : 0, TimeSpan.FromMilliseconds(180));
    }
}
