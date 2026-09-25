using System.Globalization;
using System.Windows.Input;
using Maus.Core.Fixes;
using Maus.Core.Reporting;

namespace Maus.App.ViewModels;

/// <summary>Une correction proposée, avec sa case à cocher.</summary>
public sealed class ChangeViewModel(PlannedChange change) : ObservableObject
{
    private bool _isSelected = change.Recommended && !change.Advanced;

    public PlannedChange Change { get; } = change;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Title => Change.Title;

    public string Module => Change.ModuleId;

    public string Description => Change.Description;

    public string? Gain => Change.Gain is null ? null : "Gain : " + Change.Gain;

    public bool HasGain => Change.Gain is not null;

    public string? Risk => Change.Risk is null ? null : "À savoir : " + Change.Risk;

    public bool HasRisk => Change.Risk is not null;

    public string? Warning => Change.Warning is null ? null : "Attention : " + Change.Warning;

    public bool HasWarning => Change.Warning is not null;

    public string Badges => string.Join("  ·  ", new[]
    {
        Change.ModuleId,
        Change.Advanced ? "Avancé" : Change.Recommended ? "Recommandé" : "Au choix",
        Change.Effect == ChangeEffect.Immediate ? null : Labels.Of(Change.Effect),
    }.Where(b => b is not null));

    /// <summary>Aperçu technique : chaque valeur écrite, pour les curieux et les vérifications.</summary>
    public string Technical => string.Join(Environment.NewLine, Change.Writes.Select(w =>
        $"{w.Key.Describe()}  →  {(w.Value is null ? "supprimée" : SettingValue.Display(w.Value))}"));
}

/// <summary>Une séance du journal, avec son bouton « Annuler » et une ligne par correction.</summary>
public sealed class SessionViewModel
{
    public SessionViewModel(JournalSession session, Func<SessionViewModel, string?, Task> revert)
    {
        Session = session;
        RevertCommand = new AsyncCommand(() => revert(this, null));
        Changes = session.Entries
            .Where(e => e.State is not EntryState.Pending)
            .GroupBy(e => e.ChangeId)
            .Select(g => new SessionChangeViewModel(g.ToList(), () => revert(this, g.Key)))
            .ToList();
    }

    public JournalSession Session { get; }

    public ICommand RevertCommand { get; }

    public IReadOnlyList<SessionChangeViewModel> Changes { get; }

    public bool CanRevert => Session.CanRevert;

    public string Header =>
        $"{Session.CreatedAt.ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm", CultureInfo.CurrentCulture)}  ·  " +
        (Session.RevertedAt is not null ? "annulée" : Session.CanRevert ? "active" : "rien à annuler");

    public string Summary
    {
        get
        {
            var applied = Session.Entries.Count(e => e.State is EntryState.Applied);
            var reverted = Session.Entries.Count(e => e.State is EntryState.Reverted);
            var point = Session.RestorePoint is { } rp
                ? $"point de restauration n° {rp.SequenceNumber}"
                : "sans point de restauration";
            return $"{applied} valeur(s) en place, {reverted} restaurée(s) · {point}";
        }
    }
}

/// <summary>Une correction d'une séance, avec son propre bouton « Annuler ».</summary>
public sealed class SessionChangeViewModel
{
    private readonly IReadOnlyList<JournalEntry> _entries;

    public SessionChangeViewModel(IReadOnlyList<JournalEntry> entries, Func<Task> revert)
    {
        _entries = entries;
        RevertCommand = new AsyncCommand(revert);
    }

    public ICommand RevertCommand { get; }

    public string Title => _entries[0].ChangeTitle;

    public bool CanRevert => _entries.Any(e => e.State == EntryState.Applied);

    public string State => _entries.Select(e => e.State).Distinct().ToList() switch
    {
        [EntryState.Applied] => "en place",
        [EntryState.Reverted] => "annulée",
        [EntryState.RevertSkipped] => "modifiée depuis",
        [EntryState.Failed] => "échec, origine remise",
        _ => "en partie annulée",
    };

    public string? Note => _entries.Select(e => e.EffectNote).FirstOrDefault(n => n is not null);

    public bool HasNote => Note is not null;

    public string Details => string.Join(Environment.NewLine, _entries.Select(e =>
        $"{e.Key.Describe()} = {SettingValue.Display(e.Before)} → {SettingValue.Display(e.After)}"));
}
