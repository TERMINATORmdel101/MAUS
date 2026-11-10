using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Maus.Core.Engine.Broker;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>
/// Ce qu'écrit un outil de Windows lancé par le moteur (DISM, SFC, pnputil), au fur et à mesure, avec son avancement (0.8.6) : le
/// moteur n'ouvre plus de fenêtre de commande. Elle s'ouvre à la première ligne, donc jamais si l'autorisation ou la confirmation
/// a été refusée. La fermer laisse l'outil finir.
/// </summary>
public sealed partial class ToolWindow : Window
{
    private static int s_running;

    private readonly TextBox _text;
    private readonly ProgressBar _bar;
    private int _lastPercentLine = -1;

    private ToolWindow(string title)
    {
        Title = title;
        Width = 720;
        Height = 480;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        _bar = new ProgressBar { Height = 10, IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 12), Minimum = 0, Maximum = 100 };
        AutomationProperties.SetName(_bar, T("Avancement"));
        _text = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
        AutomationProperties.SetName(_text, title);

        var close = new Button { Content = T("Fermer"), IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var panel = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(_bar);
        panel.Children.Add(close);
        panel.Children.Add(_text);
        Content = panel;
    }

    /// <summary>Un outil travaille encore : fermer MAUS arrêterait la suite de la tâche.</summary>
    public static bool IsRunning => Volatile.Read(ref s_running) > 0;

    public static async Task<ToolOutcome> RunAsync(string title, Func<IProgress<string>, Task<ToolOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        ToolWindow? window = null;
        var progress = new Progress<string>(line =>
        {
            if (window is null)
            {
                window = new ToolWindow(title);
                window.Show();
            }

            window.Add(line);
        });
        Interlocked.Increment(ref s_running);
        try
        {
            var outcome = await run(progress);
            window?.Finish(outcome.Message);
            return outcome;
        }
        catch
        {
            window?.Finish(T("L'outil s'est arrêté avant la fin."));
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref s_running);
        }
    }

    private void Add(string line)
    {
        // Une ligne de pourcentage remplace la précédente : DISM et SFC en écrivent des centaines.
        if (PercentPattern().Match(line) is { Success: true } percent
            && double.TryParse(percent.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value <= 100)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = value;
            if (_lastPercentLine >= 0)
            {
                _text.Text = _text.Text[.._lastPercentLine];
            }

            _lastPercentLine = _text.Text.Length;
        }
        else
        {
            _lastPercentLine = -1;
        }

        _text.AppendText(line + Environment.NewLine);
        _text.ScrollToEnd();
    }

    private void Finish(string? message)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = _bar.Maximum;
        _lastPercentLine = -1;
        _text.AppendText(Environment.NewLine + message + Environment.NewLine);
        _text.ScrollToEnd();
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();
}
