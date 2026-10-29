#:property RestorePackagesWithLockFile=false
// Outil de traduction de MAUS : liste les textes à traduire, ou fusionne des traductions. Programme C# d'un seul fichier
// (.NET 10), lancé depuis la racine du dépôt :
//
//   dotnet run tools/i18n.cs -- missing en            textes source sans traduction anglaise (JSON)
//   dotnet run tools/i18n.cs -- merge es fichier.json  ajoute des traductions (clé française -> texte)
//   dotnet run tools/i18n.cs -- prune en               retire les traductions dont le texte source a disparu
//
// Remplace l'ancien tools/i18n.py (12/10/2026, demande du porteur : moins de langages) ; même sortie, octet pour octet.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 2 || args[0] is not ("missing" or "merge" or "prune") || (args[0] == "merge" && args.Length < 3))
{
    Console.Error.WriteLine("Usage : dotnet run tools/i18n.cs -- missing|prune <langue> | merge <langue> <fichier.json>");
    return 2;
}

var root = FindRoot(Directory.GetCurrentDirectory());
var i18n = Path.Combine(root, "src", "Maus.Core", "Localization", "i18n");
var (command, lang) = (args[0], args[1]);
var data = Load(Path.Combine(i18n, lang + ".json"));
switch (command)
{
    case "missing":
        var missing = Sources(root).Where(t => !data.ContainsKey(t)).ToList();
        missing.Sort(CodePointComparer.Instance);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Out.Write(JsonList(missing) + "\n");
        break;
    case "merge":
        foreach (var (key, value) in Load(args[2]))
        {
            data[key] = value;
        }

        Save(Path.Combine(i18n, lang + ".json"), data);
        break;
    default:
        var keep = Sources(root);
        Save(Path.Combine(i18n, lang + ".json"), data.Where(p => keep.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        break;
}

return 0;

static string FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Maus.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Lancez l'outil depuis le dépôt de MAUS (Maus.slnx introuvable).");
}

// Textes passés à T("…") dans le code (morceaux "a" + "b" recollés), plus les champs traduits des catalogues JSON.
static HashSet<string> Sources(string root)
{
    var call = new Regex("""(?:(?<![\w.])|(?<=Texts\.))T\(\s*("(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)""");
    var piece = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
    var found = new HashSet<string>(StringComparer.Ordinal);
    var sep = Path.DirectorySeparatorChar;
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
    {
        var dir = Path.GetDirectoryName(file)!;
        if (dir.Contains(sep + "obj", StringComparison.Ordinal) || dir.Contains(sep + "bin", StringComparison.Ordinal))
        {
            continue;
        }

        foreach (Match m in call.Matches(File.ReadAllText(file, Encoding.UTF8)))
        {
            var literal = string.Concat(piece.Matches(m.Groups[1].Value).Select(p => p.Groups[1].Value));
            found.Add(literal.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal));
        }
    }

    found.UnionWith(CatalogTexts(root));
    return found;
}

// Catalogues JSON traduits et leurs champs de texte (« [] » = chaque élément d'une liste).
// Même liste dans tests/Maus.Core.Tests/Localization/TranslationCoverageTests.cs.
static IEnumerable<string> CatalogTexts(string root)
{
    string[] rules = ["[].title", "[].explanation", "[].advice", "[].category", "[].expectedLabel", "[].fix.title", "[].fix.gain", "[].fix.risk", "[].fix.warning"];
    var catalogs = new Dictionary<string, string[]>
    {
        ["processes.json"] = ["[].category", "[].what"],
        ["m01-audit-rules.json"] = rules,
        ["m04-privacy-rules.json"] = rules,
        ["m06-visual-rules.json"] = rules,
        ["m08-bios-vendors.json"] = ["vendors[].rescueTool"],
        ["m09-gpu-drivers.json"] = ["branches[].label", "branches[].note"],
        ["m12-startup-catalog.json"] = ["families[].label", "families[].item", "families[].loses", "families[].recommendation"],
    };
    foreach (var (name, paths) in catalogs)
    {
        var lines = File.ReadAllLines(Path.Combine(root, "src", "Maus.Core", "Catalog", name), Encoding.UTF8)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(string.Join("\n", lines));
        foreach (var path in paths)
        {
            foreach (var text in Walk(document.RootElement, path.Split('.')))
            {
                yield return text;
            }
        }
    }
}

static IEnumerable<string> Walk(JsonElement node, string[] steps)
{
    if (steps.Length == 0)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            yield return node.GetString()!;
        }

        yield break;
    }

    var step = steps[0];
    var isList = step.EndsWith("[]", StringComparison.Ordinal);
    var name = isList ? step[..^2] : step;
    if (name.Length > 0)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node) || node.ValueKind == JsonValueKind.Null)
        {
            yield break;
        }
    }

    var items = isList ? (node.ValueKind == JsonValueKind.Array ? node.EnumerateArray().ToList() : []) : [node];
    foreach (var item in items)
    {
        foreach (var text in Walk(item, steps[1..]))
        {
            yield return text;
        }
    }
}

static Dictionary<string, string> Load(string path)
{
    if (!File.Exists(path))
    {
        return new(StringComparer.Ordinal);
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    foreach (var property in document.RootElement.EnumerateObject())
    {
        result[property.Name] = property.Value.GetString()!;
    }

    return result;
}

// Même écriture qu'avant : clés triées par point de code, accents gardés tels quels, deux espaces, fin de ligne finale.
static void Save(string path, Dictionary<string, string> data)
{
    var keys = data.Keys.ToList();
    keys.Sort(CodePointComparer.Instance);
    var text = new StringBuilder();
    if (keys.Count == 0)
    {
        text.Append("{}");
    }
    else
    {
        text.Append("{\n");
        for (var i = 0; i < keys.Count; i++)
        {
            text.Append("  ").Append(Quote(keys[i])).Append(": ").Append(Quote(data[keys[i]])).Append(i < keys.Count - 1 ? ",\n" : "\n");
        }

        text.Append('}');
    }

    File.WriteAllText(path, text.Append('\n').ToString(), new UTF8Encoding(false));
}

static string JsonList(List<string> items) =>
    items.Count == 0 ? "[]" : "[\n" + string.Join(",\n", items.Select(i => "  " + Quote(i))) + "\n]";

static string Quote(string s)
{
    var text = new StringBuilder("\"");
    foreach (var c in s)
    {
        text.Append(c switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\b' => "\\b",
            '\f' => "\\f",
            < ' ' => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        });
    }

    return text.Append('"').ToString();
}

// Ordre des points de code Unicode (et non des unités UTF-16), comme le tri des fichiers existants.
sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = (x ?? string.Empty).EnumerateRunes().GetEnumerator();
        var b = (y ?? string.Empty).EnumerateRunes().GetEnumerator();
        while (true)
        {
            var (hasA, hasB) = (a.MoveNext(), b.MoveNext());
            if (!hasA || !hasB)
            {
                return hasA ? 1 : hasB ? -1 : 0;
            }

            var diff = a.Current.Value - b.Current.Value;
            if (diff != 0)
            {
                return diff;
            }
        }
    }
}
