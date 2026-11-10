using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Atelier, onglet Tests : refroidissement, charge combinée et vraie capacité d'une clé USB (demande du porteur du 1/11/2026).</summary>
public sealed partial class WorkshopViewModel
{
    private static readonly TimeSpan CoolingLoad = TimeSpan.FromMinutes(5);

    private ICommand? _startCooling;
    private ICommand? _startCombined;
    private ICommand? _startUsb;
    private string _coolingStatus = string.Empty;
    private string _combinedStatus = string.Empty;
    private string _usbStatus = string.Empty;
    private double _usbProgress;
    private TestOption<string>? _removableDrive;

    public ICommand StartCoolingCommand => _startCooling ??= new AsyncCommand(RunCoolingTestAsync);

    public ICommand StartCombinedCommand => _startCombined ??= new AsyncCommand(RunCombinedTestAsync);

    public ICommand StartUsbCommand => _startUsb ??= new AsyncCommand(RunUsbTestAsync);

    public string CoolingStatus
    {
        get => _coolingStatus;
        private set
        {
            SetProperty(ref _coolingStatus, value);
            ActiveStatus = value;
        }
    }

    public string CombinedStatus
    {
        get => _combinedStatus;
        private set
        {
            SetProperty(ref _combinedStatus, value);
            ActiveStatus = value;
        }
    }

    public string UsbStatus
    {
        get => _usbStatus;
        private set
        {
            SetProperty(ref _usbStatus, value);
            ActiveStatus = value;
        }
    }

    public double UsbProgress
    {
        get => _usbProgress;
        private set
        {
            SetProperty(ref _usbProgress, value);
            ActiveProgress = value;
        }
    }

    public ObservableCollection<TestOption<string>> RemovableDrives { get; } = [];

    public TestOption<string>? RemovableDrive
    {
        get => _removableDrive;
        set => SetProperty(ref _removableDrive, value);
    }

