using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Maus.Core.Tests.App;

/// <summary>
/// Garde-fou pour l'interface WPF, qui ne peut pas être lancée sous Linux : les erreurs qui font planter une fenêtre
/// au chargement (ressource statique absente, texte Ui inexistant, type de modèle inconnu) sont détectées ici.
/// </summary>
public partial class XamlConsistencyTests
{
    private static string AppFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "Maus.App"));

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppFolder(), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string CSharp() => string.Join("\n", Directory.EnumerateFiles(AppFolder(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Select(File.ReadAllText));

    [Fact]
    public void Every_static_resource_is_defined()
    {
        var files = XamlFiles().ToList();
        var keys = files.SelectMany(f => Key().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var missing = files.SelectMany(f => StaticResource().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .Where(r => !keys.Contains(r.Key))
            .ToList();

        Assert.True(missing.Count == 0, "Ressources statiques absentes : " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_ui_text_exists()
    {
        var code = CSharp();
        var missing = XamlFiles().SelectMany(f => UiStatic().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(name => !Regex.IsMatch(code, $@"public static (string|IReadOnlyList<string>) {name}\b"))
            .ToList();

        Assert.True(missing.Count == 0, "Textes Ui absents : " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_data_type_exists()
    {
        var code = CSharp();
        var missing = XamlFiles().SelectMany(f => DataType().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(type => !Regex.IsMatch(code, $@"\b(class|record) {type}\b"))
            .ToList();

        Assert.True(missing.Count == 0, "Types de modèles absents : " + string.Join(", ", missing));
    }

    [Fact]
    public void Everything_that_can_be_greyed_says_why()
    {
        // Demande du porteur (1/11/2026) : un élément grisé explique pourquoi au survol. Une nouvelle condition doit avoir sa raison.
        var reasons = File.ReadAllText(Path.Combine(AppFolder(), "Appearance", "WhyDisabled.cs"));
        var missing = XamlFiles()
            .SelectMany(f => EnabledBinding().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value.Split('.')[^1]))
            .Distinct()
            .Where(path => !reasons.Contains($"\"{path}\" =>", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "Sans explication : " + string.Join(", ", missing));

        // Sans la propriété posée sur l'élément, WPF ne prévient jamais le mécanisme : le message n'apparaîtrait pas.
        var notWatched = XamlFiles().Where(f => NotExplained().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        Assert.True(notWatched.Count == 0, "Sans WhyDisabled.Explain : " + string.Join(", ", notWatched));
    }

    [Fact]
    public void Two_way_bindings_target_settable_properties()
    {
        // Le plantage de la V0.1 : une liaison bidirectionnelle (par défaut sur IsChecked, SelectedItem, Text d'une TextBox…)
        // vers une propriété en lecture seule. Chaque propriété liée ainsi doit avoir un « set ». Les colonnes d'un DataGrid
        // (attribut Binding) sont aussi bidirectionnelles par défaut.
        var code = CSharp();
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match match in TwoWayBinding().Matches(File.ReadAllText(file)))
            {
                var property = match.Groups[2].Value.Split('.')[^1];
                if (!Regex.IsMatch(code, $@"public [^\n]*\b{property}\s*\{{[^}}]*\bset\b", RegexOptions.Singleline) &&
                    !Regex.IsMatch(code, $@"public [^\n(]*\b{property}\s*\n\s*\{{\s*\n\s*get[^\n]*\n(\s*[^\n]*\n)*?\s*set\b"))
                {
                    offenders.Add($"{Path.GetFileName(file)} : {match.Groups[1].Value}={property}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Liaisons bidirectionnelles vers une propriété sans « set » : " + string.Join(", ", offenders));
    }

    [Fact]
    public void Inputs_have_a_name_for_screen_readers()
    {
        // Liste déroulante, case à cocher sans texte, zone de saisie : le Narrateur doit pouvoir dire à quoi elle sert.
        // Exemptés : un contrôle qui porte son propre texte (Content ou contenu entre balises) et un texte en lecture seule.
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match match in InputElement().Matches(File.ReadAllText(file)))
            {
                var tag = match.Value;
                var named = tag.Contains("AutomationProperties.Name", StringComparison.Ordinal) || tag.Contains("AutomationProperties.LabeledBy", StringComparison.Ordinal);
                var selfLabelled = tag.Contains("Content=", StringComparison.Ordinal) || !tag.EndsWith("/>", StringComparison.Ordinal);
                var readOnly = tag.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal);
                if (!named && !selfLabelled && !readOnly)
                {
                    offenders.Add($"{Path.GetFileName(file)} : {Regex.Replace(tag, @"\s+", " ")[..Math.Min(90, tag.Length)]}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Contrôles sans nom pour les lecteurs d'écran : " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Controls_without_text_of_their_own_have_a_name_for_screen_readers()
    {
        // Bouton dont le contenu est un dessin, onglet dont l'en-tête est un dessin, grille, liste, barre : sans nom, le Narrateur
        // dit « bouton » ou lit le nom d'une classe .NET.
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match match in UnnamedCandidate().Matches(File.ReadAllText(file)))
            {
                var attributes = match.Groups[2].Value;
                var ownText = match.Groups[1].Value switch
                {
                    "Button" or "RadioButton" or "ToggleButton" => attributes.Contains("Content=", StringComparison.Ordinal),
                    "TabItem" or "Expander" => attributes.Contains("Header=", StringComparison.Ordinal),
                    _ => false,
                };
                if (!ownText && !attributes.Contains("AutomationProperties.Name", StringComparison.Ordinal) && !attributes.Contains("AutomationProperties.LabeledBy", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} : {Regex.Replace(match.Value, @"\s+", " ")[..Math.Min(90, match.Length)]}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Contrôles sans nom pour les lecteurs d'écran : " + string.Join(" | ", offenders));
    }

    [Fact]
    public void A_name_is_never_put_on_a_plain_panel()
    {
        // Un cadre ou un panneau n'existe pas pour les lecteurs d'écran : son nom ne serait jamais lu. Voir Controls/AccessibleGroup.
        var offenders = XamlFiles().SelectMany(file => NamedPanel().Matches(File.ReadAllText(file)).Select(m => $"{Path.GetFileName(file)} : {m.Groups[1].Value}")).ToList();

        Assert.True(offenders.Count == 0, "Nom posé sur un panneau : " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Every_dynamic_resource_exists()
    {
        // Une clé mal écrite ne se voit pas à la compilation : la couleur manque sans bruit. Restent le thème Fluent et les pastels.
        string[] fluent = ["AccentTextFillColorPrimaryBrush", "ApplicationBackgroundBrush", "CardBackgroundFillColorDefaultBrush", "CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush", "TextFillColorPrimaryBrush"];
        var files = XamlFiles().ToList();
        var defined = files.SelectMany(f => Key().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Concat(fluent).ToHashSet(StringComparer.Ordinal);
        var code = CSharp();
        var missing = files
            .SelectMany(f => DynamicResourceKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(k => !defined.Contains(k) && !code.Contains($"[\"{k}\"]", StringComparison.Ordinal) && !PastelKey().IsMatch(k))
            .ToList();

        Assert.True(missing.Count == 0, "Ressources dynamiques absentes : " + string.Join(", ", missing));
    }

    [Fact]
    public void Visible_texts_go_through_the_translations()
    {
        // Admis : icônes, chiffres et lettres seules (étapes, logo), raccourcis, et ce qui se lit dans toutes les langues.
        var offenders = XamlFiles()
            .SelectMany(file => LiteralText().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Text: m.Groups[2].Value)))
            .Where(x => !x.Text.StartsWith("&#x", StringComparison.Ordinal) && x.Text.Length > 1 && !x.Text.StartsWith("Ctrl+", StringComparison.Ordinal)
                && x.Text != "MAUS" && !x.Text.StartsWith("Language", StringComparison.Ordinal))
            .Select(x => $"{x.File} : {x.Text}")
            .ToList();

        Assert.True(offenders.Count == 0, "Textes écrits en dur : " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Background_tasks_are_never_discarded_silently()
    {
        // « _ = XxxAsync() » perdait l'erreur de la tâche sans rien afficher (liste des audits hebdomadaires qui montrait un
        // jour alors qu'aucune tâche n'existait). On écrit « XxxAsync().Forget(…) » : l'erreur est notée et affichée.
        var offenders = Directory.EnumerateFiles(AppFolder(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => DiscardedTask().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)} : {m.Value.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Tâches lancées sans surveiller leur erreur : " + string.Join(" | ", offenders));
    }

    [GeneratedRegex(@"\b_\s*=\s*(Task\.Run\b|[\w.]+Async\s*\()")]
    private static partial Regex DiscardedTask();

    [GeneratedRegex(@"<(Button|RadioButton|ToggleButton|TabItem|Expander|DataGrid|ListBox|ListView|ProgressBar)(?![.\w])([^>]*)>", RegexOptions.Singleline)]
    private static partial Regex UnnamedCandidate();

    [GeneratedRegex(@"<(Border|DockPanel|StackPanel|Grid|WrapPanel|UniformGrid)(?![.\w])[^>]*AutomationProperties\.Name", RegexOptions.Singleline)]
    private static partial Regex NamedPanel();

    [GeneratedRegex(@"\{DynamicResource\s+([\w.]+)\s*\}")]
    private static partial Regex DynamicResourceKey();

    [GeneratedRegex(@"^Pastel(Blue|Rose|Mint|Sand|Lilac|Aqua|Grey|Page)(Stroke|Ink)?$")]
    private static partial Regex PastelKey();

    [GeneratedRegex(@"\b(Text|Content|Header|ToolTip|Title)=""([^""{}]*\w[^""{}]*)""")]
    private static partial Regex LiteralText();

    [GeneratedRegex(@"<(ComboBox|CheckBox|TextBox|Slider|PasswordBox)\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex InputElement();

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex Key();

    [GeneratedRegex(@"\{StaticResource\s+([\w.]+)\s*\}")]
    private static partial Regex StaticResource();

    [GeneratedRegex(@"\{x:Static\s+app:Ui\.(\w+)\s*\}")]
    private static partial Regex UiStatic();

    [GeneratedRegex(@"DataType=""\{x:Type\s+\w+:(\w+)\}""")]
    private static partial Regex DataType();

    [GeneratedRegex(@"\b(IsChecked|SelectedItem|SelectedIndex|SelectedValue|IsExpanded|Value|Binding)=""\{Binding\s+([\w.]+)(?![^}]*Mode=OneWay)[^}]*\}""")]
    private static partial Regex TwoWayBinding();

    [GeneratedRegex(@"IsEnabled=""\{Binding (?:Source=\{x:Static [^}]+\}, Path=)?([\w.]+)")]
    private static partial Regex EnabledBinding();

    [GeneratedRegex(@"(?<!WhyDisabled\.Explain=""True"" )IsEnabled=""\{Binding ")]
    private static partial Regex NotExplained();
}
