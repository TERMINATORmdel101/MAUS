using System.IO;
using System.Windows.Input;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;
using Maus.Core.Follow;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une ligne affichée de « Mon PC a planté ».</summary>
public sealed record CrashTimelineRow(string When, string Kind, string What, bool IsCrash)
{
    public override string ToString() => What;
}

/// <summary>Textes fixes de la carte « Mon PC a planté ».</summary>
public static class CrashTimelineUi
{
    public static string Title => T("Mon PC a planté");

    public static string Intro => T("Après un écran bleu ou un redémarrage inattendu : les 30 derniers jours sur une seule liste (écrans bleus, bouton d'alimentation, arrêts non expliqués, arrêts et redémarrages demandés, mises à jour, pilotes, corrections de MAUS). Rien n'est lu avant votre clic. Un jeu qui se ferme sans écran bleu ni arrêt du PC ne laisse pas de ligne ici.");

    public static string CopyLabel => T("Copier pour un réparateur");

    public static string FindingsLabel => T("Voir les constats de la santé de Windows");

    public static string Caution => CrashTimeline.Caution;

    public static string TimeNote => CrashTimeline.CrashTimeNote;

    public static string RequestNote => CrashTimeline.RequestNote;
}

/// <summary>
/// Carte « Mon PC a planté » de la page Suivi : rien n'est lu avant le clic. Les journaux se lisent sans droits, hors du fil de la
/// fenêtre ; le journal des corrections passe par le moteur s'il tourne, et sinon il est dit « non lu », sans demander d'autorisation.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Lignes montrées d'abord : la page porte d'autres cartes. La copie donne toujours tout.</summary>
    private const int CrashTimelineShown = 30;

    private CrashTimeline? _crashTimeline;
    private bool _crashTimelineShowAll;
    private string _crashTimelineStatus = string.Empty;
    private ICommand? _readCrashTimeline;
    private ICommand? _copyCrashTimeline;
    private ICommand? _showAllCrashTimeline;
    private ICommand? _crashTimelineFindings;

    /// <summary>La lecture elle-même ; remplaçable pour les tests.</summary>
    internal Func<CrashTimelineJournal, CrashTimeline> CrashTimelineSource { get; set; } =
        journal => CrashTimelineReader.Read(new WindowsEventLogReader(), new WmiCimReader(), journal, DateTimeOffset.Now);

    /// <summary>La copie, masquée ; remplaçable pour que les tests ne touchent pas au presse-papiers du PC.</summary>
    internal Func<string, bool> CrashTimelineClipboard { get; set; } = text => ClipboardText.TryCopy(text);

    public string CrashTimelineReadLabel => _crashTimeline is null ? T("Lire les 30 derniers jours") : T("Relire");

    public string CrashTimelineStatus
    {
        get => _crashTimelineStatus;
        private set
        {
            _crashTimelineStatus = value;
            OnPropertyChanged();
        }
    }

    public bool HasCrashTimeline => _crashTimeline is not null;

    public bool HasCrashTimelineLines => _crashTimeline is { Lines.Count: > 0 };

    public IReadOnlyList<string> CrashTimelineSections => _crashTimeline?.Sections.Select(s => s.Text).ToList() ?? [];

    public IReadOnlyList<CrashTimelineRow> CrashTimelineRows => (_crashTimeline?.FirstLines(_crashTimelineShowAll ? int.MaxValue : CrashTimelineShown) ?? [])
        .Select(l => new CrashTimelineRow(l.WhenText, l.KindText, l.What, l.IsCrash))
        .ToList();

    public bool CanShowAllCrashTimeline => !_crashTimelineShowAll && _crashTimeline is { } timeline && timeline.Lines.Count > CrashTimelineShown;

    public string CrashTimelineShowAllLabel => T("Voir les {0} lignes", _crashTimeline?.Lines.Count ?? 0);

    public ICommand ReadCrashTimelineCommand => _readCrashTimeline ??= new AsyncCommand(ReadCrashTimelineAsync);

    public ICommand CopyCrashTimelineCommand => _copyCrashTimeline ??= new AsyncCommand(() =>
    {
        if (_crashTimeline is { } timeline)
        {
            CrashTimelineStatus = CrashTimelineClipboard(timeline.ToText())
                ? T("Ligne du temps copiée (nom d'utilisateur, nom du PC et e-mails masqués) : collez-la avec Ctrl+V.")
                : ClipboardText.Busy;
        }
    });

    public ICommand ShowAllCrashTimelineCommand => _showAllCrashTimeline ??= new AsyncCommand(() =>
    {
        _crashTimelineShowAll = true;
        OnPropertyChanged(nameof(CrashTimelineRows));
        OnPropertyChanged(nameof(CanShowAllCrashTimeline));
    });

    public ICommand CrashTimelineFindingsCommand => _crashTimelineFindings ??= new ParameterCommand(_ => ShowModuleFindings("M02"));

    internal async Task ReadCrashTimelineAsync()
    {
        CrashTimelineStatus = T("Lecture des journaux de Windows…");
        try
        {
            var journal = await ReadCrashTimelineJournalAsync();
            var source = CrashTimelineSource;
            _crashTimeline = await Task.Run(() => source(journal));
            _crashTimelineShowAll = false;
            CrashTimelineStatus = _crashTimeline.Summary;
        }
        catch (Exception ex)
        {
            // Une ancienne lecture ne doit pas passer pour celle-ci.
            CrashLog.Write(ex, "ligne du temps");
            _crashTimeline = null;
            CrashTimelineStatus = T("La ligne du temps n'a pas pu être lue : {0}", ErrorText.For(ex));
        }

        OnPropertyChanged(nameof(CrashTimelineReadLabel));
        OnPropertyChanged(nameof(HasCrashTimeline));
        OnPropertyChanged(nameof(HasCrashTimelineLines));
        OnPropertyChanged(nameof(CrashTimelineSections));
        OnPropertyChanged(nameof(CrashTimelineRows));
        OnPropertyChanged(nameof(CanShowAllCrashTimeline));
        OnPropertyChanged(nameof(CrashTimelineShowAllLabel));
    }

    private async Task<CrashTimelineJournal> ReadCrashTimelineJournalAsync()
    {
        try
        {
            if (await BrowseJournalIfRunningAsync() is { } listing)
            {
                return new CrashTimelineJournal(listing.Sessions, listing.Unreadable);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EngineException)
        {
            return CrashTimelineJournal.Failed(ErrorText.For(ex));
        }

        // Moteur arrêté : le relire demanderait une autorisation. Ce que l'Historique a déjà lu reste valable.
        return _journalRead && _journalNotice is null
            ? new CrashTimelineJournal(Sessions.Select(s => s.Session).ToList())
            : CrashTimelineJournal.WithoutRights;
    }
}
