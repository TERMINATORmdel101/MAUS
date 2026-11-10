using System.Windows;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Modules.M14Display;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Ce que l'onglet Constats peut demander à la fenêtre pour un constat : le marquer « voulu », le corriger.</summary>
public interface IFindingActions
{
    Task AcknowledgeAsync(string moduleId, Finding finding, bool acknowledge);

    /// <summary>L'utilisateur coche ou décoche ce module pour les prochains audits.</summary>
    void SetModuleAudited(string moduleId, bool audited);

    /// <summary>Au moins une correction proposée corrige ce constat.</summary>
    bool CanFix(Finding finding);

    /// <summary>Coche les corrections de ce constat et ouvre l'onglet Corrections.</summary>
    void Fix(Finding finding);
}

/// <summary>Pictogramme de chaque état, à côté de sa couleur et de son mot.</summary>
public static class StatusGlyphs
{
    public static string Of(FindingStatus status) => status switch
    {
        FindingStatus.Ok => "\uE73E",
        FindingStatus.Info => "\uE946",
        FindingStatus.Improvable => "\uE74A",
        FindingStatus.Warning => "\uE7BA",
        FindingStatus.Problem => "\uEA39",
        _ => "\uE9CE",
    };
}

/// <summary>Un constat affiché en carte.</summary>
public sealed class FindingViewModel
{
    public override string ToString() => Title;

    private readonly Finding finding;

    /// <param name="module">Module d'origine (« M01 · … »), affiché dans la liste par importance de l'onglet Constats.</param>
    public FindingViewModel(Finding finding, string moduleId, IFindingActions actions, string? module = null)
    {
        ArgumentNullException.ThrowIfNull(actions);

        this.finding = finding;
        ModuleId = moduleId;
        Module = module;
        CanFix = actions.CanFix(finding);
        FixCommand = new AsyncCommand(() =>
        {
            actions.Fix(finding);
        });
        AcknowledgeCommand = new AsyncCommand(() => actions.AcknowledgeAsync(moduleId, finding, true));
        UnacknowledgeCommand = new AsyncCommand(() => actions.AcknowledgeAsync(moduleId, finding, false));
        TryRefreshCommand = new AsyncCommand(() =>
        {
            var target = RefreshRateTargets.Find(finding.Id);
            Views.QuestionWindow.Inform(
                "MAUS",
                target is null
                    ? T("L'écran a changé depuis l'audit ou tourne déjà à sa fréquence maximale : relancez l'audit.")
                    : Views.RefreshTrialWindow.Run(target),
                Views.QuestionTone.Information);
        });
        OpenSettingsPageCommand = new AsyncCommand(() =>
        {
            if (finding.SettingsPage is { } page)
            {
                ShellLauncher.OpenSettings(page);
            }
        });
    }

    public string Title => finding.Title;

    public string Id => finding.Id;

    public string? Module { get; }

    public string ModuleId { get; }

    private readonly List<string> _alsoIn = [];

    /// <summary>« Aussi signalé par M08 · BIOS » quand d'autres modules décrivent le même sujet ; vide sinon.</summary>
    public string AlsoIn => _alsoIn.Count == 0 ? string.Empty : T("Aussi signalé par : {0}", string.Join(", ", _alsoIn));

    public bool HasAlsoIn => _alsoIn.Count > 0;

    /// <summary>Ajoute un autre module qui signale le même sujet (avant l'affichage de la carte).</summary>
    public void AddAlsoIn(string? module)
    {
        if (module is not null && !_alsoIn.Contains(module, StringComparer.Ordinal))
        {
            _alsoIn.Add(module);
        }
    }

    public bool HasModule => Module is not null;

    internal Finding Finding => finding;

    public FindingStatus Status => finding.Status;

    /// <summary>Pictogramme de l'état : la couleur n'est jamais le seul signal (daltonisme).</summary>
    public string StatusIcon => StatusGlyphs.Of(finding.Status);

    public string StatusHelp => finding.Status switch
    {
        FindingStatus.Problem => T("À corriger en priorité : ce point peut gêner ou exposer votre PC."),
        FindingStatus.Warning => T("À regarder : pas urgent, mais à ne pas oublier."),
        FindingStatus.Improvable => T("Tout marche, mais un réglage peut faire mieux."),
        FindingStatus.Unknown => T("MAUS n'a pas pu vérifier ce point."),
        FindingStatus.Info => T("Simple information, rien à faire."),
        _ => T("Tout va bien ici."),
    };

