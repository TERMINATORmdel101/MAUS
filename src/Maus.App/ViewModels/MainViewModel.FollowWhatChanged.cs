using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une ligne de « Qu'est-ce qui a changé ? » : la chose, ce qui lui est arrivé, quand, et son constat s'il existe.</summary>
public sealed record WhatChangedRow(string Icon, string Title, string Detail, string When, string? OpenLabel, ICommand? Open)
{
    public override string ToString() => Title;

    public bool HasOpen => Open is not null;
}

/// <summary>Une chose que MAUS ne peut pas comparer, et pourquoi.</summary>
public sealed record WhatChangedNote(string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>
/// « Qu'est-ce qui a changé ? » (page Suivi) : entre le dernier audit gardé et un précédent, au choix. Tout vient des résumés
/// déjà en mémoire : aucune lecture de Windows, aucun moteur, aucune autorisation, rien de lancé à l'ouverture de la page.
/// </summary>
public sealed partial class MainViewModel
{
    private ChoiceOption<AuditSnapshot>? _whatChangedChoice;
    private bool _loadingWhatChanged;
    private ParameterCommand? _whatChangedCopy;

    public ObservableCollection<ChoiceOption<AuditSnapshot>> WhatChangedOptions { get; } = [];

    public ObservableCollection<WhatChangedRow> WhatChangedRows { get; } = [];

    public ObservableCollection<WhatChangedNote> WhatChangedNotes { get; } = [];

    public ChoiceOption<AuditSnapshot>? WhatChangedChoice
    {
        get => _whatChangedChoice;
        set
        {
            if (SetProperty(ref _whatChangedChoice, value) && !_loadingWhatChanged)
            {
                RefreshWhatChanged();
            }
        }
    }

    public bool HasWhatChanged => WhatChangedOptions.Count > 0;

    public bool HasWhatChangedNotes => WhatChangedNotes.Count > 0;

    /// <summary>Moins de deux audits gardés : ce qu'il faut faire pour remplir la carte.</summary>
    public string WhatChangedEmpty
    {
        get
        {
            if (HasWhatChanged)
            {
                return string.Empty;
            }

            // Sans droits, un audit lancé à la main n'est pas gardé : répéter « refaites un audit » ne remplirait jamais la carte.
            var withoutRights = IsLimitedAudit || !CanReadJournalSilently();
            return _snapshots.Count switch
            {
                0 when withoutRights => T("Aucun audit gardé sur ce PC pour l'instant. Lancez un audit avec « Toutes les fonctionnalités » (un audit sans droits administrateur n'est pas gardé) : MAUS en garde un résumé ici. Dès le deuxième, cette carte montrera ce qui a changé."),
                0 => T("Aucun audit gardé sur ce PC pour l'instant. Lancez un audit depuis l'accueil : MAUS en garde un résumé ici. Dès le deuxième, cette carte montrera ce qui a changé."),
                _ when withoutRights => T("Un seul audit gardé, celui du {0:g}. Lancez un audit avec « Toutes les fonctionnalités » (un audit sans droits administrateur n'est pas gardé) : cette carte montrera ce qui a changé.", _snapshots[^1].At.LocalDateTime),
                _ => T("Un seul audit gardé, celui du {0:g}. Refaites un audit plus tard : cette carte montrera ce qui a changé entre les deux.", _snapshots[^1].At.LocalDateTime),
            };
        }
    }

    public bool ShowWhatChangedEmpty => !HasWhatChanged;

    public string WhatChangedSummary { get; private set; } = string.Empty;

    /// <summary>Deux versions de MAUS : le dit, car un contrôle a pu naître entre les deux.</summary>
    public string WhatChangedVersionNote { get; private set; } = string.Empty;

    public bool HasWhatChangedVersionNote => WhatChangedVersionNote.Length > 0;

    /// <summary>Le détail d'un constat ne se trouve que dans un audit de cette fenêtre : les résumés gardés ne l'ont pas.</summary>
    public string WhatChangedLinkHint => HasWhatChanged && _results.Count == 0
        ? T("Les liens vers les constats apparaissent après un audit dans cette fenêtre : les résumés gardés n'ont pas le détail.")
        : string.Empty;

    public bool HasWhatChangedLinkHint => WhatChangedLinkHint.Length > 0;

    public string WhatChangedStatus { get; private set; } = string.Empty;

    public ICommand WhatChangedCopyCommand => _whatChangedCopy ??= new ParameterCommand(_ => CopyWhatChanged());

    /// <summary>La liste telle qu'elle se copie (le masquage est appliqué à la copie).</summary>
    internal string WhatChangedText()
    {
        var text = new StringBuilder().AppendLine(Ui.WhatChangedTitle).AppendLine(WhatChangedSummary);
        if (HasWhatChangedVersionNote)
        {
            text.AppendLine(WhatChangedVersionNote);
        }

        foreach (var row in WhatChangedRows)
        {
            text.AppendLine().Append("- ").Append(row.Title).Append(" : ").AppendLine(row.Detail).Append("  ").AppendLine(row.When);
        }

        if (HasWhatChangedNotes)
        {
            text.AppendLine().AppendLine(Ui.WhatChangedNotesTitle);
            foreach (var note in WhatChangedNotes)
            {
                text.Append("- ").Append(note.Title).Append(" : ").AppendLine(note.Detail);
            }
        }

        return text.ToString().TrimEnd();
    }

    private void CopyWhatChanged()
    {
        WhatChangedStatus = ClipboardText.TryCopy(WhatChangedText()) ? T("Liste copiée (nom d'utilisateur et nom du PC masqués).") : ClipboardText.Busy;
        OnPropertyChanged(nameof(WhatChangedStatus));
    }

    /// <summary>Audits gardés relus ou nouvel audit : le dernier reste la référence, le précédent est choisi d'office.</summary>
    private void RefreshWhatChangedChoices()
    {
        _loadingWhatChanged = true;
        WhatChangedOptions.Clear();
        if (_snapshots.LastOrDefault() is { } newest)
        {
            foreach (var older in _snapshots.Where(s => s.At < newest.At).OrderByDescending(s => s.At))
            {
                WhatChangedOptions.Add(new(AuditLabel(older), older));
            }
        }

        WhatChangedChoice = WhatChangedOptions.FirstOrDefault();
        _loadingWhatChanged = false;
        RefreshWhatChanged();
    }

    private void RefreshWhatChanged()
    {
        WhatChangedRows.Clear();
        WhatChangedNotes.Clear();
        WhatChangedSummary = string.Empty;
        WhatChangedVersionNote = string.Empty;
        if (WhatChangedChoice?.Value is { } older && _snapshots.LastOrDefault() is { } newest)
        {
            var report = WhatChanged.Compare(_snapshots, older, newest);

            // La note de tête dit déjà que MAUS a changé de version : pas besoin de le redire à chaque ligne.
            var versionSaid = !report.SameMausVersion;
            foreach (var change in report.Changes)
            {
                WhatChangedRows.Add(RowOf(change, versionSaid));
            }

            foreach (var note in report.NotComparable)
            {
                WhatChangedNotes.Add(NoteOf(note, versionSaid));
            }

            var from = older.At.LocalDateTime;
            var to = newest.At.LocalDateTime;
            WhatChangedSummary = report.Changes.Count == 0
                ? T("Aucun changement relevé entre l'audit du {0:g} et celui du {1:g}. Cela veut dire que rien n'a bougé dans ce que MAUS garde, pas que rien n'a bougé du tout.", from, to)
                : T("Entre l'audit du {0:g} et celui du {1:g} : {2}.", from, to, Plural(report.Changes.Count, T("{0} changement relevé"), T("{0} changements relevés")));
            if (!report.SameMausVersion)
            {
                WhatChangedVersionNote = older.MausVersion is { } before && newest.MausVersion is { } after
                    ? T("Ces audits viennent de deux versions de MAUS ({0}, puis {1}) : un contrôle ajouté par MAUS n'est pas compté comme un changement de votre PC.", before, after)
                    : T("Un de ces audits ne note pas sa version de MAUS : un contrôle ajouté depuis n'est pas compté comme un changement de votre PC.");
            }
        }

        foreach (var name in new[]
        {
            nameof(HasWhatChanged), nameof(HasWhatChangedNotes), nameof(WhatChangedEmpty), nameof(ShowWhatChangedEmpty), nameof(WhatChangedSummary),
            nameof(WhatChangedVersionNote), nameof(HasWhatChangedVersionNote), nameof(WhatChangedLinkHint), nameof(HasWhatChangedLinkHint),
        })
        {
            OnPropertyChanged(name);
        }
    }

    private static string AuditLabel(AuditSnapshot snapshot)
    {
        var at = snapshot.At.LocalDateTime;
        if (snapshot.WithoutRights)
        {
            return T("Audit de la semaine du {0:g}, sans droits administrateur", at);
        }

        if (snapshot.Weekly)
        {
            return T("Audit de la semaine du {0:g}", at);
        }

        return snapshot.IsPartial ? T("Audit partiel du {0:g}", at) : T("Audit du {0:g}", at);
    }

    private WhatChangedRow RowOf(PcChange change, bool versionSaid)
    {
        var hasFinding = change.FindingId is { } id && _results.Exists(r => r.Findings.Any(f => f.Id == id));
        return new(
            IconOf(change.Family),
            change.Family switch
            {
                ChangeFamily.Windows => T("Version de Windows"),

                // Le résumé garde le titre dans la langue de son audit : retraduit s'il est connu, jamais pour un nom de programme ou de pilote.
                ChangeFamily.Finding => T(change.Name),
                _ => change.Name,
            },
            DetailOf(change, versionSaid),
            T("Entre l'audit du {0:g} et celui du {1:g}", change.LastBefore.LocalDateTime, change.FirstAfter.LocalDateTime),
            hasFinding ? T("Voir le constat") : null,
            hasFinding ? new ParameterCommand(_ => OpenFollowedFinding(change.FindingId!)) : null);
    }

    private static string IconOf(ChangeFamily family) => family switch
    {
        ChangeFamily.Windows => "",
        ChangeFamily.Startup => "",
        ChangeFamily.Task => "",
        ChangeFamily.Driver => "",
        _ => "",
    };

    private static string DetailOf(PcChange change, bool versionSaid) => change.Family switch
    {
        ChangeFamily.Windows => T("Build {0} → {1}", change.Before, change.After),
        ChangeFamily.Startup => StartupDetail(change),
        ChangeFamily.Task => change.Type == ChangeType.Appeared
            ? T("Nouvelle tâche planifiée lancée à l'ouverture de session")
            : T("N'est plus lancée à l'ouverture de session (tâche retirée ou désactivée)"),
        ChangeFamily.Driver => DriverDetail(change),
        _ => FindingDetail(change, versionSaid),
    };

    private static string StartupDetail(PcChange change) => change.Type switch
    {
        ChangeType.Appeared when change.After == WhatChanged.StartupDisabled => T("Nouveau au démarrage, désactivé"),
        ChangeType.Appeared => T("Nouveau programme au démarrage"),
        ChangeType.Gone => T("N'est plus dans les programmes au démarrage"),
        _ when change.After == WhatChanged.StartupEnabled => T("Réactivé au démarrage"),
        _ => T("Désactivé au démarrage"),
    };

    private static string DriverDetail(PcChange change) => change.Type switch
    {
        ChangeType.Changed => T("Pilote : version {0} → {1}", change.Before, change.After),
        ChangeType.Appeared when change.After is { } version => T("Nouveau pilote (version {0})", version),
        ChangeType.Appeared => T("Nouveau pilote"),
        _ => T("Pilote plus listé : appareil débranché, pilote retiré ou remplacé par celui de Windows, MAUS ne sait pas lequel"),
    };

    private static string FindingDetail(PcChange change, bool versionSaid)
    {
        var detail = change.Type switch
        {
            ChangeType.Appeared => T("Constat apparu : {0}", Labels.Of(change.StatusAfter!.Value)),
            ChangeType.Gone => T("Constat disparu (était : {0})", Labels.Of(change.StatusBefore!.Value)),
            _ => T("Constat : {0} → {1}", Labels.Of(change.StatusBefore!.Value), Labels.Of(change.StatusAfter!.Value)),
        };
        if (change.MausVersionChanged && !versionSaid)
        {
            detail += ". " + T("MAUS a changé de version entre ces audits : ce verdict a pu changer avec lui.");
        }

        if (change.ReadingChanged)
        {
            detail += ". " + T("Ces deux audits n'ont pas pu lire autant l'un que l'autre : ce verdict a pu changer avec cela, sans que rien ne change sur le PC.");
        }

        return change.OtherAccount ? detail + ". " + T("Un seul de ces audits a été fait avec un autre compte administrateur : ce verdict peut venir de là.") : detail;
    }

    private static WhatChangedNote NoteOf(NotComparableNote note, bool versionSaid)
    {
        var at = note.At.LocalDateTime;
        return note.Reason switch
        {
            NotComparableReason.ModuleNotRead => new(T("Modules non lus : {0}", note.Name), T("L'audit du {0:g} ne les a pas lus : rien n'y est comparé.", at)),
            NotComparableReason.SourceNotRead => new(note.Name ?? string.Empty, note.WithoutRights
                ? T("Non lu sans droits administrateur à l'audit du {0:g} : ses programmes ne sont pas comparés.", at)
                : T("Non lu à l'audit du {0:g} : ses programmes ne sont pas comparés.", at)),
            NotComparableReason.NotKept => new(FamilyLabel(note.Family), note.WithoutRights
                ? T("Non lu sans droits administrateur à l'audit du {0:g} : pas de comparaison.", at)
                : T("L'audit du {0:g} ne contient pas cette information (version plus ancienne de MAUS, ou lecture impossible ce jour-là) : pas de comparaison.", at)),
            NotComparableReason.ReadsLess => new(note.Name ?? string.Empty, note.Side == ChangeType.Gone
                ? T("Absent de l'audit du {0:g}, qui lit moins que l'autre : MAUS ne peut pas dire que cela a disparu.", at)
                : T("Absent de l'audit du {0:g}, qui lit moins que l'autre : MAUS ne peut pas dire que c'est nouveau.", at)),
            NotComparableReason.MausVersionChanged when versionSaid => new(note.Name ?? string.Empty, note.Side == ChangeType.Gone
                ? T("Plus dans l'audit du {0:g} : le contrôle a peut-être changé, pas forcément votre PC.", at)
                : T("Seulement dans l'audit du {0:g} : c'est peut-être un contrôle ajouté, pas un changement du PC.", at)),
            NotComparableReason.MausVersionChanged => new(note.Name ?? string.Empty, note.Side == ChangeType.Gone
                ? T("Plus dans l'audit du {0:g}, fait par une autre version de MAUS : le contrôle a peut-être changé, pas forcément votre PC.", at)
                : T("Seulement dans l'audit du {0:g}, fait par une autre version de MAUS : c'est peut-être un contrôle ajouté, pas un changement du PC.", at)),
            NotComparableReason.OtherAccount => new(note.Name ?? string.Empty, T("L'audit du {0:g} a été fait avec un autre compte administrateur : ce qui est propre à l'utilisateur n'est pas comparé.", at)),
            _ => new(T("Contrôles non lus : {0}", note.Count), note.WithoutRights
                ? T("Non lus sans droits administrateur à l'audit du {0:g} : ils ne sont pas comparés.", at)
                : T("Indéterminés à l'audit du {0:g} (droits administrateur manquants ou source indisponible) : ils ne sont pas comparés.", at)),
        };
    }

    private static string FamilyLabel(ChangeFamily family) => family switch
    {
        ChangeFamily.Startup => T("Programmes au démarrage"),
        ChangeFamily.Task => T("Tâches planifiées à l'ouverture de session"),
        ChangeFamily.Driver => T("Pilotes"),
        _ => T("Constats"),
    };

    /// <summary>Ouvre l'onglet Constats sur ce constat : par la recherche, qui le trouve quel que soit le module, conformes compris.</summary>
    private void OpenFollowedFinding(string id)
    {
        if (_results.SelectMany(r => r.Findings).FirstOrDefault(f => f.Id == id) is not { } finding)
        {
            return;
        }

        FindingFilterChoice = FindingFilterOptions[0];
        FamilyFilterChoice = FamilyFilterOptions[0];
        ShowByModule = false;
        FindingSearch = finding.Title;
        ApplyFindingSearch();
        SelectedTab = TabFindings;
    }
}
