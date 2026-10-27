using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Maus.Core.Modules.M14Display;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// Essai de la fréquence maximale d'un écran (Module 14) : confirmation, essai non enregistré, puis « Garder » ou retour
/// automatique à l'ancien réglage après 15 secondes sans réponse (écran noir, fenêtre hors de vue, fermeture).
/// </summary>
public sealed class RefreshTrialWindow : Window
{
    private readonly RefreshRateTrial _trial;
    private readonly TextBlock _countdown = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14), FontWeight = FontWeights.SemiBold };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _deadline;
    private bool _keep;

    private RefreshTrialWindow(RefreshRateTrial trial)
    {
        _trial = trial;
        var target = trial.Target;
        Title = T("Garder {0} Hz ?", target.TargetHz);
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("{0} affiche maintenant {1} Hz (au lieu de {2} Hz). L'image est-elle normale ?", target.Label, target.TargetHz, target.CurrentHz),
        };
        AutomationProperties.SetName(message, message.Text);

        var keep = new Button { Content = T("Garder {0} Hz", target.TargetHz), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        keep.SetResourceReference(StyleProperty, "PrimaryButton");
        keep.Click += (_, _) =>
        {
            _keep = true;
            Close();
        };
        var back = new Button { Content = T("Revenir à {0} Hz", target.CurrentHz), IsCancel = true, IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
        back.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(keep);
        buttons.Children.Add(back);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(message);
        panel.Children.Add(_countdown);
        panel.Children.Add(buttons);
        Content = panel;

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            _deadline = DateTime.UtcNow + RefreshRateTrial.ConfirmDelay;
            Tick();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        var left = (int)Math.Ceiling((_deadline - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            Close();
            return;
        }

        _countdown.Text = T("Sans réponse, l'ancien réglage revient dans {0} s.", left);
        AutomationProperties.SetName(_countdown, _countdown.Text);
    }

    /// <summary>Confirme, essaie, demande de garder ; tout ce qui n'est pas « Garder » ramène l'ancien réglage.</summary>
    /// <returns>Message de bilan à afficher.</returns>
    public static string Run(RefreshTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var question = T("MAUS va passer {0} de {1} Hz à {2} Hz, sans changer la résolution ({3}×{4}). Ce n'est qu'un essai : rien n'est enregistré tant que vous n'avez pas cliqué sur « Garder ».",
                target.Label, target.CurrentHz, target.TargetHz, target.Width, target.Height)
            + Environment.NewLine + Environment.NewLine
            + T("Écran noir ? Ne touchez à rien : l'ancien réglage revient dans {0} secondes.", (int)RefreshRateTrial.ConfirmDelay.TotalSeconds)
            + (target.HdrActive
                ? Environment.NewLine + Environment.NewLine + T("HDR actif : à fréquence plus haute, le câble peut compresser les couleurs (texte qui bave). Relancez l'audit ensuite pour vérifier l'encodage.")
                : string.Empty);
        if (MessageBox.Show(question, T("Essayer {0} Hz ?", target.TargetHz), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return T("Essai annulé : rien n'a changé.");
        }

        var trial = new RefreshRateTrial(new Win32DisplayModeWriter(), target);
        var started = trial.Start();
        if (started != DisplayChange.Applied)
        {
            return RefreshRateTrial.Describe(started);
        }

        var window = new RefreshTrialWindow(trial);
        DisplayChange? reverted = null;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            if (!trial.Finished && !window._keep)
            {
                reverted = trial.Revert();
            }
        }

        if (window._keep)
        {
            var kept = trial.Keep();
            return kept == DisplayChange.Applied
                ? T("{0} reste à {1} Hz. Relancez l'audit pour mettre le constat à jour.", target.Label, target.TargetHz)
                : RefreshRateTrial.Describe(kept);
        }

        return reverted is null or DisplayChange.Applied
            ? T("Retour à {0} Hz : rien n'a été enregistré.", target.CurrentHz)
            : T("Windows n'a pas pu revenir à {0} Hz tout de suite. Rien n'a été enregistré : l'ancien réglage reviendra au prochain redémarrage, ou choisissez-le dans Paramètres > Système > Écran > Écran avancé.", target.CurrentHz);
    }
}
