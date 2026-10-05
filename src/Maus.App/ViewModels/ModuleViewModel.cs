using System.Windows.Input;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Ce que l'onglet Constats peut demander à la fenêtre pour un constat : le marquer « voulu », le corriger.</summary>
public interface IFindingActions
{
    Task AcknowledgeAsync(string moduleId, Finding finding, bool acknowledge);

    /// <summary>Au moins une correction proposée corrige ce constat.</summary>
    bool CanFix(Finding finding);

    /// <summary>Coche les corrections de ce constat et ouvre l'onglet Corrections.</summary>
    void Fix(Finding finding);
}

public sealed class FindingViewModel
{
    private readonly Finding finding;

    /// <param name="module">Module d'origine (« M01 · … »), affiché dans la liste par importance de l'onglet Constats.</param>
    public FindingViewModel(Finding finding, string moduleId, IFindingActions actions, string? module = null)
    {
        this.finding = finding;
        Module = module;
        CanFix = actions.CanFix(finding);
        FixCommand = new AsyncCommand(() =>
        {
            actions.Fix(finding);
            return Task.CompletedTask;
        });
        AcknowledgeCommand = new AsyncCommand(() => actions.AcknowledgeAsync(moduleId, finding, true));
        UnacknowledgeCommand = new AsyncCommand(() => actions.AcknowledgeAsync(moduleId, finding, false));
        OpenSettingsPageCommand = new AsyncCommand(() =>
        {
            if (finding.SettingsPage is { } page)
            {
                ShellLauncher.OpenSettings(page);
            }

            return Task.CompletedTask;
        });
    }

    public string Title => finding.Title;

    public string Id => finding.Id;

    public string? Module { get; }

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

    public FindingStatus Status => finding.Status;

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

    public ICommand OpenSettingsPageCommand { get; }

    public string? Category => finding.Category;

    public string Values => finding switch
    {
        { Current: not null, Expected: not null } => T("Constaté : {0}   ·   Attendu : {1}", finding.Current, finding.Expected),
        { Current: not null } => T("Constaté : {0}", finding.Current),
        _ => string.Empty,
    };

    public bool HasValues => finding.Current is not null;

    /// <summary>Explication, les adresses web réduites au nom du site (bouton « Ouvrir la page web » pour l'adresse complète).</summary>
    public string Explanation => LinkText.Shorten(finding.Explanation)!;

    public string? Advice => finding.Advice is null ? null : $"→ {LinkText.Shorten(finding.Advice)}";

    /// <summary>Première adresse web du conseil (ou de l'explication) : bouton « Ouvrir la page web ».</summary>
    public Uri? Link => LinkText.FirstLink(finding.Advice) ?? LinkText.FirstLink(finding.Explanation);

    public bool HasLink => Link is not null;

    public ICommand OpenLinkCommand => new AsyncCommand(() =>
    {
        if (Link is { } link)
        {
            ShellLauncher.OpenUrl(link);
        }

        return Task.CompletedTask;
    });

    public bool HasAdvice => finding.Advice is not null;
}

public sealed class ModuleViewModel : ObservableObject
{
    private readonly IFindingActions _actions;
    private ModuleResult? _result;

    public ModuleViewModel(IAuditModule module, IFindingActions actions)
    {
        Id = module.Id;
        Title = module.Title;
        _actions = actions;
    }

    public string Id { get; }

    public string Title { get; }

    public string Header => $"{Id} · {Title}";

    public FindingStatus Status => _result?.WorstStatus ?? FindingStatus.Unknown;

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

    public void SetResult(ModuleResult result)
    {
        _result = result;
        OnPropertyChanged(nameof(Status));
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
