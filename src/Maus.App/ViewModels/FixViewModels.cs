using System.Windows.Input;
using Maus.Core.Fixes;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une idée reçue sur l'optimisation, avec un bouton vers sa source.</summary>
public sealed class MythViewModel(Maus.Core.Reporting.OptimizationMyth myth)
{
    public override string ToString() => Claim;

    public string Claim => myth.Claim;

    public string Truth => myth.Truth;

    public string Source => myth.Source.Host;

    public ICommand OpenSourceCommand { get; } = new AsyncCommand(() =>
    {
        ShellLauncher.OpenUrl(myth.Source);
    });
}

/// <summary>Une correction proposée, avec sa case à cocher.</summary>
/// <param name="group">Titre du groupe dans la liste (le module : « M04 · Confidentialité et télémétrie »).</param>
public sealed class ChangeViewModel(PlannedChange change, string? group = null) : ObservableObject
{
    public override string ToString() => Title;

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

    public string Description => Change.Description;

    /// <summary>Date de la dernière fois que cette correction a été écrite, si le journal la dit encore en place.</summary>
    public DateTimeOffset? AppliedOn
    {
        get => _appliedOn;
        set
        {
            if (SetProperty(ref _appliedOn, value))
            {
                OnPropertyChanged(nameof(HasComeBack));
                OnPropertyChanged(nameof(ComeBackNote));
            }
        }
    }

    private DateTimeOffset? _appliedOn;

    /// <summary>Déjà faite, et pourtant proposée de nouveau : le réglage est revenu, ou l'écriture n'a pas pris.</summary>
    public bool HasComeBack => AppliedOn is not null;

    public string ComeBackNote => AppliedOn is { } date
        ? T("Déjà appliquée le {0} : le réglage a été remis depuis (Windows, une mise à jour, un autre programme), ou la correction n'a pas pris.", date.ToLocalTime().ToString("d", Culture))
        : string.Empty;

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
    public override string ToString() => Header;

    private readonly string? _accountSid;

    /// <param name="session">Séance du journal.</param>
    /// <param name="revert">Annulation de la séance, ou d'une correction.</param>
    /// <param name="accountSid">Compte de cette session Windows : le journal est commun à tout le PC.</param>
    /// <param name="comeBack">Corrections que le dernier plan propose de nouveau : elles ne sont plus « en place ».</param>
    public SessionViewModel(JournalSession session, Func<SessionViewModel, string?, Task> revert, string? accountSid = null, IReadOnlySet<string>? comeBack = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        _accountSid = accountSid;
        RevertCommand = new AsyncCommand(() => revert(this, null));
        Changes = session.Entries
            .GroupBy(e => e.ChangeId)
            .Select(g => new SessionChangeViewModel(g.ToList(), () => revert(this, g.Key), session, accountSid, comeBack?.Contains(g.Key) == true))
            .ToList();
    }

    public JournalSession Session { get; }

    public ICommand RevertCommand { get; }

    public IReadOnlyList<SessionChangeViewModel> Changes { get; }

    /// <summary>Faite par un autre compte du PC : « Tout annuler » la laisse de côté, et l'écran le dit.</summary>
    public bool IsFromAnotherAccount => Session.IsFromAnotherAccount(_accountSid);

    /// <summary>Il reste quelque chose que ce compte peut remettre (les réglages du profil d'un autre compte n'en font pas partie).</summary>
    public bool CanRevert => Session.CanRevertAs(_accountSid);

    public string Header =>
        $"{Session.CreatedAt.ToLocalTime().ToString("f", Culture)}  ·  " +
        Session.StateLabel +
        (IsFromAnotherAccount ? "  ·  " + T("faite par un autre compte") : string.Empty);

    public string Summary
    {
        get
        {
            var applied = Session.Entries.Count(e => e.State is EntryState.Applied);
            var reverted = Session.Entries.Count(e => e.State is EntryState.Reverted);
            var point = Session.RestorePoint is { } rp
                ? T("point de restauration n° {0}", rp.SequenceNumber)
                : T("sans point de restauration");
            return Plural(applied, T("{0} valeur en place"), T("{0} valeurs en place")) + ", " + Plural(reverted, T("{0} restaurée"), T("{0} restaurées")) + " · " + point;
        }
    }
}

/// <summary>Une correction d'une séance, avec son propre bouton « Annuler ».</summary>
public sealed class SessionChangeViewModel
{
    public override string ToString() => Title;

    private readonly IReadOnlyList<JournalEntry> _entries;
    private readonly JournalSession _session;
    private readonly string? _accountSid;
    private readonly bool _comeBack;

    public SessionChangeViewModel(IReadOnlyList<JournalEntry> entries, Func<Task> revert, JournalSession session, string? accountSid = null, bool comeBack = false)
    {
        _entries = entries;
        _session = session;
        _accountSid = accountSid;
        _comeBack = comeBack;
        RevertCommand = new AsyncCommand(revert);
    }

    public ICommand RevertCommand { get; }

    public string Title => _entries[0].ChangeTitle;

    public bool CanRevert => _entries.Any(e => e.State.IsOutstanding() && !_session.IsInAnotherProfile(e, _accountSid));

    /// <summary>Une valeur attend d'être remise, mais seul le compte de la séance peut le faire.</summary>
    private bool WaitsForItsAccount => _entries.Any(e => e.State.IsOutstanding() && _session.IsInAnotherProfile(e, _accountSid));

    public string State => _entries.Select(e => e.State).Distinct().ToList() switch
    {
        [EntryState.Applied] when _comeBack => T("remise depuis"),
        [EntryState.Applied] => T("en place"),
        [EntryState.Reverted] => T("annulée"),
        [EntryState.RevertSkipped] => T("modifiée depuis"),
        [EntryState.Failed] => T("échec, origine remise"),
        [EntryState.Pending] => T("interrompue : peut-être appliquée, « Annuler » vérifie"),
        _ when _entries.Any(e => e.State == EntryState.RollbackFailed) => T("retour arrière raté : peut-être encore en place, « Annuler » réessaie"),
        _ => T("en partie annulée"),
    };

    public string? Note => WaitsForItsAccount
        ? T("Séance faite par un autre compte : ce réglage est dans son profil. Ouvrez MAUS depuis ce compte pour l'annuler.")
        : _entries.Select(e => e.EffectNote).FirstOrDefault(n => n is not null);

    public bool HasNote => Note is not null;

    public string Details => string.Join(Environment.NewLine, _entries.Select(e =>
        $"{e.Key.Describe()} = {SettingValue.Display(e.Before)} → {SettingValue.Display(e.After)}"));
}
