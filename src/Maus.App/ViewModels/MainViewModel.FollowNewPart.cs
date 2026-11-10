using System.Windows.Input;
using Maus.App.ViewModels.Workshop;
using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Un changement de pièce, avec ses boutons.</summary>
public sealed record NewPartRow(string Title, string When, string Guide, IReadOnlyList<string> Checks, ICommand? OpenFindings, ICommand? OpenBenchmark, string OpenFindingsLabel, string OpenBenchmarkLabel)
{
    public override string ToString() => Title;

    public bool HasGuide => Guide.Length > 0;

    public bool HasChecks => Checks.Count > 0;

    public bool ShowFindings => OpenFindings is not null;

    public bool ShowBenchmark => OpenBenchmark is not null;
}

/// <summary>Suivi, « J'ai changé une pièce » : la fiche du matériel se met à jour quand un audit ou la fiche du PC la lit, jamais à l'ouverture.</summary>
public sealed partial class MainViewModel
{
    private readonly object _newPartGate = new();
    private PartsCard? _newPartCard;
    private AuditSnapshot? _newPartSnapshot;
    private DateTimeOffset? _newPartAuditAt;
    private string _newPartNotice = string.Empty;
    private ICommand? _readNewPart;

    /// <summary>Le fichier existe mais n'a pas pu être relu : ce n'est pas « pas encore de fiche ».</summary>
    private bool _newPartNotRead;

    /// <summary>Change à chaque mise à jour : une relecture commencée avant ne remet pas l'ancienne fiche après elle.</summary>
    private int _newPartVersion;

    /// <summary>Sans fichier (tests) : la fiche ne lit ni n'écrit rien.</summary>
    internal PartsCardStore? NewPartStore { get; init; }

    internal Func<HardwareInventory?, IReadOnlyList<HardwarePart>> ReadNewParts { get; init; } =
        inventory => inventory is null ? PartsReader.ReadLight() : PartsReader.FromInventory(inventory);

    public static string NewPartTitle => T("J'ai changé une pièce");

    public static string NewPartIntro => T("Écran, mémoire, carte graphique ou disque : MAUS garde une petite fiche des modèles de ce PC, sans aucun numéro de série. Quand une pièce diffère, il le dit, avec ce qu'il sait vérifier pour elle.");

    public static string NewPartReadLabel => T("Relire mon matériel");

    public static string NewPartAuditLabel => T("Lancer l'audit");

    public static string NewPartNeedsAuditText => T("Aucun audit depuis ce changement : lancez-en un pour que MAUS vérifie cette pièce. L'audit ne modifie rien.");

    public ICommand ReadNewPartCommand => _readNewPart ??= new AsyncCommand(() => RefreshNewPartAsync(null));

    public IReadOnlyList<NewPartRow> NewPartChanges => NewPartView.Build(_newPartCard, NewPartChecks(out var at), at).Select(ToRow).ToList();

    public bool NewPartNeedsAudit => NewPartView.Build(_newPartCard, NewPartChecks(out var at), at).Any(c => c.NeedsAudit);

    public string NewPartStatus
    {
        get
        {
            if (_newPartCard is not { } card)
            {
                if (_newPartNotRead)
                {
                    return T("La fiche du matériel n'a pas pu être relue pour l'instant : MAUS réessaie au prochain affichage de cette carte.");
                }

                return T("Pas encore de fiche. Elle se crée au prochain audit, à la lecture de « Mon PC » dans l'Atelier, ou avec le bouton ci-dessous. La première fois, rien n'est annoncé comme changé : MAUS ne sait pas encore ce qu'il y avait avant.");
            }

            return card.Changes.Count == 0
                ? T("Rien de changé depuis la création de la fiche, le {0:d}. Dernière lecture le {1:d}.", card.CreatedAt.ToLocalTime(), card.UpdatedAt.ToLocalTime())
                : T("Fiche créée le {0:d}. Dernière lecture le {1:d}.", card.CreatedAt.ToLocalTime(), card.UpdatedAt.ToLocalTime());
        }
    }

    /// <summary>Le contenu de la fiche, sans les pièces qui ne sont plus vues.</summary>
    public string NewPartList
    {
        get
        {
            var names = (_newPartCard?.Parts ?? []).Where(p => p.MissingSince is null && p.Name.Length > 0)
                .Select(p => p.Count > 1 ? $"{p.Name} × {p.Count}" : p.Name).ToList();
            return names.Count == 0 ? string.Empty : T("Sur la fiche : {0}.", string.Join(" · ", names));
        }
    }

    public bool HasNewPartList => NewPartList.Length > 0;

