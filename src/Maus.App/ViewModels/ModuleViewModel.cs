using Maus.Core;
using Maus.Core.Reporting;

namespace Maus.App.ViewModels;

public sealed class FindingViewModel(Finding finding)
{
    public string Title => finding.Title;

    public FindingStatus Status => finding.Status;

    public string StatusLabel => Labels.Of(finding.Status);

    public string? Category => finding.Category;

    public string Values => finding switch
    {
        { Current: not null, Expected: not null } => $"Constaté : {finding.Current}   ·   Attendu : {finding.Expected}",
        { Current: not null } => $"Constaté : {finding.Current}",
        _ => string.Empty,
    };

    public bool HasValues => finding.Current is not null;

    public string Explanation => finding.Explanation;

    public string? Advice => finding.Advice is null ? null : $"→ {finding.Advice}";

    public bool HasAdvice => finding.Advice is not null;
}

public sealed class ModuleViewModel : ObservableObject
{
    private ModuleResult? _result;

    public ModuleViewModel(IAuditModule module)
    {
        Id = module.Id;
        Title = module.Title;
    }

    public string Id { get; }

    public string Title { get; }

    public string Header => $"{Id} · {Title}";

    public FindingStatus Status => _result?.WorstStatus ?? FindingStatus.Unknown;

    public string Summary => _result switch
    {
        null => "En attente",
        { Error: not null } => _result.Error,
        _ => Describe(_result),
    };

    public IReadOnlyList<FindingViewModel> Findings =>
        _result?.Findings
            .OrderByDescending(f => f.Status.Rank())
            .Select(f => new FindingViewModel(f))
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
        Add(parts, result.Count(FindingStatus.Problem), "problème", "problèmes");
        Add(parts, result.Count(FindingStatus.Warning), "à surveiller", "à surveiller");
        Add(parts, result.Count(FindingStatus.Improvable), "optimisation", "optimisations");
        Add(parts, result.Count(FindingStatus.Unknown), "indéterminé", "indéterminés");
        return parts.Count == 0 ? "Tout est conforme" : string.Join(" · ", parts);
    }

    private static void Add(List<string> parts, int count, string singular, string plural)
    {
        if (count > 0)
        {
            parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }
    }
}
