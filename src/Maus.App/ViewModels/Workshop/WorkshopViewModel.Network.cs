using System.Text;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Atelier, onglet Tests : test de connexion (latence, gigue et pertes vers la box et vers Internet).</summary>
public sealed partial class WorkshopViewModel
{
    private const int NetRounds = 50;

    private bool _isNetTesting;
    private double _netProgress;
    private string _netStatus = string.Empty;
    private Action? _stopNet;
    private ICommand? _startNetTest;
    private ICommand? _stopNetTest;

    public bool IsNetTesting
    {
        get => _isNetTesting;
        private set
        {
            if (SetProperty(ref _isNetTesting, value))
            {
                OnPropertyChanged(nameof(IsNotNetTesting));
            }
        }
    }

    public bool IsNotNetTesting => !IsNetTesting;

    public double NetProgress
    {
        get => _netProgress;
        private set => SetProperty(ref _netProgress, value);
    }

    public string NetStatus
    {
        get => _netStatus;
        private set => SetProperty(ref _netStatus, value);
    }

    public ICommand StartNetTestCommand => _startNetTest ??= new AsyncCommand(RunNetTestAsync);

    public ICommand StopNetTestCommand => _stopNetTest ??= new AsyncCommand(() => Run(() => _stopNet?.Invoke()));

    private async Task RunNetTestAsync()
    {
        if (IsNetTesting)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _stopNet = cancellation.Cancel;
        IsNetTesting = true;
        NetProgress = 0;
        NetStatus = T("Mesure en cours (une quinzaine de secondes)…");
        try
        {
            var gateway = await Task.Run(ConnectionTest.DefaultGateway);
            var progress = new Progress<double>(p => NetProgress = p);
            var result = await ConnectionTest.RunAsync(new SystemPinger(), gateway, NetRounds, TimeSpan.FromMilliseconds(250), progress, cancellation.Token);
            NetProgress = 100;

            var text = new StringBuilder();
            if (result.Aborted)
            {
                text.AppendLine(T("Test arrêté : résultats partiels."));
            }

            if (result.Gateway is { } box)
            {
                text.AppendLine(ConnectionTest.Describe(box));
            }
            else
            {
                text.AppendLine(T("Box : passerelle introuvable (connexion partagée, VPN ou réseau particulier)."));
            }

            foreach (var server in result.Internet)
            {
                text.AppendLine(ConnectionTest.Describe(server));
            }

            text.AppendLine().AppendLine(ConnectionTest.Advice(result)).Append(ConnectionTest.References);
            NetStatus = text.ToString();
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
        {
            NetStatus = T("Le test n'a pas pu se dérouler : {0}", ex.Message);
        }
        finally
        {
            _stopNet = null;
            IsNetTesting = false;
        }
    }
}