    private void LoadRemovableDrives()
    {
        RemovableDrives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Removable))
        {
            RemovableDrives.Add(new(T("{0} · {1} libres sur {2}", drive.Name.TrimEnd('\\'), FormatSize(drive.AvailableFreeSpace), FormatSize(drive.TotalSize)), drive.Name));
        }

        RemovableDrive = RemovableDrives.FirstOrDefault();
    }

    /// <summary>Message du bouton « Enregistrer le contrôle technique », dans la carte du bilan.</summary>
    public void ShowCheckHint(string text) => FullCheckLines.Add(text);

    private double? CpuDegrees() => Live.Samples.LastOrDefault()?.CpuTemperatureC;

    private async Task RunCoolingTestAsync()
    {
        if (Busy)
        {
            return;
        }

        await SampleBeforeTestAsync();
        if (!_confirm(T("Lancer le test de refroidissement ?"), Maus.Core.Reporting.ConfirmationText.Build(
            T("30 secondes au repos, 5 minutes de charge sur tous les cœurs, puis jusqu'à 2 minutes de retour au calme : ne lancez rien d'autre pendant ce temps."),
            T("Le PC chauffera et ses ventilateurs accéléreront. Le test s'arrête tout seul si une température devient trop haute.") + HeavyWarning(CpuMode) + TemperatureCaveat())))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await StartTestAsync(cancellation);
        var (idle, load) = (new List<double>(), new List<double>());
        var after = new List<(double Seconds, double C)>();
        try
        {
            CoolingStatus = T("Mesure au repos…");
            await Collect(idle, TimeSpan.FromSeconds(30), cancellation.Token);
            if (idle.Count == 0)
            {
                CoolingStatus = T("MAUS ne lit pas la température du processeur sur ce PC : ce test ne peut rien mesurer. Les capteurs avancés (PawnIO) peuvent la donner.");
                return;
            }

            CoolingStatus = T("Charge en cours : 5 minutes…");
            var progress = new Progress<CpuTestProgress>(p => CpuProgress = Math.Min(100, 100 * p.Elapsed.TotalSeconds / CoolingLoad.TotalSeconds));
            var running = CpuTest.RunAsync(new CpuTestOptions(CoolingLoad, Environment.ProcessorCount, CpuMode.Value), progress, _watchdog.AbortReason, cancellation.Token);
            while (!running.IsCompleted)
            {
                await Collect(load, TimeSpan.FromSeconds(2), CancellationToken.None);
            }

            var result = await running;
            if (result.Aborted)
            {
                Report(TestOutcome.Stopped);
                CoolingStatus = T("Test interrompu : {0}", result.AbortReason);
                return;
            }

            CoolingStatus = T("Retour au calme…");
            var began = DateTimeOffset.Now;
            while (DateTimeOffset.Now - began < TimeSpan.FromMinutes(2) && !cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                if (CpuDegrees() is { } now)
                {
                    after.Add(((DateTimeOffset.Now - began).TotalSeconds, now));
                    if (now <= idle.Average() + CoolingResult.CalmMarginC)
                    {
                        break;
                    }
                }
            }

            if (CoolingResult.From(idle, load, after) is not { } cooling)
            {
                CoolingStatus = T("MAUS n'a pas pu lire la température pendant la charge : pas de résultat.");
                return;
            }

            // Le carnet de santé garde chaque test, daté ; il reprend l'ancien chiffre sans date de cooling.txt.
            var log = Maus.Core.Follow.HealthLogStore.CreateDefault();
            var finished = DateTimeOffset.Now;
            var previous = await Task.Run(log.LastCooling);
            var kept = await Task.Run(() =>
            {
                try
                {
                    log.AddCooling(cooling, finished);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
            Report(TestOutcome.Good);
            CoolingStatus = T("Au repos {0:0} °C, pic à {1:0} °C : +{2:0} °C.", cooling.IdleC, cooling.PeakC, cooling.RiseC)
                + " " + (cooling.BackInSeconds is { } back ? T("Revenu au calme en {0:0} s.", back) : T("Pas revenu au calme en 2 minutes."))
                + (previous?.Cooling is { } last ? " " + T("La dernière fois, le {0:d} : +{1:0} °C.", previous.At.LocalDateTime, last.RiseC) : " " + T("Refaites ce test plus tard : c'est la comparaison qui parle."))
                + (kept ? string.Empty : " " + T("Ce résultat n'a pas pu être noté dans le carnet de santé."));
        }
        catch (OperationCanceledException)
        {
            Report(TestOutcome.Stopped);
            CoolingStatus = T("Test interrompu.");
        }
        finally
        {
            await StopTestAsync();
        }
    }

    private async Task Collect(List<double> into, TimeSpan during, CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.Now + during;
        while (DateTimeOffset.Now < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (CpuDegrees() is { } degrees)
            {
                into.Add(degrees);
            }
        }
    }

    private async Task RunCombinedTestAsync()
    {
        if (Busy)
        {
            return;
        }

        if (VramAdapter?.Value is not { } adapter)
        {
            CombinedStatus = T("Aucune carte graphique testable sur ce PC.");
            return;
        }

        await SampleBeforeTestAsync();
        if (!_confirm(T("Lancer la charge combinée ?"), Maus.Core.Reporting.ConfirmationText.Build(
            T("Pendant 5 minutes, tous les cœurs du processeur travaillent à 100 % pendant que MAUS écrit et relit 1 Go de mémoire vidéo."),
            T("Le PC chauffera et ses ventilateurs accéléreront. Le test s'arrête tout seul si une température devient trop haute.") + HeavyWarning(CpuMode) + TemperatureCaveat())))
        {
            return;
        }

        await RunGuardedTestAsync(status => CombinedStatus = status, async cancellationToken =>
        {
            CombinedStatus = T("Charge combinée en cours : 5 minutes…");
            var progress = new Progress<CpuTestProgress>(p => CpuProgress = Math.Min(100, 100 * p.Elapsed.TotalSeconds / CoolingLoad.TotalSeconds));
            var cpu = CpuTest.RunAsync(new CpuTestOptions(CoolingLoad, Environment.ProcessorCount, CpuMode.Value), progress, _watchdog.AbortReason, cancellationToken);
            var memoryStable = true;
            while (!cpu.IsCompleted)
            {
                var memory = await VramTest.RunAsync(_gpuProvider, adapter, new VramTestOptions(VramSizes[2].Value), null, _watchdog.AbortReason, cancellationToken);
                memoryStable &= memory.Stable || memory.Aborted;
            }

            var result = await cpu;
            Report(result.Aborted ? TestOutcome.Stopped : result.Stable && memoryStable ? TestOutcome.Good : TestOutcome.Problem);
            CombinedStatus = result.Aborted
                ? T("Test interrompu : {0}", result.AbortReason)
                : result.Stable && memoryStable
                    ? T("Aucune erreur en 5 minutes de charge combinée.")
                    : T("ERREURS pendant la charge combinée : processeur {0}, mémoire vidéo {1}. Revenez aux réglages d'origine et vérifiez le refroidissement et l'alimentation.", result.Stable ? T("sans erreur") : T("avec erreurs"), memoryStable ? T("sans erreur") : T("avec erreurs"));
        });
    }

    private async Task RunUsbTestAsync()
    {
        if (Busy)
        {
            return;
        }

        if (RemovableDrive?.Value is not { } root)
        {
            UsbStatus = T("Branchez une clé USB, puis rouvrez cette tuile.");
            return;
        }

        var bytes = RealCapacityTest.Testable(root);
        if (bytes <= 0)
        {
            UsbStatus = T("Pas assez de place libre sur ce support.");
            return;
        }

        if (!_confirm(T("Vérifier la vraie capacité de ce support ?"), Maus.Core.Reporting.ConfirmationText.Build(
            T("MAUS va écrire {0} sur {1}, tout relire, puis retirer ses fichiers. Vos fichiers ne sont pas touchés. Cela peut durer longtemps sur une clé lente.", FormatSize(bytes), root),
            T("Ne retirez pas la clé pendant le test."))))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        try
        {
            await StartTestAsync(cancellation);
            var progress = new Progress<CapacityProgress>(p =>
            {
                UsbProgress = p.Writing ? 50.0 * p.Bytes / p.Total : 50 + (50.0 * p.Bytes / Math.Max(1, p.Total));
                UsbStatus = p.Writing ? T("Écriture : {0} sur {1}", FormatSize(p.Bytes), FormatSize(p.Total)) : T("Relecture : {0} sur {1}", FormatSize(p.Bytes), FormatSize(p.Total));
            });
            var result = await RealCapacityTest.RunAsync(root, bytes, progress, cancellation.Token);
            var whole = result.GoodBytes == result.TestedBytes && result.TestedBytes == bytes;
            Report(result.Aborted ? TestOutcome.Stopped : whole ? TestOutcome.Good : TestOutcome.Problem);
            UsbStatus = result.Aborted
                ? T("Test interrompu : {0} relus correctement.", FormatSize(result.GoodBytes))
                : whole
                    ? T("Capacité vérifiée : les {0} testés se relisent correctement.", FormatSize(bytes))
                    : T("ATTENTION : seuls {0} sur {1} se relisent correctement. Ce support est abîmé, ou annonce plus qu'il ne contient.", FormatSize(result.GoodBytes), FormatSize(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UsbStatus = T("Le test n'a pas pu se dérouler : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            await StopTestAsync();
        }
    }
}
