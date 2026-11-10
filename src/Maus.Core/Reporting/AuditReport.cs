using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maus.Core.Hardware;
using Maus.Core.Platform;

namespace Maus.Core.Reporting;

/// <summary>Rapport d'audit complet, sérialisable pour le rapport avant/après.</summary>
public sealed record AuditReport(
    DateTimeOffset CreatedAt,
    string MausVersion,
    WindowsInfo Windows,
    HardwareProfile Hardware,
    bool IsElevated,
    IReadOnlyList<ModuleResult> Modules)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="privacy">Masque chaque valeur de texte, jamais les noms de champs ni les états ; <c>null</c> : rien n'est masqué.</param>
    public string ToJson(PrivacyFilter? privacy = null)
    {
        var exported = WithoutScreenOnlyNotes();
        if (privacy is null)
        {
            return JsonSerializer.Serialize(exported, JsonOptions);
        }

        // Export demandé à la main, avec le filtre de cet export : rien à réutiliser d'une fois sur l'autre.
#pragma warning disable CA1869
        var options = new JsonSerializerOptions(JsonOptions);
#pragma warning restore CA1869
        options.Converters.Add(new MaskedText(privacy));
        return JsonSerializer.Serialize(exported, options);
    }

    /// <summary>Ce qui ne doit rester qu'à l'écran ne sort jamais dans un fichier.</summary>
    private AuditReport WithoutScreenOnlyNotes() => this with
    {
        Modules = [.. Modules.Select(m => m with { Findings = [.. m.Findings.Select(f => f with { ScreenOnlyNote = null })] })],
    };

    private sealed class MaskedText(PrivacyFilter privacy) : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(privacy.Mask(value));
    }

    /// <summary>Rapport d'après ce que le moteur a dit du PC (interface sans accès direct au système).</summary>
    public static AuditReport Create(Engine.Broker.EngineSnapshot snapshot, IReadOnlyList<ModuleResult> modules)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot.Now, AppVersion.Display, snapshot.Windows, snapshot.Hardware, snapshot.IsElevated, modules);
    }

    public static AuditReport Create(AuditContext context, IReadOnlyList<ModuleResult> modules)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
        context.Now,
        AppVersion.Display,
        context.Windows,
        context.Hardware,
        context.IsElevated,
        modules);
    }
}
