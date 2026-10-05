using System.Globalization;
using System.Windows.Input;
using Maus.Core.Fixes;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une idée reçue sur l'optimisation, avec un bouton vers sa source.</summary>
public sealed class MythViewModel(Maus.Core.Reporting.OptimizationMyth myth)
{
    public string Claim => myth.Claim;

    public string Truth => myth.Truth;

    public string Source => myth.Source.Host;

    public ICommand OpenSourceCommand { get; } = new AsyncCommand(() =>
    {
        ShellLauncher.OpenUrl(myth.Source);
        return Task.CompletedTask;
    });
}

/// <summary>Une correction proposée, avec sa case à cocher.</summary>
/// <param name="group">Titre du groupe dans la liste (le module : « M04 · Confidentialité et télémétrie »).</param>
public sealed class ChangeViewModel(PlannedChange change, string? group = null) : ObservableObject
{
    private bool _isSelected = change.Recommended && !change.Advanced;

    public PlannedChange Change { get; } = change;

    public string Group { get; } = group ?? change.ModuleId;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Correction choisie depuis un constat (bouton « Corriger ») : entourée pour qu'on la retrouve.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    private bool _isHighlighted;

    public string Title => Change.Title;

    public string Module => Change.ModuleId;

    public string Description => Change.Description;

    public string? Gain => Change.Gain is null ? null : T("Gain : {0}", Change.Gain);

    public bool HasGain => Change.Gain is not null;

    public string? Risk => Change.Risk is null ? null : T("À savoir : {0}", Change.Risk);

    public bool HasRisk => Change.Risk is not null;

    public string? Warning => Change.Warning is null ? null : T("Attention : {0}", Change.Warning);

    public bool HasWarning => Change.Warning is not null;

    /// <summary>Teinte de la carte : rose si avertissement, sable si avancée, vert d'eau si recommandée, bleu sinon.</summary>
    public Maus.App.Appearance.Tone Tone => Change.Warning is not null
        ? Maus.App.Appearance.Tone.Rose
        : Change.Advanced
            ? Maus.App.Appearance.Tone.Sand
            : Change.Recommended ? Maus.App.Appearance.Tone.Mint : Maus.App.Appearance.Tone.Blue;

    public string Badges => string.Join("  ·  ", new[]
    {
        Change.ModuleId,
        Change.Advanced ? T("Avancé") : Change.Recommended ? T("Recommandé") : T("Au choix"),
        Change.Effect == ChangeEffect.Immediate ? null : Labels.Of(Change.Effect),
    }.Where(b => b is not null));

    /// <summary>Aperçu technique : chaque valeur écrite, pour les curieux et les vérifications.</summary>
    public string Technical => string.Join(Environment.NewLine, Change.Writes.Select(w =>
        $"{w.Key.Describe()}  →  {(w.Value is null ? T("supprimée") : SettingValue.Display(w.Value))}"));
}

/// <summary>Une séance du journal, avec son bouton « Annuler » et une ligne par correction.</summary>
public sealed class SessionViewModel
{
    public SessionViewModel(JournalSession session, Func<SessionViewModel, string?, Task> revert)
    {
        Session = session;
        RevertCommand = new AsyncCommand(() => revert(this, null));
        Changes = session.Entries
            .GroupBy(e => e.ChangeId)
            .Select(g => new SessionChangeViewModel(g.ToList(), () => revert(this, g.Key)))
            .ToList();
    }

    public JournalSession Session { get; }

    public ICommand RevertCommand { get; }

    public IReadOnlyList<SessionChangeViewModel> Changes { get; }

    public bool CanRevert => Session.CanRevert;

    public string Header =>
        $"{Session.CreatedAt.ToLocalTime().ToString("f", Culture)}  ·  " +
        (Session.RevertedAt is not null ? T("annulée") : Session.CanRevert ? T("active") : T("rien à annuler"));

    public string Summary
    {
        get
        {
            var applied = Session.Entries.Count(e => e.State is EntryState.Applied);
            var reverted = Session.Entries.Count(e => e.State is EntryState.Reverted);
            var point = Session.RestorePoint is { } rp
                ? T("point de restauration n° {0}", rp.SequenceNumber)
                : T("sans point de restauration");
            return T("{0} valeur(s) en place, {1} restaurée(s) · {2}", applied, reverted, point);
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

    public bool CanRevert => _entries.Any(e => e.State is EntryState.Applied or EntryState.Pending);

    public string State => _entries.Select(e => e.State).Distinct().ToList() switch
    {
        [EntryState.Applied] => T("en place"),
        [EntryState.Reverted] => T("annulée"),
        [EntryState.RevertSkipped] => T("modifiée depuis"),
        [EntryState.Failed] => T("échec, origine remise"),
        [EntryState.Pending] => T("interrompue : peut-être appliquée, « Annuler » vérifie"),
        _ => T("en partie annulée"),
    };

    public string? Note => _entries.Select(e => e.EffectNote).FirstOrDefault(n => n is not null);

    public bool HasNote => Note is not null;

    public string Details => string.Join(Environment.NewLine, _entries.Select(e =>
        $"{e.Key.Describe()} = {SettingValue.Display(e.Before)} → {SettingValue.Display(e.After)}"));
}
