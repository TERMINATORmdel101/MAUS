using System.IO;
using System.Media;
using System.Net;
using System.Windows.Input;
using Maus.App.Views;
using Maus.Core.Modules.M18Network;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>
/// Atelier, onglet Tests : écran, clavier et souris, haut-parleurs, batterie, balade Wi-Fi (demande du porteur du 1/11/2026).
/// Rien de tout cela ne demande de droits ni ne passe par le moteur.
/// </summary>
public sealed partial class WorkshopViewModel
{
    private static readonly TimeSpan BatteryDuration = TimeSpan.FromMinutes(10);

    private ICommand? _openScreenTest;
    private ICommand? _openInputTest;
    private ICommand? _playLeft;
    private ICommand? _playRight;
    private ICommand? _playBoth;
    private ICommand? _toggleBattery;
    private ICommand? _toggleWifiWalk;
    private SoundPlayer? _player;
    private CancellationTokenSource? _batteryStop;
    private CancellationTokenSource? _wifiStop;
    private string _deviceStatus = string.Empty;
    private string _batteryStatus = string.Empty;
    private string _wifiWalkStatus = string.Empty;
    private double[] _wifiSeries = [];

    public ICommand OpenScreenTestCommand => _openScreenTest ??= new AsyncCommand(() => new ScreenTestWindow().ShowDialog());

    public ICommand OpenInputTestCommand => _openInputTest ??= new AsyncCommand(() => new InputTestWindow().Show());

    public ICommand PlayLeftCommand => _playLeft ??= new AsyncCommand(() => Play(true, false, T("Le son vient-il seulement de la gauche ?")));

    public ICommand PlayRightCommand => _playRight ??= new AsyncCommand(() => Play(false, true, T("Le son vient-il seulement de la droite ?")));

    public ICommand PlayBothCommand => _playBoth ??= new AsyncCommand(() => Play(true, true, T("Le son vient-il des deux côtés ?")));

    public ICommand ToggleBatteryCommand => _toggleBattery ??= new AsyncCommand(ToggleBatteryAsync);

    public ICommand ToggleWifiWalkCommand => _toggleWifiWalk ??= new AsyncCommand(ToggleWifiWalkAsync);

    private bool HasBattery { get; } = BatteryProbe.Read().HasBattery;

    public bool ShowBatteryTest => ShowDeviceTests && HasBattery;

    /// <summary>Ligne de la tuile « Écran et périphériques ».</summary>
    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => SetProperty(ref _deviceStatus, value);
    }

    public string BatteryStatus
    {
        get => _batteryStatus;
        private set => SetProperty(ref _batteryStatus, value);
    }

    public string WifiWalkStatus
    {
        get => _wifiWalkStatus;
        private set => SetProperty(ref _wifiWalkStatus, value);
    }

    public double[] WifiSeries
    {
        get => _wifiSeries;
        private set => SetProperty(ref _wifiSeries, value);
    }

    private void Play(bool left, bool right, string question)
    {
        _player?.Dispose();
        _player = new SoundPlayer(new MemoryStream(TestTone.Wave(left, right)));
        _player.Play();
        DeviceStatus = question;
    }

    private async Task ToggleBatteryAsync()
    {
        if (_batteryStop is { } running)
        {
            await running.CancelAsync();
            return;
        }

        var start = BatteryProbe.Read();
        if (!start.OnBattery || start.Percent is not { } first)
        {
            BatteryStatus = T("Débranchez le chargeur, puis relancez la mesure.");
            return;
        }

        using var stop = new CancellationTokenSource();
        _batteryStop = stop;
        var began = DateTimeOffset.Now;
        try
        {
            while (DateTimeOffset.Now - began < BatteryDuration)
            {
                BatteryStatus = T("Mesure en cours : {0:0} s sur {1:0} s. Utilisez le PC normalement.", (DateTimeOffset.Now - began).TotalSeconds, BatteryDuration.TotalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                if (!BatteryProbe.Read().OnBattery)
                {
                    BatteryStatus = T("Le chargeur a été rebranché : mesure arrêtée.");
                    return;
                }
            }

            var last = BatteryProbe.Read().Percent ?? first;
            BatteryStatus = BatteryProbe.Remaining(first, last, DateTimeOffset.Now - began) is { } left
                ? T("{0} % → {1} % en 10 minutes. Autonomie restante estimée : {2:0.0} h, au même rythme.", first, last, left.TotalHours)
                : T("La batterie n'a pas assez baissé en 10 minutes pour estimer l'autonomie.");
        }
        catch (OperationCanceledException)
        {
            BatteryStatus = T("Mesure arrêtée.");
        }
        finally
        {
            _batteryStop = null;
        }
    }

    private async Task ToggleWifiWalkAsync()
    {
        if (_wifiStop is { } running)
        {
            await running.CancelAsync();
            return;
        }

        using var stop = new CancellationTokenSource();
        _wifiStop = stop;
        var source = new WindowsWifiSource();
        var pinger = new SystemPinger();
        var series = new List<double>();
        try
        {
            var gateway = await Task.Run(ConnectionTest.DefaultGateway, stop.Token);
            while (true)
            {
                var reading = await Task.Run(source.Read, stop.Token);
                if (reading.Link is not { } link)
                {
                    WifiWalkStatus = reading.LocationDenied
                        ? T("Windows refuse de donner le signal : autorisez la localisation dans les Paramètres de Windows.")
                        : T("Pas de connexion Wi-Fi active sur ce PC.");
                    return;
                }

                series.Add(link.SignalPercent);
                if (series.Count > 180)
                {
                    series.RemoveAt(0);
                }

                WifiSeries = [.. series];
                var ping = gateway is IPAddress address ? await pinger.PingAsync(address, TimeSpan.FromSeconds(1), stop.Token) : null;
                WifiWalkStatus = ping is { } ms
                    ? T("Signal : {0} %. Réponse de la box : {1:0} ms. Marchez, la courbe suit.", link.SignalPercent, ms)
                    : T("Signal : {0} %. La box ne répond pas pour l'instant. Marchez, la courbe suit.", link.SignalPercent);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            WifiWalkStatus = T("Balade terminée.");
        }
        finally
        {
            _wifiStop = null;
        }
    }
}
