using System.Collections.ObjectModel;
using Maus.Core.Follow;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Carnet de santé du PC (page « Suivi ») : une ligne à chaque audit, relue quand la carte s'affiche. Aucune lecture de Windows ici.</summary>
public sealed partial class MainViewModel
{
    private string _healthLogMessage = string.Empty;
    private string _healthLogLastLine = string.Empty;

    public ObservableCollection<HealthLogRow> HealthLogRows { get; } = [];

    /// <summary>Ce qui reste vide dans le carnet, et pourquoi.</summary>
    public ObservableCollection<string> HealthLogNotes { get; } = [];

    /// <summary>Carnet vide ou illisible : ce qu'il en est, et ce qu'il faut faire.</summary>
    public string HealthLogMessage
    {
        get => _healthLogMessage;
        private set
        {
            if (SetProperty(ref _healthLogMessage, value))
            {
                OnPropertyChanged(nameof(HasHealthLogMessage));
            }
        }
    }

    public bool HasHealthLogMessage => HealthLogMessage.Length > 0;

    public string HealthLogLastLine
    {
        get => _healthLogLastLine;
        private set => SetProperty(ref _healthLogLastLine, value);
    }

    /// <summary>Relit le carnet, hors du fil de la fenêtre. Un fichier abîmé est dit tel quel : rien n'est affiché à sa place.</summary>
    public async Task RefreshHealthLogAsync()
    {
        if (_histories.HealthLog is not { } store)
        {
            return;
        }

        var (read, entries) = await Task.Run(() => (store.TryLoad(out var kept), kept));
        var summary = HealthLogSummary.Build(entries, hasBattery: _lastContext?.Hardware is { } pc && (pc.HasBattery || pc.IsLaptop));
        HealthLogRows.Clear();
        HealthLogNotes.Clear();
        foreach (var row in summary.Rows)
        {
            HealthLogRows.Add(row);
        }

        foreach (var note in summary.Notes)
        {
            HealthLogNotes.Add(note);
        }

        HealthLogLastLine = summary.LastLine;
        HealthLogMessage = !read
            ? T("Le carnet n'a pas pu être relu : rien n'est affiché plutôt qu'un chiffre faux. Le prochain audit repart d'une nouvelle ligne, une copie du fichier abîmé reste à côté.")
            : entries.Count == 0
                ? T("Le carnet est vide. Il se remplit tout seul à chaque audit : lancez-en un depuis l'accueil. Sans les droits administrateur, MAUS lit peu de chose et la première ligne peut manquer.")
                : string.Empty;
    }

    /// <summary>Après un audit : note ce qu'il a pu lire. Un carnet non écrit ne gêne jamais l'audit.</summary>
    private async Task RecordHealthLogAsync(bool elevated, bool readOnly = false)
    {
        if (_histories.HealthLog is not { } store)
        {
            return;
        }

        if (readOnly)
        {
            await RefreshHealthLogAsync();
            return;
        }

        var results = _results;
        await Task.Run(() =>
        {
            try
            {
                store.AddAudit(results, DateTimeOffset.Now, weekly: false, elevated);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Maus.Core.Diagnostics.Breadcrumbs.Add("carnet de santé non écrit");
            }
        });
        await RefreshHealthLogAsync();
    }
}
