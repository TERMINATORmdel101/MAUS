using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maus.Core.Rules;

/// <summary>
/// Catalogues JSON embarqués dans l'assemblage (dossier <c>Catalog</c>).
/// Ils seront plus tard remplacés par la version signée téléchargée chaque mois, avec repli sur ceux-ci.
/// </summary>
public static class EmbeddedCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Charge <c>Catalog/&lt;fileName&gt;</c>, par exemple <c>audit-rules.json</c>.</summary>
    public static T Load<T>(string fileName)
    {
        var resourceName = $"Maus.Core.Catalog.{fileName}";
        using var stream = typeof(EmbeddedCatalog).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Catalogue embarqué introuvable : {resourceName}");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Catalogue vide : {resourceName}");
    }

    public static IReadOnlyList<RegistryRule> LoadRegistryRules(string fileName) => Load<List<RegistryRule>>(fileName);
}
