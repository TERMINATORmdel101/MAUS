using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M20Software;

/// <summary>
/// Module 20 — Mises à jour des logiciels : la liste de <c>winget upgrade</c> (catalogue communautaire de Microsoft),
/// en lecture seule. La mise à jour se fait à la demande, logiciel par logiciel, dans une fenêtre visible.
/// </summary>
public sealed class SoftwareUpdatesModule : IAuditModule
{
    internal static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Logiciels qui ouvrent des pages ou des fichiers venus d'Internet : navigateurs, lecteurs PDF, compression, lecteurs
    /// multimédia, messageries, Java. Leurs failles sont les plus exploitées : leurs mises à jour passent en orange.
    /// </summary>
    internal static readonly string[] ExposedPrefixes =
    [
        "Mozilla.Firefox", "Google.Chrome", "Microsoft.Edge", "Brave.Brave", "Opera.", "Vivaldi.Vivaldi", "TorProject.",
        "Adobe.Acrobat", "Foxit.", "SumatraPDF.",
        "7zip.", "RARLab.WinRAR", "Giorgiosarcinelli.NanaZip", "PeaZip.",
        "VideoLAN.VLC", "PotPlayer.", "Microsoft.Teams", "Zoom.Zoom", "Discord.Discord", "Telegram.", "OpenWhisperSystems.Signal",
        "Oracle.JavaRuntimeEnvironment", "Oracle.JDK", "EclipseAdoptium.", "Notepad++.Notepad++", "Microsoft.Office", "TheDocumentFoundation.LibreOffice",
    ];

    private readonly string _programFiles;

    public SoftwareUpdatesModule()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
    {
    }

    /// <summary>Dossier Program Files injecté pour les tests (emplacement protégé de winget).</summary>
    internal SoftwareUpdatesModule(string programFiles)
    {
        _programFiles = programFiles;
    }

    private static string Category => T("Logiciels");

    public string Id => "M20";

    public string Title => T("Mises à jour des logiciels");

    public int Order => 200;

    /// <summary>winget met d'abord à jour son catalogue : compter une minute sur une connexion lente.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public async Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        const string id = "M20.software-updates";
        var title = T("Logiciels à mettre à jour");
        string? winget;
        try
        {
            winget = Winget.Locate(context.Packages, context.Files, _programFiles);
        }
        catch (MausAccessDeniedException)
        {
            return [Finding.AdminRequired(id, title, Category)];
        }

        if (winget is null)
        {
            return [Finding.Unknown(id, title, T("winget (Programme d'installation d'application de Microsoft) est absent : "
                + "réinstallez-le depuis le Microsoft Store pour que MAUS puisse lister les mises à jour des logiciels (voir aussi le Module 1)."), Category)];
        }

        IReadOnlyList<SoftwareUpdate> updates;
        try
        {
            updates = await Winget.ListUpgradesAsync(context.Commands, winget, ListTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or InvalidOperationException)
        {
            return [Finding.Unknown(id, title, T("Liste des mises à jour illisible : {0}", ex.Message), Category)];
        }

        return [Describe(id, title, updates)];
    }

    public static bool IsExposed(SoftwareUpdate update) =>
        ExposedPrefixes.Any(prefix => update.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static Finding Describe(string id, string title, IReadOnlyList<SoftwareUpdate> updates)
    {
        var exposed = updates.Where(IsExposed).ToList();
        var severity = exposed.Count > 0 ? Severity.Medium : Severity.Low;
        var listed = exposed.Concat(updates.Except(exposed)).Take(10)
            .Select(u => T("{0} ({1} → {2})", u.Name, u.Version, u.Available));
        var current = updates.Count == 0
            ? T("tous les logiciels suivis par winget sont à jour")
            : T("{0} logiciel(s) : {1}", updates.Count, string.Join(", ", listed)) + (updates.Count > 10 ? "…" : string.Empty);
        return new Finding
        {
            Id = id,
            Title = title,
            Category = Category,
            Status = updates.Count == 0 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = current,
            Expected = T("à jour"),
            Explanation = T("Les mises à jour des logiciels corrigent surtout des failles de sécurité et des bugs ; elles ne rendent pas le PC "
                + "plus rapide. Les plus importantes concernent ce qui ouvre des pages ou des fichiers venus d'Internet : navigateurs, "
                + "lecteurs PDF, logiciels de compression, lecteurs vidéo, messageries. Liste fournie par winget, l'outil de Microsoft : "
                + "les applications du Microsoft Store se mettent à jour par le Store."),
            Advice = updates.Count == 0
                ? null
                : (exposed.Count > 0
                    ? T("À faire en priorité : {0}. ", string.Join(", ", exposed.Select(u => u.Name)))
                    : string.Empty)
                  + T("Onglet Corrections, « Mettre à jour les logiciels » : vous choisissez lesquels, winget les installe dans une "
                    + "fenêtre visible. Fermez d'abord les logiciels concernés. Certains (navigateurs, Steam, Discord) se mettent aussi à jour tout seuls."),
        };
    }
}
