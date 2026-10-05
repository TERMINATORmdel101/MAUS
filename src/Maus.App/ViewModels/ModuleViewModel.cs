using System.Windows.Input;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

public sealed class FindingViewModel
{
    private readonly Finding finding;

    /// <param name="module">Module d'origine (« M01 · … »), affiché dans la liste par importance de l'onglet Constats.</param>
    public FindingViewModel(Finding finding, Func<Finding, bool, Task> onAcknowledge, string? module = null)
    {
        this.finding = finding;
        Module = module;
        AcknowledgeCommand = new AsyncCommand(() => onAcknowledge(finding, true));
        UnacknowledgeCommand = new AsyncCommand(() => onAcknowledge(finding, false));
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

    public string? Module { get; }

    public bool HasModule => Module is not null;

    public FindingStatus Status => finding.Status;

    public string StatusLabel => finding.AcknowledgedFrom is { } was ? T("Voulu (était : {0})", Labels.Of(was)) : Labels.Of(finding.Status);

    /// <summary>Un écart peut être marqué « voulu » : il ne sera plus signalé tant que la situation ne change pas.</summary>
    public bool CanAcknowledge => finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem;

    public bool IsAcknowledged => finding.AcknowledgedFrom is not null;

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

    public string Explanation => finding.Explanation;

    public string? Advice => finding.Advice is null ? null : $"→ {finding.Advice}";

    public bool HasAdvice => finding.Advice is not null;
}

public sealed class ModuleViewModel : ObservableObject
{
    private readonly Func<string, Finding, bool, Task> _onAcknowledge;
    private ModuleResult? _result;

    public ModuleViewModel(IAuditModule module, Func<string, Finding, bool, Task> onAcknowledge)
    {
        Id = module.Id;
        Title = module.Title;
        _onAcknowledge = onAcknowledge;
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
            .Select(f => new FindingViewModel(f, (finding, acknowledge) => _onAcknowledge(Id, finding, acknowledge)))
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
