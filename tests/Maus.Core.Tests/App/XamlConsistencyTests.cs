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
}
