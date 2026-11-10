using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Controls;

/// <summary>
/// Mots techniques d'un texte : soulignés en pointillés, avec une explication ultra simple au survol (demande du porteur du 1/11/2026).
/// S'utilise à la place de <c>Text</c> : <c>controls:Glossary.Text="{Binding Explication}"</c>.
/// </summary>
public static partial class Glossary
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Glossary), new PropertyMetadata(null, OnTextChanged));

    private static readonly TextDecorationCollection Dotted = CreateDotted();

    public static string? GetText(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(TextProperty);
    }

    public static void SetText(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TextProperty, value);
    }

    /// <summary>Explication d'un mot technique, dans la langue de l'interface.</summary>
    internal static string? Explain(string term) => term switch
    {
        "XMP" => T("Profil qui fait tourner la mémoire à la vitesse annoncée."),
        "DISM" => T("Outil de Microsoft qui répare l'image de Windows."),
        "SFC" => T("Outil de Microsoft qui répare les fichiers de Windows."),
        "Curve Optimizer" => T("Réglage AMD qui baisse la tension du processeur, cœur par cœur."),
        "undervolt" => T("Baisser la tension du processeur pour qu'il chauffe moins."),
        "TjMax" => T("Température maximale du processeur, avant qu'il ralentisse pour se protéger."),
        "UAC" => T("La fenêtre de Windows qui demande votre autorisation."),
        "BIOS" or "UEFI" => T("Logiciel de la carte mère, lancé avant Windows."),
        "Secure Boot" => T("Vérifie que le démarrage du PC n'a pas été modifié."),
        "TPM" => T("Puce de sécurité du PC, demandée par Windows 11."),
        "SSD" => T("Disque rapide, sans pièce mobile."),
        "VRAM" => T("Mémoire de la carte graphique."),
        "BitLocker" => T("Chiffrement du disque par Windows."),
        "WHEA" => T("Erreurs matérielles signalées par le processeur ou la mémoire."),
        "PawnIO" => T("Petit pilote qui permet de lire les capteurs avancés."),
        _ => null,
    };

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        var text = (string?)e.NewValue ?? string.Empty;
        block.Inlines.Clear();
        var position = 0;
        foreach (Match match in TermPattern().Matches(text))
        {
            if (match.Index > position)
            {
                block.Inlines.Add(new Run(text[position..match.Index]));
            }

            block.Inlines.Add(new Run(match.Value) { TextDecorations = Dotted, ToolTip = Explain(match.Value), Cursor = Cursors.Help });
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            block.Inlines.Add(new Run(text[position..]));
        }
    }

    private static TextDecorationCollection CreateDotted()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), 1) { DashStyle = DashStyles.Dot };
        pen.Freeze();
        var decorations = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended) };
        decorations.Freeze();
        return decorations;
    }

    [GeneratedRegex(@"\b(XMP|DISM|SFC|Curve Optimizer|undervolt|TjMax|UAC|BIOS|UEFI|Secure Boot|TPM|SSD|VRAM|BitLocker|WHEA|PawnIO)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();
}
