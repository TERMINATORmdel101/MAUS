using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de l'analyse de l'espace : dossier, gros fichier, ou petits fichiers regroupés.</summary>
public sealed class SpaceEntryViewModel
{
    public SpaceEntryViewModel(string name, string path, long bytes, long parentBytes, bool isFolder, string? hint, Action<SpaceEntryViewModel>? open)
    {
        Name = name;
        Path = path;
        Bytes = bytes;
        IsFolder = isFolder;
        Hint = hint;
        Size = WorkshopViewModel.FormatSize(bytes);
        Share = parentBytes > 0 ? (double)bytes / parentBytes : 0;
        Percent = T("{0:0} %", Share * 100);
        OpenCommand = new AsyncCommand(() =>
        {
            open?.Invoke(this);
            return Task.CompletedTask;
        });
        ShowCommand = new AsyncCommand(() =>
        {
            ShellLauncher.ShowInFolder(Path);
            return Task.CompletedTask;
        });
    }

    public string Name { get; }

    public string Path { get; }

    public long Bytes { get; }

    public bool IsFolder { get; }

    public string Icon => IsFolder ? "" : "";

    public string Size { get; }

    public double Share { get; }

    public string Percent { get; }

    public string? Hint { get; }

    public bool HasHint => Hint is not null;

    public bool CanOpen => IsFolder;

    public ICommand OpenCommand { get; }

    public ICommand ShowCommand { get; }
}

/// <summary>Atelier, onglet Stockage : « Qu'est-ce qui prend de la place ? » et test de vitesse du disque.</summary>
public sealed partial class WorkshopViewModel
{
    private SpaceNode? _current;
    private bool _isScanning;
    private string _scanStatus = T("Choisissez un lecteur puis « Analyser » : MAUS parcourt vos dossiers sans rien modifier ni supprimer.");
    private Action? _stopScan;
    private TestOption<string>? _drive;
    private TestOption<long> _diskSize;
    private double _diskProgress;
    private string _diskStatus = string.Empty;

    public ObservableCollection<TestOption<string>> Drives { get; } = [];

    public TestOption<string>? Drive
    {
        get => _drive;
        set => SetProperty(ref _drive, value);
    }

    public ObservableCollection<SpaceEntryViewModel> SpaceEntries { get; } = [];

