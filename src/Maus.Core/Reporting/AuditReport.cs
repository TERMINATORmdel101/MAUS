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

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AuditReport Create(AuditContext context, IReadOnlyList<ModuleResult> modules) => new(
        context.Now,
        typeof(AuditReport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        context.Windows,
        context.Hardware,
        context.IsElevated,
        modules);
}
