using System.Collections.ObjectModel;
using System.Globalization;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne du gestionnaire des tâches à la MAUS.</summary>
public sealed class ProcessRowViewModel(ProcessSample sample, ProcessCatalog catalog, SystemFolders folders) : ObservableObject
{
    public override string ToString() => Name;

    private readonly (string Category, string What) _explained = catalog.Explain(sample.Name);

    public ProcessSample Sample { get; private set; } = sample;

    /// <summary>Même processus (PID et heure de lancement) relu : la ligne reste, seules ses valeurs changent à l'écran.</summary>
    internal void Update(ProcessSample sample, SystemFolders folders)
    {
        var before = Sample;
        Sample = sample;
        OnPropertyChanged(nameof(Sample));
        if (before.CpuPercent != sample.CpuPercent)
        {
            OnPropertyChanged(nameof(Cpu));
            OnPropertyChanged(nameof(CpuBar));
        }

        if (before.PrivateBytes != sample.PrivateBytes)
        {
            OnPropertyChanged(nameof(Memory));
        }

        if (before.IoBytesPerSecond != sample.IoBytesPerSecond)
        {
            OnPropertyChanged(nameof(Io));
        }

        if (before.GpuPercent != sample.GpuPercent)
        {
            OnPropertyChanged(nameof(Gpu));
        }

        if (before.Path != sample.Path)
        {
            Trust = ProcessRules.TrustOf(sample, folders);
            OnPropertyChanged(nameof(Trust));
            OnPropertyChanged(nameof(TrustLabel));
            OnPropertyChanged(nameof(Location));
        }
    }

    public int Pid => Sample.Pid;

    public string Name => Sample.Name;

    public string Cpu => Sample.CpuPercent.ToString("0.0", Culture) + " %";

    public double CpuBar => Math.Min(60, Sample.CpuPercent * 0.6);

    public string Memory => Sample.PrivateBytes >= 1073741824
        ? (Sample.PrivateBytes / 1073741824.0).ToString("0.0 ", Culture) + T("Go")
        : (Sample.PrivateBytes / 1048576.0).ToString("0 ", Culture) + T("Mo");

    public string Io => Sample.IoBytesPerSecond >= 1_048_576
        ? T("{0:0.0} Mo/s", Sample.IoBytesPerSecond / 1_048_576)
        : Sample.IoBytesPerSecond > 0 ? T("{0:0} Ko/s", Sample.IoBytesPerSecond / 1024) : "—";

    public string Gpu => Sample.GpuPercent is { } g ? g.ToString("0", Culture) + " %" : "—";

    public ProcessTrust Trust { get; private set; } = ProcessRules.TrustOf(sample, folders);

    public string TrustLabel => ProcessRules.Describe(Trust);

    public string Category => _explained.Category;

    public string What => _explained.What;

    public string Location => Sample.Path ?? T("emplacement protégé (processus système)");

    public string Started => Sample.StartTime is { } start ? start.ToLocalTime().ToString("g", Culture) : "—";

    public string PidText => Pid.ToString(CultureInfo.InvariantCulture);

    public (bool Allowed, string? Reason) Termination => ProcessRules.CanTerminate(Sample, Environment.ProcessId, ProcessRules.CurrentSessionId);

    public bool CanTerminate => Termination.Allowed;

    public string? TerminationNote => Termination.Reason;

}

/// <summary>Une cause de lenteur, avec le module qui aide à la corriger.</summary>
public sealed record CauseViewModel(string Title, string Explanation, string Module, System.Windows.Input.ICommand? OpenModule = null)
{
    public override string ToString() => Title;

    public bool HasModule => OpenModule is not null;
}

/// <summary>Données de l'onglet « Processus ».</summary>
public sealed class ProcessListState
{
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    public ObservableCollection<CauseViewModel> Causes { get; } = [];

    /// <summary>
    /// Affiche ces processus dans cet ordre en gardant les lignes déjà là : un processus se reconnaît à son PID et à son heure de
    /// lancement. La ligne choisie et le focus clavier survivent ainsi à la relecture, toutes les 2 secondes.
    /// </summary>
    public void Show(IReadOnlyList<ProcessSample> samples, ProcessCatalog catalog, SystemFolders folders)
    {
        var known = new Dictionary<(int Pid, DateTime? Start), ProcessRowViewModel>(Rows.Count);
        foreach (var row in Rows)
        {
            known[(row.Pid, row.Sample.StartTime)] = row;
        }

        var wanted = new List<ProcessRowViewModel>(samples.Count);
        foreach (var sample in samples)
        {
            if (known.Remove((sample.Pid, sample.StartTime), out var row))
            {
                row.Update(sample, folders);
            }
            else
            {
                row = new ProcessRowViewModel(sample, catalog, folders);
            }

            wanted.Add(row);
        }

        // Ce qui reste dans « known » a disparu.
        foreach (var gone in known.Values)
        {
            Rows.Remove(gone);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i]))
            {
                continue;
            }

            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0)
            {
                Rows.Move(at, i);
            }
            else
            {
                Rows.Insert(i, wanted[i]);
            }
        }

        // Tri choisi d'un clic sur un en-tête : la vue ne le refait pas seule quand seules les valeurs changent.
        if (System.Windows.Data.CollectionViewSource.GetDefaultView(Rows) is { SortDescriptions.Count: > 0 } view)
        {
            view.Refresh();
        }
    }
}
