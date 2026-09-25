using Maus.Core.Rules;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M03Updates;

/// <summary>Canal de maintenance, qui fixe la date de fin des correctifs d'une même version.</summary>
internal enum ServicingChannel
{
    HomePro,
    EnterpriseEducation,
    Ltsc,
    IotLtsc,
}

/// <summary>Catalogue <c>m03-windows-lifecycle.json</c> : fin de maintenance de chaque version de Windows 11.</summary>
internal sealed class WindowsLifecycleCatalog
{
    public const string FileName = "m03-windows-lifecycle.json";

    public string Reviewed { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public int MinimumSupportedBuild { get; init; }

    public DateOnly Windows10EndOfSupport { get; init; }

    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    public static WindowsLifecycleCatalog LoadEmbedded() => EmbeddedCatalog.Load<WindowsLifecycleCatalog>(FileName);

    public WindowsRelease? Find(int build) => Releases.FirstOrDefault(r => r.Build == build);

    /// <summary>Build le plus récent du catalogue : au-delà, il s'agit d'une préversion (Insider) ou d'une version plus récente que le catalogue.</summary>
    public int NewestBuild => Releases.Count == 0 ? 0 : Releases.Max(r => r.Build);

    /// <summary>Canal d'après <c>EditionID</c> : les éditions « S » sont les LTSC, Pro Éducation reste dans le canal Famille et Pro.</summary>
    public static ServicingChannel Classify(string editionId)
    {
        if (editionId.StartsWith("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase))
        {
            return ServicingChannel.IotLtsc;
        }

        if (editionId.StartsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase))
        {
            return ServicingChannel.Ltsc;
        }

        return editionId.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
            || editionId.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase)
            || editionId.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
            || editionId.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase)
                ? ServicingChannel.EnterpriseEducation
                : ServicingChannel.HomePro;
    }

    public static string Describe(ServicingChannel channel) => channel switch
    {
        ServicingChannel.HomePro => "Famille et Pro",
        ServicingChannel.EnterpriseEducation => T("Entreprise et Éducation"),
        ServicingChannel.Ltsc => "Entreprise LTSC",
        _ => "IoT Entreprise LTSC",
    };
}

/// <summary>Une version de Windows 11 et ses dates de fin de correctifs (dernier jour inclus).</summary>
internal sealed class WindowsRelease
{
    public int Build { get; init; }

    public string Version { get; init; } = string.Empty;

    public DateOnly HomePro { get; init; }

    public DateOnly EnterpriseEducation { get; init; }

    public DateOnly? Ltsc { get; init; }

    public DateOnly? IotLtsc { get; init; }

    /// <summary>Version livrée seulement sur des PC neufs (26H1) : jamais proposée en mise à jour d'un PC existant.</summary>
    public bool NewDevicesOnly { get; init; }

    /// <summary>Package d'activation vers la version suivante (par exemple KB5054156 de 24H2 vers 25H2).</summary>
    public string? EnablementPackage { get; init; }

    public string? EnablementTarget { get; init; }

    /// <summary>Révision (UBR) minimale pour installer le package d'activation.</summary>
    public int? EnablementMinimumUbr { get; init; }

    /// <summary>Fin des correctifs pour ce canal ; une LTSC sans date propre retombe sur Entreprise et Éducation.</summary>
    public DateOnly EndOfService(ServicingChannel channel) => channel switch
    {
        ServicingChannel.HomePro => HomePro,
        ServicingChannel.IotLtsc => IotLtsc ?? Ltsc ?? EnterpriseEducation,
        ServicingChannel.Ltsc => Ltsc ?? EnterpriseEducation,
        _ => EnterpriseEducation,
    };
}
