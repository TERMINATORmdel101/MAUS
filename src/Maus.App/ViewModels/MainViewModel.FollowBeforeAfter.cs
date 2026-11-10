using System.Collections.ObjectModel;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Follow;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Avant et après (page « Suivi ») : les démarrages du carnet de part et d'autre de chaque séance du journal. Rien n'est mesuré ici, aucune autorisation n'est demandée.</summary>
public sealed partial class MainViewModel
{
    private string _beforeAfterMessage = string.Empty;
    private string _beforeAfterNote = string.Empty;

    public ObservableCollection<BeforeAfterRow> BeforeAfterRows { get; } = [];

    /// <summary>Rien à comparer, ou lecture impossible : ce qu'il en est, et ce qu'il faut faire.</summary>
    public string BeforeAfterMessage
    {
        get => _beforeAfterMessage;
        private set
        {
            if (SetProperty(ref _beforeAfterMessage, value))
            {
                OnPropertyChanged(nameof(HasBeforeAfterMessage));
            }
        }
    }

    public bool HasBeforeAfterMessage => BeforeAfterMessage.Length > 0;

    /// <summary>Séances non lues ou non montrées.</summary>
    public string BeforeAfterNote
    {
        get => _beforeAfterNote;
        private set
        {
            if (SetProperty(ref _beforeAfterNote, value))
            {
                OnPropertyChanged(nameof(HasBeforeAfterNote));
            }
        }
    }

    public bool HasBeforeAfterNote => BeforeAfterNote.Length > 0;

    /// <summary>Relit le journal, si cela ne demande rien, et le carnet. Sans l'un ou l'autre, aucun chiffre : la carte dit pourquoi.</summary>
    public async Task RefreshBeforeAfterAsync()
    {
        if (_histories.HealthLog is not { } store || IsWriting)
        {
            return;
        }

        var (sessions, notice) = await ReadJournalWithoutAskingAsync();
        if (sessions is null)
        {
            ShowBeforeAfter([], notice ?? T("Le journal des corrections n'est pas lisible sans droits administrateur : rien n'est comparé, et MAUS ne demande aucune autorisation depuis cette carte. Cliquez sur « Toutes les fonctionnalités » depuis l'accueil, puis revenez ici."), string.Empty);
            return;
        }

        var (read, entries) = await Task.Run(() => (store.TryLoad(out var kept), kept));
        if (!read)
        {
            ShowBeforeAfter([], T("Le carnet de santé n'a pas pu être relu : rien n'est comparé plutôt qu'un chiffre faux."), notice ?? string.Empty);
            return;
        }

        var summary = BeforeAfter.Build(sessions, HealthLog.BootTimes(entries));
        var older = summary.Older > 0 ? Plural(summary.Older, T("{0} séance plus ancienne n'est pas affichée : elle reste dans l'Historique."), T("{0} séances plus anciennes ne sont pas affichées : elles restent dans l'Historique.")) : null;

        // Une seule fois sur la carte, même si plusieurs séances manquent de démarrages.
        var howToFill = summary.Rows.Any(r => r.Verdict == BeforeAfterVerdict.NotEnough) ? T("Le carnet de santé note les démarrages à chaque audit avec « Toutes les fonctionnalités ».") : null;
        ShowBeforeAfter(
            summary.Rows,
            summary.Rows.Count == 0 ? T("Aucune correction appliquée pour l'instant : il n'y a rien à comparer. Après une séance de corrections, cette carte montre les démarrages d'avant et d'après.") : string.Empty,
            string.Join(Environment.NewLine, new[] { howToFill, notice, older }.OfType<string>()));
    }

    private void ShowBeforeAfter(IReadOnlyList<BeforeAfterRow> rows, string message, string note)
    {
        BeforeAfterRows.Clear();
        foreach (var row in rows)
        {
            BeforeAfterRows.Add(row);
        }

        BeforeAfterMessage = message;
        BeforeAfterNote = note;
    }

    /// <summary>
    /// Sans le moteur administrateur, le lire déclencherait l'invite de Windows : on s'en tient alors aux séances déjà lues depuis
    /// l'ouverture de MAUS. Rien de lu et rien de connu : pas de liste (<c>null</c>), jamais une liste vide qui ferait croire à un journal vide.
    /// </summary>
    private async Task<(IReadOnlyList<JournalSession>? Sessions, string? Notice)> ReadJournalWithoutAskingAsync()
    {
        try
        {
            if (await BrowseJournalIfRunningAsync() is { } listing)
            {
                return (listing.Sessions, listing.UnreadableNotice);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or EngineException)
        {
            return (null, T("Le journal des corrections n'a pas pu être lu : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
        }

        return _journalRead && (Sessions.Count > 0 || _journalNotice is null)
            ? ([.. Sessions.Select(s => s.Session)], _journalNotice)
            : (null, null);
    }
}