    /// <summary>Ce qui n'a pas été lu est dit : ni retiré, ni sain.</summary>
    public string NewPartUnread => _newPartCard is { UnreadKinds.Count: > 0 } card
        ? T("Non lu à la dernière lecture : {0}. Lecture vide ou sans droits administrateur : MAUS n'en conclut rien et la fiche garde ce qu'elle en savait.", string.Join(", ", card.UnreadKinds.Select(NewPartView.KindName)))
        : string.Empty;

    public bool HasNewPartUnread => NewPartUnread.Length > 0;

    public string NewPartNotice
    {
        get => _newPartNotice;
        private set
        {
            if (SetProperty(ref _newPartNotice, value))
            {
                OnPropertyChanged(nameof(HasNewPartNotice));
            }
        }
    }

    public bool HasNewPartNotice => _newPartNotice.Length > 0;

    /// <summary>Affichage du panneau : relit le fichier de la fiche et les audits gardés, pas le matériel.</summary>
    internal async Task LoadNewPartAsync()
    {
        if (NewPartStore is not { } store)
        {
            return;
        }

        try
        {
            var snapshots = _histories.Snapshots;
            var version = _newPartVersion;
            var (read, card, snapshot) = await Task.Run(() => (store.TryLoad(out var loaded), loaded, snapshots.Load() is { Count: > 0 } kept ? kept[^1] : null));
            _newPartSnapshot = snapshot;

            // Fichier tenu un instant par un antivirus, ou abîmé : la fiche déjà en mémoire reste la bonne.
            if (version == _newPartVersion)
            {
                _newPartNotRead = !read;
                _newPartCard = read ? card : _newPartCard;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("fiche du matériel : relecture impossible (" + ex.GetType().Name + ")");
        }

        RaiseNewPart();
    }

    /// <summary>Lit le matériel, hors du fil de la fenêtre, et met la fiche à jour.</summary>
    /// <param name="inventory">La fiche du PC, si c'est elle qui vient d'être lue.</param>
    /// <param name="afterAudit">Les constats en mémoire datent de cet instant : ils parlent de ce qui est lu ici.</param>
    internal async Task RefreshNewPartAsync(HardwareInventory? inventory, bool afterAudit = false)
    {
        if (NewPartStore is not { } store)
        {
            return;
        }

        var at = DateTimeOffset.Now;
        if (afterAudit)
        {
            _newPartAuditAt = at;
        }

        try
        {
            var read = ReadNewParts;
            _newPartCard = await Task.Run(() =>
            {
                // Un audit et la fiche du PC peuvent finir en même temps : une mise à jour à la fois.
                lock (_newPartGate)
                {
                    return store.Update(read(inventory), at);
                }
            });
            _newPartVersion++;
            _newPartNotRead = false;
            NewPartNotice = _newPartCard is null ? T("La lecture n'a rien rendu : la fiche n'est pas créée. Ce n'est pas un problème du PC.") : string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Une fiche qui ne s'écrit pas ne doit jamais faire échouer l'audit.
            Maus.Core.Diagnostics.Breadcrumbs.Add("fiche du matériel : mise à jour impossible (" + ex.GetType().Name + ")");
            NewPartNotice = T("La fiche du matériel n'a pas pu être mise à jour : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }

        RaiseNewPart();
    }

    /// <summary>Les constats les plus récents : ceux de l'audit de cette fenêtre, sinon le dernier audit gardé (sans ses valeurs lues).</summary>
    private List<PartCheck> NewPartChecks(out DateTimeOffset? at)
    {
        if (_newPartAuditAt is not null)
        {
            at = _newPartAuditAt;
            return [.. PartCheck.From(_results)];
        }

        at = _newPartSnapshot?.At;
        return _newPartSnapshot is { } snapshot ? [.. PartCheck.From(snapshot)] : [];
    }

    private NewPartRow ToRow(PartChangeView change) => new(
        change.Title,
        change.When,
        change.Guide,
        change.Checks,
        change.ModuleId is { } module && change.Checks.Count > 0 ? new ParameterCommand(_ => ShowModuleFindings(module)) : null,
        change.Kind == PartKind.Graphics && change.ModuleId is not null ? new ParameterCommand(_ => GoToCommand.Execute(TabBenchmark)) : null,
        T("Voir les constats"),
        T("Ouvrir le benchmark"));

    private void RaiseNewPart()
    {
        OnPropertyChanged(nameof(NewPartChanges));
        OnPropertyChanged(nameof(NewPartNeedsAudit));
        OnPropertyChanged(nameof(NewPartStatus));
        OnPropertyChanged(nameof(NewPartList));
        OnPropertyChanged(nameof(HasNewPartList));
        OnPropertyChanged(nameof(NewPartUnread));
        OnPropertyChanged(nameof(HasNewPartUnread));
    }
}
