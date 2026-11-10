using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Maus.App.ViewModels;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Appearance;

/// <summary>
/// Ce qui est grisé dit pourquoi au survol (demande du porteur du 1/11/2026). La raison se déduit de ce qui grise l'élément : la liaison
/// de son <c>IsEnabled</c>, ou sa commande déjà en cours. Un parent grisé passe avant : c'est lui, la vraie raison.
/// </summary>
public static class WhyDisabled
{
    private static readonly DependencyProperty ReasonProperty = DependencyProperty.RegisterAttached(
        "Reason", typeof(string), typeof(WhyDisabled));

    /// <summary>
    /// À poser sur tout ce qui peut être grisé. Le gestionnaire doit être sur l'élément lui-même : pour aller plus vite, WPF n'envoie
    /// <c>Loaded</c> qu'aux éléments qui en ont un, jamais à un simple gestionnaire de classe.
    /// </summary>
    public static readonly DependencyProperty ExplainProperty = DependencyProperty.RegisterAttached(
        "Explain", typeof(bool), typeof(WhyDisabled), new PropertyMetadata(false, OnExplainChanged));

    public static bool GetExplain(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ExplainProperty);
    }

    public static void SetExplain(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplainProperty, value);
    }

    /// <summary>Explication d'une condition de <c>IsEnabled</c>, d'après son nom dans le modèle ; <c>null</c> si elle n'est pas connue.</summary>
    public static string? Reason(string bindingPath)
    {
        ArgumentNullException.ThrowIfNull(bindingPath);
        return bindingPath[(bindingPath.LastIndexOf('.') + 1)..] switch
        {
            "IsFull" => T("Demande les droits administrateur : cliquez sur « Toutes les fonctionnalités », en haut de la fenêtre."),
            "HasAudit" => Ui.TipNeedsAudit,
            "IsIdle" => T("Un test est en cours : attendez la fin ou arrêtez-le."),
            "IsNotBenchmarkRunning" => T("Le benchmark est en cours : attendez la fin."),
            "IsNotScanning" => T("L'analyse du disque est en cours : attendez la fin ou arrêtez-la."),
            "IsNotLatencyRunning" => T("La mesure de latence est en cours : attendez la fin."),
            "IsNotRecording" => T("Un enregistrement est en cours : arrêtez-le d'abord."),
            "IsNotReadingMemory" => T("La lecture de la mémoire est en cours : attendez la fin."),
            "IsNotNetTesting" => T("Le test de connexion est en cours : attendez la fin ou arrêtez-le."),
            "HasMemoryReport" => T("Rien à copier pour l'instant : lisez d'abord la mémoire."),
            "CanGoUp" => T("Vous êtes déjà dans le dossier de départ."),
            "CanRevert" => T("Rien à annuler : ces corrections ne sont plus en place."),
            "CanBackupOrRemove" => T("C'est un pilote de Windows : MAUS ne le sauvegarde pas et ne le supprime pas."),
            "CanReinstall" => T("Seulement pour une carte branchée sur le bus PCI : un adaptateur virtuel ne serait pas recréé."),
            "CanTerminate" => T("MAUS n'arrête pas ce processus : il est protégé ou appartient à un autre compte."),
            "CanUpdate" => T("winget a coupé le nom de ce logiciel : MAUS ne peut pas le choisir sans risque de se tromper."),
            "CanApply" => T("Cochez au moins une correction. Si une opération est en cours, attendez sa fin."),
            "OverlayEnabled" => T("Cochez d'abord « Afficher le compteur au-dessus du jeu »."),
            "CanEnable" => T("La demande d'autorisation est déjà en cours."),
            _ => null,
        };
    }

    internal static void Watch() =>
        ToolTipService.ShowOnDisabledProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(true));

    private static void OnExplainChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement target)
        {
            target.Loaded -= OnLoaded;
            if ((bool)e.NewValue)
            {
                target.Loaded += OnLoaded;
            }
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.GetValue(ReasonProperty) is not null)
        {
            return;
        }

        var reason = BindingOperations.GetBinding(element, UIElement.IsEnabledProperty) is { Path.Path: { } path }
            ? Reason(path)
            : element is ButtonBase { Command: AsyncCommand } ? T("Cette action est déjà en cours.") : null;
        if (reason is null)
        {
            return;
        }

        element.SetValue(ReasonProperty, reason);
        element.IsEnabledChanged += (_, _) => Apply(element);
        Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        if (!element.IsEnabled && ReasonFor(element) is { } reason)
        {
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, reason);
        }
        else
        {
            element.InvalidateProperty(FrameworkElement.ToolTipProperty);
        }
    }

    private static string? ReasonFor(FrameworkElement element)
    {
        string? reason = null;
        for (DependencyObject? current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
        {
            if (current is UIElement { IsEnabled: false } disabled && disabled.GetValue(ReasonProperty) is string own && !IsReadyCommand(disabled))
            {
                reason = own;
            }
        }

        return reason;
    }

    /// <summary>Bouton dont la commande est libre : il n'est pas gris à cause d'elle, mais de son parent.</summary>
    private static bool IsReadyCommand(UIElement element) =>
        element is ButtonBase { Command: { } command } button
        && BindingOperations.GetBinding(button, UIElement.IsEnabledProperty) is null
        && command.CanExecute(button.CommandParameter);
}