    public ObservableCollection<SpaceEntryViewModel> LargestFiles { get; } = [];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsNotScanning));
            }
        }
    }

    public bool IsNotScanning => !IsScanning;

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public string CurrentPath => _current is { } node ? T("{0} · {1}", node.Path, FormatSize(node.Bytes)) : string.Empty;

    public bool CanGoUp => _current?.Parent is not null;

    public ICommand ScanCommand { get; }

    public ICommand StopScanCommand { get; }

    public ICommand UpCommand { get; }

    public ICommand OpenCurrentFolderCommand { get; }

    public ICommand StorageSettingsCommand { get; }

    public ICommand StartDiskTestCommand { get; }

    public IReadOnlyList<TestOption<long>> DiskSizes { get; }

    public TestOption<long> DiskSize
    {
        get => _diskSize;
        set => SetProperty(ref _diskSize, value);
    }

    public double DiskProgress
    {
        get => _diskProgress;
        private set => SetProperty(ref _diskProgress, value);
    }

    public string DiskStatus
    {
        get => _diskStatus;
        private set => SetProperty(ref _diskStatus, value);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => T("{0:0.#} Go", bytes / 1073741824.0),
        >= 1L << 20 => T("{0:0.#} Mo", bytes / 1048576.0),
        >= 1L << 10 => T("{0:0} Ko", bytes / 1024.0),
        _ => T("{0} octets", bytes),
    };

    private void LoadDrives()
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                Drives.Add(new(T("{0} · {1} libres sur {2}", drive.Name.TrimEnd('\\'), FormatSize(drive.AvailableFreeSpace), FormatSize(drive.TotalSize)), drive.RootDirectory.FullName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScanStatus = T("La liste des lecteurs n'a pas pu être lue : {0}", ex.Message);
        }

        Drive = Drives.FirstOrDefault();
    }

    private async Task ScanAsync()
    {
        if (IsScanning || Drive is not { Value: var root })
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _stopScan = cancellation.Cancel;
        IsScanning = true;
        SpaceEntries.Clear();
        LargestFiles.Clear();
        try
        {
            ScanStatus = T("Analyse en cours…");
            var progress = new Progress<long>(files => ScanStatus = T("Analyse en cours… {0:N0} fichiers parcourus", files));
            var report = await SpaceAnalyzer.ScanAsync(root, progress, cancellation.Token);
            foreach (var file in report.Largest)
            {
                LargestFiles.Add(new SpaceEntryViewModel(file.Name, file.Path, file.Bytes, report.Root.Bytes, false, SpaceHints.Describe(file.Path), null));
            }

            Show(report.Root);
            ScanStatus = T("{0:N0} fichiers dans {1:N0} dossiers, analysés en {2:0} s.", report.Files, report.Folders, report.Duration.TotalSeconds)
                + (report.Inaccessible > 0 ? " " + T("{0} dossier(s) illisible(s), non comptés.", report.Inaccessible) : string.Empty)
                + " " + T("MAUS ne supprime rien : pour libérer de la place sans risque, utilisez l'Assistant de stockage de Windows.");
        }
        catch (OperationCanceledException)
        {
            ScanStatus = T("Analyse arrêtée.");
        }
        catch (Exception ex)
        {
            ScanStatus = T("L'analyse n'a pas pu aboutir : {0}", ex.Message);
        }
        finally
        {
            _stopScan = null;
            IsScanning = false;
        }
    }

    private void Show(SpaceNode node)
    {
        _current = node;
        SpaceEntries.Clear();
        var entries = new List<SpaceEntryViewModel>();
        entries.AddRange(node.Folders.Select(f => new SpaceEntryViewModel(
            f.Inaccessible ? T("{0} (illisible)", f.Name) : f.Name, f.Path, f.Bytes, node.Bytes, true, SpaceHints.Describe(f.Path), Open)));
        entries.AddRange(node.BigFiles.Select(f => new SpaceEntryViewModel(f.Name, f.Path, f.Bytes, node.Bytes, false, SpaceHints.Describe(f.Path), null)));
        if (node.OtherFiles > 0)
        {
            entries.Add(new SpaceEntryViewModel(T("{0:N0} petits fichiers", node.OtherFiles), node.Path, node.OtherBytes, node.Bytes, false, null, null));
        }

        foreach (var entry in entries.OrderByDescending(e => e.Bytes).Take(200))
        {
            SpaceEntries.Add(entry);
        }

        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CanGoUp));
    }

    private void Open(SpaceEntryViewModel entry)
    {
        if (_current?.Folders.FirstOrDefault(f => f.Path == entry.Path) is { } child)
        {
            Show(child);
        }
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
        {
            Show(parent);
        }
    }

    private async Task RunDiskTestAsync()
    {
        if (Busy || Drive is not { Value: var root })
        {
            return;
        }

        var folder = string.Equals(Path.GetPathRoot(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : root;
        long free;
        try
        {
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiskStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
            return;
        }

        if (free < DiskSize.Value + (2L << 30))
        {
            DiskStatus = T("Pas assez de place libre sur {0} pour ce test : il faut {1} de plus que la taille du test.", root, FormatSize(2L << 30));
            return;
        }

        if (!_confirm(T("Lancer le test de vitesse du disque ?"), T("MAUS va écrire puis relire un fichier temporaire de {0} sur {1}, puis le supprimer. Rien d'autre n'est touché. Le test dure de quelques secondes à une minute.", FormatSize(DiskSize.Value), root) + Environment.NewLine + Environment.NewLine + T("Continuer ?")))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        try
        {
            DiskStatus = T("Test en cours…");
            var progress = new Progress<DiskSpeedProgress>(p =>
            {
                DiskProgress = p.Percent;
                DiskStatus = T("{0} · {1:0} %", p.Step, p.Percent);
            });
            var result = await DiskSpeedTest.RunAsync(folder, DiskSize.Value, progress, cancellation.Token);
            DiskProgress = 100;
            if (result.Aborted)
            {
                DiskStatus = T("Test interrompu.");
                return;
            }

            BenchmarkHistory.CreateDefault().Add(new BenchmarkEntry("disk-read", result.ReadMegabytesPerSecond ?? 0, true, DateTimeOffset.Now, root));
            DiskStatus = T("Écriture : {0:N0} Mo/s · lecture : {1:N0} Mo/s · accès aléatoires : {2:N0} par seconde ({3:0.00} ms chacun).", result.WriteMegabytesPerSecond, result.ReadMegabytesPerSecond, result.RandomReadsPerSecond, result.RandomLatencyMilliseconds)
                + " " + T("Repères : disque dur 100 à 250 Mo/s et environ 100 accès par seconde ; SSD SATA 450 à 550 Mo/s ; SSD NVMe 2 000 à 7 000 Mo/s et plus, avec des milliers d'accès par seconde. Mesure simple : les chiffres des fabricants, obtenus avec beaucoup de demandes en parallèle, sont plus élevés.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiskStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            await StopTestAsync();
        }
    }
}
