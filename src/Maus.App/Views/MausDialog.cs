using System.Windows;
using System.Windows.Controls;

namespace Maus.App.Views;

/// <summary>Fenêtre de dialogue écrite en C# : taille, position, ressources du thème et propriétaire communs.</summary>
public abstract class MausDialog : Window
{
    protected MausDialog(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = Application.Current?.MainWindow;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    /// <summary>Rangée de boutons, alignée à droite.</summary>
    protected static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }
}
