using System.Collections.ObjectModel;
using System.Globalization;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne du gestionnaire des tâches à la MAUS.</summary>
public sealed class ProcessRowViewModel(ProcessSample sample, ProcessCatalog catalog, SystemFolders folders)
{
    private readonly (string Category, string What) _explained = catalog.Explain(sample.Name);

    public ProcessSample Sample { get; } = sample;

    public int Pid => Sample.Pid;

    public string Name => Sample.Name;

    public string Cpu => Sample.CpuPercent.ToString("0.0", Culture) + " %";

    public double CpuBar => Math.Min(60, Sample.CpuPercent * 0.6);

    public string Memory => Sample.PrivateBytes >= 1073741824
        ? (Sample.PrivateBytes / 1073741824.0).ToString("0.0 ", Culture) + T("Go")
        : (Sample.PrivateBytes / 1048576.0).ToString("0 ", Culture) + T("Mo");

    public string Disk => Sample.DiskBytesPerSecond >= 1_048_576
        ? T("{0:0.0} Mo/s", Sample.DiskBytesPerSecond / 1_048_576)
        : Sample.DiskBytesPerSecond > 0 ? T("{0:0} Ko/s", Sample.DiskBytesPerSecond / 1024) : "—";

    public string Gpu => Sample.GpuPercent is { } g ? g.ToString("0", Culture) + " %" : "—";

    public ProcessTrust Trust { get; } = ProcessRules.TrustOf(sample, folders);

    public string TrustLabel => ProcessRules.Describe(Trust);

    public string Category => _explained.Category;

    public string What => _explained.What;

    public string Location => Sample.Path ?? T("emplacement protégé (processus système)");

    public string Started => Sample.StartTime is { } start ? start.ToLocalTime().ToString("g", Culture) : "—";

    public string PidText => Pid.ToString(CultureInfo.InvariantCulture);

    public (bool Allowed, string? Reason) Termination => ProcessRules.CanTerminate(Sample, Environment.ProcessId);

    public bool CanTerminate => Termination.Allowed;

    public string? TerminationNote => Termination.Reason;

    public bool IsUnusual => Trust == ProcessTrust.Unusual;
}

/// <summary>Une cause de lenteur, avec le module qui aide à la corriger.</summary>
public sealed record CauseViewModel(string Title, string Explanation, string Module);

/// <summary>Données de l'onglet « Processus ».</summary>
public sealed class ProcessListState
{
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    public ObservableCollection<CauseViewModel> Causes { get; } = [];
}