    public string StatusLabel => finding.AcknowledgedFrom is { } was ? T("Voulu (était : {0})", Labels.Of(was)) : Labels.Of(finding.Status);

    /// <summary>Un écart peut être marqué « voulu » : il ne sera plus signalé tant que la situation ne change pas.</summary>
    public bool CanAcknowledge => finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem;

    public bool IsAcknowledged => finding.AcknowledgedFrom is not null;

    /// <summary>MAUS sait corriger ce constat : bouton « Corriger » (coche la correction et ouvre l'onglet Corrections).</summary>
    public bool CanFix { get; }

    public ICommand FixCommand { get; }

    public ICommand AcknowledgeCommand { get; }

    public ICommand UnacknowledgeCommand { get; }

    /// <summary>Le constat renvoie vers une page des Paramètres de Windows (bouton « Ouvrir dans Windows »).</summary>
    public bool HasSettingsPage => finding.SettingsPage is not null;

    /// <summary>Écran sous sa fréquence maximale (Module 14) : bouton « Essayer la fréquence maximale », avec retour automatique.</summary>
    public bool CanTryRefresh => RefreshRateTargets.IsRefreshFinding(finding.Id) && finding.Fixable && finding.AcknowledgedFrom is null;

    public ICommand TryRefreshCommand { get; }

    public ICommand OpenSettingsPageCommand { get; }

    public string Values => finding switch
    {
        { Current: not null, Expected: not null } => T("Constaté : {0}   ·   Attendu : {1}", finding.Current, finding.Expected),
        { Current: not null } => T("Constaté : {0}", finding.Current),
        _ => string.Empty,
    };

    public bool HasValues => finding.Current is not null;

    /// <summary>Problème ou point à surveiller : l'explication est précédée de « Pourquoi ça compte ».</summary>
    public bool ShowWhy => finding.Status is FindingStatus.Problem or FindingStatus.Warning;

    /// <summary>Identité technique du constat, repliée sous « Détails techniques ».</summary>
    public string Identity => finding.Category is { Length: > 0 } category
        ? T("Identifiant : {0} · module {1} · {2}", finding.Id, ModuleId, category)
        : T("Identifiant : {0} · module {1}", finding.Id, ModuleId);

    /// <summary>D'où vient le seuil de ce constat : sa source, ou « choisi par MAUS ».</summary>
    public string? ThresholdNote => ThresholdNotes.For(finding.Id);

    public bool HasThresholdNote => ThresholdNote is not null;

    /// <summary>
    /// Explication, les adresses web réduites au nom du site (bouton « Ouvrir la page web » pour l'adresse complète). Un texte lu
    /// sur le PC reste exact : l'écran montre la commande telle qu'elle est.
    /// </summary>
    public string Explanation => finding.ExplanationHasPcData ? finding.Explanation : LinkText.Shorten(finding.Explanation)!;

    public string? Advice => finding.Advice is null ? null : $"→ {LinkText.Shorten(finding.Advice)}";

    /// <summary>
    /// Première adresse web du conseil : bouton « Ouvrir la page web ». Les adresses de MAUS sont écrites dans les conseils ;
    /// une explication peut citer un nom lu sur le PC, elle ne donne donc jamais de lien.
    /// </summary>
    public Uri? Link => LinkText.FirstLink(finding.Advice);

    public bool HasLink => Link is not null;

    public ICommand OpenLinkCommand => new AsyncCommand(() =>
    {
        if (Link is { } link)
        {
            ShellLauncher.OpenUrl(link);
        }
    });

    public bool HasAdvice => finding.Advice is not null;

    /// <summary>Encadré d'avertissement : toujours dans la couleur d'un point à regarder, quel que soit l'état du constat.</summary>
    public string? Callout => LinkText.Shorten(finding.Callout);

    public bool HasCallout => finding.Callout is not null;

    public FindingStatus CalloutStatus => HasCallout ? FindingStatus.Warning : finding.Status;

    public Uri? CalloutLink => LinkText.FirstLink(finding.Callout);

    public bool HasCalloutLink => CalloutLink is not null;

    public ICommand OpenCalloutLinkCommand => new AsyncCommand(() =>
    {
        if (CalloutLink is { } link)
        {
            ShellLauncher.OpenUrl(link);
        }
    });

