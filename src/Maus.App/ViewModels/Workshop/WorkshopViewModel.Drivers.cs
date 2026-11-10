using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Maus.Core;
using Maus.Core.Hardware;
using Maus.Core.Modules.M09Gpu;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Une ligne de la liste des pilotes.</summary>
public sealed record DriverRowViewModel(string Group, string Device, string Version, string Date, DateTime? DateValue, string Age, string Provider, string Package, string Signature)
{
    public override string ToString() => Device;

    public static DriverRowViewModel From(DriverEntry driver) => From(driver, DateTime.Today);

    public static DriverRowViewModel From(DriverEntry driver, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return new(
        DriverInventoryReader.GroupName(driver.Group),
        driver.Device,
        driver.Version ?? "—",
        driver.Date?.ToString("d", Culture) ?? "—",
        driver.Date,
        DriverAge.Describe(driver, today),
        driver.Provider ?? driver.Manufacturer ?? "—",
        driver.InfName ?? "—",
        driver.IsSigned switch
        {
            true => driver.Signer ?? T("signé"),
            false => T("non signé"),
            null => "—",
        });
    }

    /// <summary>La ligne contient le texte recherché (nom, famille, version, éditeur, paquet ou signature).</summary>
    public bool Matches(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Length == 0
        || Device.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Group.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Version.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Provider.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Package.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Signature.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>Carte d'une carte graphique dans l'onglet Pilotes : son pilote en détail et les actions manuelles.</summary>
public sealed class GraphicsDriverCardViewModel
{
    public override string ToString() => Title;

    public GraphicsDriverCardViewModel(DriverEntry driver, Uri? downloadPage, Func<GraphicsDriverAction, DriverEntry, Task> run, Action<Uri> open)
    {
        ArgumentNullException.ThrowIfNull(driver);

        Driver = driver;
        Lines =
        [
            new(T("Version du pilote"), driver.Version ?? "—"),
            new(T("Date du pilote"), driver.Date?.ToString("d", Culture) ?? "—"),
            new(T("Éditeur"), driver.Provider ?? "—"),
            new(T("Fabricant"), driver.Manufacturer ?? "—"),
            new(T("Paquet du pilote"), driver.InfName ?? "—"),
            new(T("Signature"), driver.IsSigned == true ? driver.Signer ?? T("signé") : driver.IsSigned == false ? T("non signé") : "—"),
            new(T("Identifiant du périphérique"), driver.InstanceId),
        ];
        CanBackupOrRemove = GraphicsDriverActions.CanBackupOrRemove(driver);
        CanReinstall = GraphicsDriverActions.CanReinstall(driver);
        HasDownloadPage = downloadPage is not null;
        RestartCommand = new AsyncCommand(() => Do(run, GraphicsDriverAction.Restart));
        ReinstallCommand = new AsyncCommand(() => Do(run, GraphicsDriverAction.Reinstall));
        BackupCommand = new AsyncCommand(() => Do(run, GraphicsDriverAction.Backup));
        RemoveCommand = new AsyncCommand(() => Do(run, GraphicsDriverAction.Remove));
        DownloadCommand = new AsyncCommand(() =>
        {
            if (downloadPage is not null)
            {
                open(downloadPage);
            }
        });
    }

    public DriverEntry Driver { get; }

    public string Title => Driver.Device;

    public IReadOnlyList<InfoLine> Lines { get; }

    /// <summary>Pilote tiers (oem#.inf) : sauvegarde et suppression possibles ; un pilote de Windows ne se supprime pas ici.</summary>
    public bool CanBackupOrRemove { get; }

    /// <summary>Carte du bus PCI : « Retirer et redétecter » possible (un adaptateur virtuel ne serait pas recréé).</summary>
    public bool CanReinstall { get; }

    public bool HasDownloadPage { get; }

    public string PackageNote => CanBackupOrRemove
        ? T("Pilote du fabricant ({0}) : il peut être sauvegardé, puis supprimé.", Driver.InfName ?? "?")
        : T("Pilote fourni par Windows ({0}) : MAUS ne propose ni de le sauvegarder ni de le supprimer.", Driver.InfName ?? "?");

    public ICommand RestartCommand { get; }

    public ICommand ReinstallCommand { get; }

    public ICommand BackupCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand DownloadCommand { get; }

    private Task Do(Func<GraphicsDriverAction, DriverEntry, Task> run, GraphicsDriverAction action) => run(action, Driver);
}

/// <summary>
/// Atelier, onglet « Pilotes » : tous les pilotes installés (version, date, éditeur, paquet, signature), en lecture seule,
/// et actions manuelles sur le pilote de la carte graphique (redémarrer, retirer et redétecter, sauvegarder, supprimer,
/// réinstaller une sauvegarde), toujours après confirmation et dans une fenêtre de commande visible. Seul « redémarrer » est
/// affiché à tous : les autres, et les sauvegardes existantes, sont en mode technicien (MainWindow.xaml).
/// </summary>
public sealed partial class WorkshopViewModel
{
    public const int SectionDrivers = 6;

    private IReadOnlyList<DriverEntry> _drivers = [];
    private bool _driversLoaded;
    private bool _isReadingDrivers;
    private string _driverFilter = string.Empty;
    private string _driversStatus = string.Empty;
    private ICommand? _refreshDrivers;
    private ICommand? _copyDrivers;
    private ICommand? _openDriverBackups;
    private ICommand? _restoreDriverBackup;

    public ObservableCollection<DriverRowViewModel> DriverRows { get; } = [];

    public ObservableCollection<GraphicsDriverCardViewModel> GraphicsDrivers { get; } = [];

    public bool IsReadingDrivers
    {
        get => _isReadingDrivers;
        private set => SetProperty(ref _isReadingDrivers, value);
    }

    public string DriversStatus
    {
        get => _driversStatus;
        private set => SetProperty(ref _driversStatus, value);
    }

    /// <summary>Résumé de la liste : nombre de pilotes, pilotes non signés, pilote de fabricant le plus ancien.</summary>
    public string DriversSummary
    {
        get => _driversSummary;
        private set => SetProperty(ref _driversSummary, value);
    }

    private string _driversSummary = string.Empty;

    /// <summary>Au moins un pilote sans signature reconnue par Windows (bouton « Voir les non signés »).</summary>
    public bool HasUnsignedDrivers => DriverAge.UnsignedCount(_drivers) > 0;

    /// <summary>Filtre la liste sur les pilotes non signés.</summary>
    public ICommand ShowUnsignedDriversCommand => _showUnsigned ??= new AsyncCommand(() =>
    {
        DriverFilter = T("non signé");
    });

    private ICommand? _showUnsigned;

    /// <summary>Texte recherché dans la liste (nom, famille, version, éditeur, paquet).</summary>
    public string DriverFilter
    {
        get => _driverFilter;
        set
        {
            if (SetProperty(ref _driverFilter, value ?? string.Empty))
            {
                ShowDriverRows();
            }
        }
    }

    public ICommand RefreshDriversCommand => _refreshDrivers ??= new AsyncCommand(LoadDriversAsync);

    /// <summary>Copie la liste des pilotes en texte (nom d'utilisateur, nom du PC et e-mails masqués).</summary>
    public ICommand CopyDriversCommand => _copyDrivers ??= new AsyncCommand(() =>
    {
        if (_drivers.Count == 0)
        {
            return;
        }

        var text = new System.Text.StringBuilder(T("Pilotes relevés par MAUS {0} le {1}", AppVersion.Display, DateTime.Now.ToString("g", Culture))).AppendLine();
        foreach (var group in _drivers.GroupBy(d => d.Group))
        {
            text.AppendLine().AppendLine(DriverInventoryReader.GroupName(group.Key));
            foreach (var driver in group)
            {
                var row = DriverRowViewModel.From(driver);
                text.Append("  - ").Append(row.Device).Append(" : ").Append(row.Version).Append(" · ").Append(row.Date)
                    .Append(" · ").Append(row.Provider).Append(" · ").AppendLine(row.Package);
            }
        }

        DriversStatus = ClipboardText.TryCopy(text.ToString().TrimEnd())
            ? T("Liste copiée (nom d'utilisateur, nom du PC et e-mails masqués) : collez-la avec Ctrl+V.")
            : ClipboardText.Busy;
    });

    /// <summary>
    /// Ouvre la page « Mises à jour facultatives » de Windows Update (adresse documentée par Microsoft), où Windows propose des
    /// pilotes : l'utilisateur choisit lui-même ce qu'il installe, MAUS n'installe aucun pilote.
    /// </summary>
    public ICommand OpenDriverUpdatesCommand => _openDriverUpdates ??= new AsyncCommand(() =>
    {
        ShellLauncher.OpenSettings("ms-settings:windowsupdate-optionalupdates");
    });

    private ICommand? _openDriverUpdates;

    /// <summary>Ouvre le dossier des pilotes sauvegardés par MAUS.</summary>
    public ICommand OpenDriverBackupsCommand => _openDriverBackups ??= new AsyncCommand(async () =>
    {
        // Le dossier est réservé aux administrateurs (comme le journal) : le moteur liste les sauvegardes.
        if (await ReadDriverBackupsAsync() is not { } backups)
        {
            return;
        }

        DriversStatus = backups.Count == 0
            ? T("Aucune sauvegarde de pilote pour l'instant.")
            : T("{0} sauvegarde(s) dans {1} : {2}. « Réinstaller une sauvegarde… » permet d'en choisir une.", backups.Count, GraphicsDriverActions.BackupRoot, string.Join(", ", backups));
    });

    /// <summary>Réinstalle un pilote sauvegardé : choix du dossier (dans les sauvegardes de MAUS), confirmation, console visible.</summary>
    public ICommand RestoreDriverBackupCommand => _restoreDriverBackup ??= new AsyncCommand(RestoreDriverBackupAsync);

    /// <summary>Choix d'une sauvegarde dans la liste du moteur ; <c>null</c> si l'utilisateur renonce. Remplaçable pour les tests.</summary>
    public Func<IReadOnlyList<string>, string?> PickDriverBackup { get; init; } = backups =>
        Views.ChoiceWindow.Pick(T("Réinstaller une sauvegarde"), T("Choisissez la sauvegarde du pilote à réinstaller (la plus récente en premier) :"), backups);

    private async Task LoadDriversAsync()
    {
        if (IsReadingDrivers)
        {
            return;
        }

        Maus.Core.Diagnostics.Breadcrumbs.Add("Pilotes : lecture de la liste");
        _driversLoaded = true;
        IsReadingDrivers = true;
        DriversStatus = T("Lecture des pilotes installés…");
        try
        {
            var reading = await EngineHost.Current.ReadDriversAsync(CancellationToken.None);
            _drivers = reading.Drivers;
            ShowDriverRows();
            ShowGraphicsDrivers(reading.Hardware);
            DriversSummary = Summary(_drivers);
            OnPropertyChanged(nameof(HasUnsignedDrivers));
            DriversStatus = _drivers.Count == 0
                ? T("Windows n'a pas donné la liste des pilotes (service WMI indisponible ?).")
                : T("{0} pilotes, lus dans Windows sans rien modifier. Les actions sur la carte graphique demandent toujours votre accord.", _drivers.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _driversLoaded = false;
            DriversStatus = T("La lecture des pilotes a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsReadingDrivers = false;
        }
    }

    private void ShowDriverRows()
    {
        var filter = DriverFilter.Trim();
        DriverRows.Clear();
        foreach (var row in _drivers.Select(DriverRowViewModel.From).Where(r => r.Matches(filter)))
        {
            DriverRows.Add(row);
        }
    }

    private void ShowGraphicsDrivers(HardwareProfile hardware)
    {
        GraphicsDrivers.Clear();
        var catalog = GpuDriverCatalog.Default;
        foreach (var driver in _drivers.Where(d => d.Group == DriverGroup.Graphics))
        {
            // Page officielle : celle de la branche du catalogue du Module 9, sinon celle du fabricant.
            var gpu = hardware.Gpus.FirstOrDefault(g => string.Equals(g.PnpDeviceId, driver.InstanceId, StringComparison.OrdinalIgnoreCase));
            Uri? page = null;
            if (gpu is not null && gpu.Vendor is HardwareVendor.Nvidia or HardwareVendor.Amd or HardwareVendor.Intel)
            {
                var url = catalog.FindBranch(gpu.Vendor, gpu.Name)?.DownloadUrl ?? catalog.DownloadUrlFor(gpu.Vendor);
                page = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
            }

            GraphicsDrivers.Add(new GraphicsDriverCardViewModel(driver, page, RunGraphicsDriverActionAsync, ShellLauncher.OpenUrl));
        }
    }

    private async Task RunGraphicsDriverActionAsync(GraphicsDriverAction action, DriverEntry gpu)
    {
        if (_driverActionRunning)
        {
            return;
        }

        var folder = GraphicsDriverActions.BackupFolder(gpu.InfName ?? "pilote", DateTimeOffset.Now);
        if (!GraphicsDriverActions.CanRun(action, gpu, folder))
        {
            DriversStatus = T("Action impossible pour ce pilote (pilote de Windows ou identifiant inattendu).");
            return;
        }

        // /delete-driver /uninstall retire le paquet de TOUS les périphériques qui l'utilisent (Microsoft Learn, « PnPUtil Command Syntax »).
        var sharing = _drivers
            .Where(d => !string.Equals(d.InstanceId, gpu.InstanceId, StringComparison.OrdinalIgnoreCase)
                && d.InfName is not null && string.Equals(d.InfName, gpu.InfName, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Device)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var sharedNote = sharing.Count == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + T("Attention : ce même pilote ({0}) sert aussi à : {1}. Ils perdront eux aussi ce pilote.", gpu.InfName ?? "?", string.Join(", ", sharing));

        var (title, message) = action switch
        {
            GraphicsDriverAction.Restart => (T("Redémarrer le pilote graphique ?"),
                T("MAUS va redémarrer le pilote de « {0} » avec pnputil, l'outil de Microsoft. L'écran devient noir quelques secondes puis revient. "
                    + "Utile quand l'image se fige, clignote ou qu'un jeu a laissé la carte dans un mauvais état.", gpu.Device) + Environment.NewLine + Environment.NewLine
                + T("Fermez d'abord les jeux et les logiciels 3D ou vidéo : ils peuvent se fermer ou planter pendant le redémarrage.")),
            GraphicsDriverAction.Reinstall => (T("Retirer et redétecter la carte graphique ?"),
                T("MAUS va retirer « {0} » de la liste des périphériques puis demander à Windows de la redétecter, qui réinstalle alors son pilote. "
                    + "L'écran peut devenir noir plusieurs secondes. À essayer si le redémarrage du pilote ne suffit pas.", gpu.Device) + Environment.NewLine + Environment.NewLine
                + T("Fermez d'abord les jeux et les logiciels 3D ou vidéo.")),
            GraphicsDriverAction.Backup => (T("Sauvegarder le pilote graphique ?"),
                T("MAUS va copier le paquet du pilote de « {0} » ({1}) dans un sous-dossier daté de {2}. Rien n'est modifié : la sauvegarde permet de réinstaller ce pilote plus tard.", gpu.Device, gpu.InfName ?? "?", GraphicsDriverActions.BackupRoot)),
            _ => (T("Supprimer le pilote graphique ?"),
                T("MAUS va d'abord créer un point de restauration de Windows (vérifié), puis sauvegarder le pilote de « {0} » ({1}) dans un sous-dossier daté de {2}, et seulement ensuite le supprimer avec pnputil. Sans sauvegarde réussie, rien n'est supprimé.", gpu.Device, gpu.InfName ?? "?", GraphicsDriverActions.BackupRoot) + Environment.NewLine + Environment.NewLine
                + T("Ensuite, Windows affiche l'image avec son pilote graphique de base : image moins fluide, pas de jeux 3D, définition parfois limitée. Windows Update peut réinstaller un pilote de lui-même.") + Environment.NewLine
                + T("Téléchargez AVANT le nouveau pilote sur le site du fabricant (bouton « Page officielle du pilote ») : vous en aurez besoin pour retrouver une image normale.") + Environment.NewLine
                + T("Les logiciels du fabricant (NVIDIA App, AMD Software, Intel Graphics) restent installés. Pour un nettoyage complet, DDU (téléchargé uniquement sur wagnardsoft.com), lancé en mode sans échec, reste l'outil de référence.") + sharedNote),
        };

        if (!_confirm(title, Maus.Core.Reporting.ConfirmationText.Build(message, reminder: Maus.Core.Legal.Disclaimer.OperationReminder)))
        {
            return;
        }

        _driverActionRunning = true;
        try
        {
            if (action == GraphicsDriverAction.Remove)
            {
                // Point de restauration vérifié avant la suppression (même mécanisme que les corrections) ; s'il échoue,
                // l'utilisateur décide de continuer ou non : la sauvegarde du pilote reste faite de toute façon.
                DriversStatus = T("Création du point de restauration…");
                IsCreatingRestorePoint = true;
                Maus.Core.Fixes.RestorePointOutcome point;
                try
                {
                    point = await EngineHost.Current.CreateDriverRestorePointAsync(CancellationToken.None);
                }
                finally
                {
                    IsCreatingRestorePoint = false;
                }

                if (!point.Succeeded && !_confirm(T("Point de restauration impossible"),
                        point.Message + Environment.NewLine + Environment.NewLine
                        + T("Supprimer le pilote quand même ? Il sera sauvegardé avant, et la sauvegarde permet de le réinstaller.")))
                {
                    DriversStatus = T("Suppression annulée : aucun pilote n'a été touché.");
                    return;
                }
            }

            var outcome = await Views.ToolWindow.RunAsync(T("Pilote graphique"), progress => EngineHost.Current.RunToolAsync(
                new Maus.Core.Engine.Broker.ToolRequest(Maus.Core.Engine.Broker.ToolKind.DriverAction, Action: action, InstanceId: gpu.InstanceId), progress, CancellationToken.None));
            DriversStatus = outcome.Ran
                ? T("{0} Cliquez sur « Actualiser la liste ».", outcome.Message ?? string.Empty)
                : outcome.Message ?? T("L'outil n'a pas pu démarrer.");
        }
        finally
        {
            _driverActionRunning = false;
        }
    }

    private bool _driverActionRunning;

    /// <summary>Un point de restauration est en cours de création : fermer MAUS à ce moment-là pose la question.</summary>
    public bool IsCreatingRestorePoint { get; private set; }

    /// <summary>Résumé : nombre de pilotes, pilotes non signés, pilote de fabricant le plus ancien (sans seuil « trop vieux »).</summary>
    private static string Summary(IReadOnlyList<DriverEntry> drivers)
    {
        var parts = new List<string> { T("{0} pilotes", drivers.Count) };
        var unsigned = DriverAge.UnsignedCount(drivers);
        parts.Add(unsigned == 0
            ? T("tous signés")
            : T("{0} non signé(s) : Windows ne leur voit pas de signature numérique, vérifiez qu'ils viennent bien du fabricant", unsigned));
        if (DriverAge.OldestVendorDriver(drivers) is { Date: { } date } oldest)
        {
            parts.Add(T("pilote de fabricant le plus ancien : {0} ({1}, {2})", oldest.Device, date.ToString("d", Culture), DriverAge.Describe(oldest, DateTime.Today)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Liste des sauvegardes selon le moteur ; <c>null</c>, avec la raison affichée, s'il refuse de lire le dossier.</summary>
    private async Task<IReadOnlyList<string>?> ReadDriverBackupsAsync()
    {
        try
        {
            return await EngineHost.Current.DriverBackupsAsync(CancellationToken.None);
        }
        catch (Maus.Core.Engine.Broker.EngineException ex)
        {
            DriversStatus = ex.Message;
            return null;
        }
    }

    private async Task RestoreDriverBackupAsync()
    {
        if (await ReadDriverBackupsAsync() is not { } backups)
        {
            return;
        }

        if (backups.Count == 0)
        {
            DriversStatus = T("Aucune sauvegarde de pilote pour l'instant.");
            return;
        }

        if (PickDriverBackup(backups) is not { } chosen)
        {
            return;
        }

        if (!_confirm(T("Réinstaller ce pilote ?"), Maus.Core.Reporting.ConfirmationText.Build(T("MAUS va réinstaller le pilote sauvegardé dans {0} avec pnputil. L'écran peut devenir noir quelques secondes.", Path.Combine(GraphicsDriverActions.BackupRoot, chosen)), reminder: Maus.Core.Legal.Disclaimer.OperationReminder)))
        {
            return;
        }

        // Le moteur vérifie le nom (dossier de sauvegarde de MAUS contenant un .inf) et construit lui-même la commande.
        var outcome = await Views.ToolWindow.RunAsync(T("Pilote graphique"), progress => EngineHost.Current.RunToolAsync(
            new Maus.Core.Engine.Broker.ToolRequest(Maus.Core.Engine.Broker.ToolKind.DriverRestore, Backup: chosen), progress, CancellationToken.None));
        DriversStatus = outcome.Message ?? T("L'outil n'a pas pu démarrer.");
    }
}
