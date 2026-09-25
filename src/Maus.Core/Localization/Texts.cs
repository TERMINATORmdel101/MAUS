using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Maus.Core.Localization;

/// <summary>
/// Traductions de MAUS. Le français est la langue source : chaque texte est écrit en français dans le code,
/// et <see cref="T(string)"/> renvoie sa traduction dans la langue choisie (anglais, espagnol), ou le français
/// si la traduction manque encore. Les traductions sont dans <c>Localization/i18n/&lt;langue&gt;.json</c>.
/// </summary>
public static class Texts
{
    /// <summary>Langues proposées : code ISO et nom dans sa propre langue.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("fr", "Français"),
        ("en", "English"),
        ("es", "Español"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static FrozenDictionary<string, string> _current = FrozenDictionary<string, string>.Empty;

    /// <summary>Code de la langue active (« fr », « en » ou « es »).</summary>
    public static string Language { get; private set; } = "fr";

    /// <summary>Culture utilisée pour les nombres et les dates dans la langue active.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Langue de Windows si MAUS la parle, sinon l'anglais.</summary>
    public static string FromWindows() => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName switch
    {
        "fr" => "fr",
        "es" => "es",
        _ => "en",
    };

    /// <summary>Active une langue (<c>null</c> ou inconnue : langue de Windows).</summary>
    public static void Use(string? code)
    {
        code = Languages.Any(l => l.Code == code) ? code! : FromWindows();
        _current = code == "fr" ? FrozenDictionary<string, string>.Empty : Load(code).ToFrozenDictionary();
        Language = code;
        Culture = CultureInfo.GetCultureInfo(code switch
        {
            "en" => "en-US",
            "es" => "es-ES",
            _ => "fr-FR",
        });
    }

    /// <summary>Traduction d'un texte français (le texte lui-même si la traduction manque).</summary>
    public static string T(string french) => _current.TryGetValue(french, out var translated) ? translated : french;

    /// <summary>Traduction d'un texte facultatif (catalogues JSON) : <c>null</c> reste <c>null</c>.</summary>
    public static string? Optional(string? french) => french is null ? null : T(french);

    /// <summary>Traduction d'un modèle français à trous ({0}, {1}…), puis remplissage dans la culture de la langue active.</summary>
    public static string T(string french, params object?[] args) => string.Format(Culture, T(french), args);

    /// <summary>Toutes les traductions d'une langue (pour les tests de couverture).</summary>
    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        var resource = $"Maus.Core.Localization.i18n.{code}.json";
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOptions);
        return (entries ?? []).Where(e => !string.IsNullOrEmpty(e.Value)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }
}
