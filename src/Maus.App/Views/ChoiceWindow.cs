using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Choix d'un élément dans une courte liste (sauvegardes de pilotes listées par le moteur).</summary>
public sealed class ChoiceWindow : Window
{
    private readonly ListBox _list = new() { MinHeight = 160, Margin = new Thickness(0, 10, 0, 12) };

    private ChoiceWindow(string title, string message, IReadOnlyList<string> items)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        foreach (var item in items)
        {
            _list.Items.Add(item);
        }

        _list.SelectedIndex = 0;
        AutomationProperties.SetName(_list, message);

        var ok = new Button { Content = T("Choisir"), IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = _list.SelectedItem is not null;
        var cancel = new Button { Content = T("Annuler"), IsCancel = true, MinWidth = 110 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_list);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Élément choisi, ou <c>null</c> si l'utilisateur renonce.</summary>
    public static string? Pick(string title, string message, IReadOnlyList<string> items)
    {
        var window = new ChoiceWindow(title, message, items);
        return window.ShowDialog() == true ? window._list.SelectedItem as string : null;
    }
}