    /// <summary>Note d'information : son seul bouton ouvre une page web, rien n'agit sur le PC.</summary>
    public string? SideNote => LinkText.Shorten(finding.SideNote);

    public bool HasSideNote => finding.SideNote is not null;

    public Uri? SideNoteLink => LinkText.FirstLink(finding.SideNote);

    public bool HasSideNoteLink => SideNoteLink is not null;

    public ICommand OpenSideNoteLinkCommand => new AsyncCommand(() =>
    {
        if (SideNoteLink is { } link)
        {
            ShellLauncher.OpenUrl(link);
        }
    });

    /// <summary>Valeur montrée ici seulement : aucun bouton ne la copie.</summary>
    public string? ScreenOnlyNote => finding.ScreenOnlyNote;

    public bool HasScreenOnlyNote => finding.ScreenOnlyNote is not null;
}

/// <summary>Où en est un module pendant l'audit (squelette de l'accueil, 0.8.6).</summary>
public enum AuditState
{
    Pending,
    Running,
    Done,
}

public sealed class ModuleViewModel : ObservableObject
{
    public override string ToString() => Header;

    private AuditState _auditState;

    public AuditState AuditState
    {
        get => _auditState;
        set
        {
            if (SetProperty(ref _auditState, value))
            {
                OnPropertyChanged(nameof(AuditStateIcon));
                OnPropertyChanged(nameof(IsAuditDone));
                OnPropertyChanged(nameof(AuditStateLabel));
            }
        }
    }

    public bool IsAuditDone => AuditState == AuditState.Done;

    private bool _isAudited = true;

    /// <summary>Case « auditer ce module » : décochée, l'audit l'indique comme non vérifié.</summary>
    public bool IsAudited
    {
        get => _isAudited;
        set
        {
            if (SetProperty(ref _isAudited, value))
            {
                _actions.SetModuleAudited(Id, value);
            }
        }
    }

    /// <summary>Affiche le choix enregistré, sans déclencher d'écriture.</summary>
    public void ShowAudited(bool audited)
    {
        _isAudited = audited;
        OnPropertyChanged(nameof(IsAudited));
    }

    public string AuditStateIcon => AuditState switch
    {
        AuditState.Done => "\uE73E",
        AuditState.Running => "\uE768",
        _ => "\uE9CE",
    };

    public string AuditStateLabel => AuditState switch
    {
        AuditState.Done => T("terminé"),
        AuditState.Running => T("en cours"),
        _ => T("en attente"),
    };

    private readonly IFindingActions _actions;
    private ModuleResult? _result;

    public ModuleViewModel(IAuditModule module, IFindingActions actions)
    {
        ArgumentNullException.ThrowIfNull(module);

        Id = module.Id;
        Title = module.Title;
        _actions = actions;
    }

    public string Id { get; }

    public string Title { get; }

    public string Header => $"{Id} · {Title}";

    public FindingStatus Status => _result?.Verdict ?? FindingStatus.Unknown;

    public string StatusIcon => StatusGlyphs.Of(Status);

    public string StatusLabel => Labels.Of(Status);

    public string Summary => _result switch
    {
        null => T("En attente"),
        { Error: not null } => _result.Error,
        _ => Describe(_result),
    };

    public IReadOnlyList<FindingViewModel> Findings =>
        _result?.Findings
            .OrderByDescending(f => f.Status.Rank())
            .Select(f => new FindingViewModel(f, Id, _actions))
            .ToList() ?? [];

    public void SetResult(ModuleResult? result)
    {
        _result = result;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Findings));
    }

    private static string Describe(ModuleResult result)
    {
        var parts = new List<string>();
        Add(parts, result.Count(FindingStatus.Problem), T("{0} problème"), T("{0} problèmes"));
        Add(parts, result.Count(FindingStatus.Warning), T("{0} à surveiller"), T("{0} à surveiller"));
        Add(parts, result.Count(FindingStatus.Improvable), T("{0} optimisation"), T("{0} optimisations"));
        Add(parts, result.Count(FindingStatus.Unknown), T("{0} indéterminé"), T("{0} indéterminés"));
        return parts.Count == 0 ? T("Tout est conforme") : string.Join(" · ", parts);
    }

    private static void Add(List<string> parts, int count, string singular, string plural)
    {
        if (count > 0)
        {
            parts.Add(string.Format(Culture, count == 1 ? singular : plural, count));
        }
    }
}
