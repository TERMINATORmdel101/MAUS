using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maus.Core.Localization;

namespace Maus.Core.Tests.Localization;

/// <summary>
/// Chaque texte passé à <c>T("…")</c> dans le code doit avoir sa traduction anglaise et espagnole,
/// avec les mêmes emplacements {0}, {1}…
/// </summary>
public partial class TranslationCoverageTests
{
    public static TheoryData<string> Languages => ["en", "es"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_marked_for_translation_is_translated(string language)
    {
        var translations = Texts.Load(language);
        var missing = SourceTexts().Where(text => !translations.ContainsKey(text)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, $"{missing.Count} texte(s) sans traduction « {language} » :\n" + string.Join("\n", missing.Take(40)));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_the_same_placeholders(string language)
    {
        var wrong = Texts.Load(language)
            .Where(pair => !Placeholders(pair.Key).SetEquals(Placeholders(pair.Value)))
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(wrong.Count == 0, "Emplacements différents :\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void French_is_the_source_and_needs_no_file()
    {
        Assert.Empty(Texts.Load("fr"));
        Assert.Equal(["fr", "en", "es"], Texts.Languages.Select(l => l.Code));
    }

    /// <summary>
    /// Textes source : littéraux passés à T( dans src/**/*.cs (chaînes simples, éventuellement coupées en « "a" + "b" »,
    /// séquences d'échappement C# décodées).
    /// </summary>
    internal static HashSet<string> SourceTexts([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src"));
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in TCall().Matches(File.ReadAllText(file)))
            {
                texts.Add(Regex.Unescape(string.Concat(match.Groups["piece"].Captures.Select(c => c.Value))));
            }
        }

        // Catalogues JSON traduits : leurs champs de texte comptent aussi (même liste que tools/i18n.py).
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var (name, paths) in Catalogs)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Maus.Core", "Catalog", name)), options);
            foreach (var path in paths)
            {
                texts.UnionWith(Walk(document.RootElement, path.Split('.'), 0));
            }
        }

        return texts;
    }

    private static readonly string[] RuleFields =
        ["[].title", "[].explanation", "[].advice", "[].category", "[].expectedLabel", "[].fix.title", "[].fix.gain", "[].fix.risk", "[].fix.warning"];

    private static readonly Dictionary<string, string[]> Catalogs = new(StringComparer.Ordinal)
    {
        ["processes.json"] = ["[].category", "[].what"],
        ["m01-audit-rules.json"] = RuleFields,
        ["m04-privacy-rules.json"] = RuleFields,
        ["m06-visual-rules.json"] = RuleFields,
        ["m08-bios-vendors.json"] = ["vendors[].rescueTool"],
        ["m09-gpu-drivers.json"] = ["branches[].label", "branches[].note"],
        ["m12-startup-catalog.json"] = ["families[].label", "families[].item", "families[].loses", "families[].recommendation"],
    };

    /// <summary>Textes d'un chemin « a[].b.c » dans un document JSON (« [] » : chaque élément de la liste).</summary>
    private static IEnumerable<string> Walk(JsonElement node, string[] steps, int index)
    {
        if (index == steps.Length)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                yield return node.GetString()!;
            }

            yield break;
        }

        var step = steps[index];
        var isList = step.EndsWith("[]", StringComparison.Ordinal);
        var name = isList ? step[..^2] : step;
        if (name.Length > 0 && (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)))
        {
            yield break;
        }

        var items = isList && node.ValueKind == JsonValueKind.Array ? node.EnumerateArray().ToList() : [node];
        foreach (var item in items)
        {
            foreach (var text in Walk(item, steps, index + 1))
            {
                yield return text;
            }
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"(?:(?<![\w.])|(?<=Texts\.))T\(\s*""(?<piece>(?:[^""\\]|\\.)*)""(?:\s*\+\s*""(?<piece>(?:[^""\\]|\\.)*)"")*")]
    private static partial Regex TCall();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
