using System.IO;
using System.Text;
using System.Windows.Input;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels.Workshop;

/// <summary>Atelier, En direct : relevé pendant une partie (une mesure par seconde, bilan, export CSV).</summary>
public sealed partial class WorkshopViewModel
{
    private SessionRecording? _recording;
    private SessionRecording? _lastRecording;
    private TimeSpan? _recordLimit;
    private bool _isRecording;
    private string _sessionStatus = T("Lancez l'enregistrement, jouez, puis arrêtez-le (ou laissez-le s'arrêter tout seul) : MAUS fait le bilan. Il continue même si vous quittez cet onglet ou réduisez la fenêtre.");
    private string _sessionSummary = string.Empty;
    private TestOption<TimeSpan?>? _sessionDuration;
    private ICommand? _startSession;
    private ICommand? _stopSession;
    private ICommand? _exportSession;

    /// <summary>Durées proposées (créées avec l'atelier, donc dans la langue choisie).</summary>
    public IReadOnlyList<TestOption<TimeSpan?>> SessionDurations { get; } =
    [
        new(T("15 minutes"), TimeSpan.FromMinutes(15)),
        new(T("30 minutes (recommandé)"), TimeSpan.FromMinutes(30)),
        new(T("1 heure"), TimeSpan.FromHours(1)),
        new(T("2 heures"), TimeSpan.FromHours(2)),
        new(T("jusqu'à l'arrêt"), null),
    ];

    public TestOption<TimeSpan?> SessionDuration
    {
        get => _sessionDuration ?? SessionDurations[1];
        set => SetProperty(ref _sessionDuration, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(IsNotRecording));
                _ = RefreshActivityAsync();
            }
        }
    }

    public bool IsNotRecording => !IsRecording;

    public string SessionStatus
    {
        get => _sessionStatus;
        private set => SetProperty(ref _sessionStatus, value);
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set
        {
            if (SetProperty(ref _sessionSummary, value))
            {
                OnPropertyChanged(nameof(HasSessionSummary));
            }
        }
    }

    public bool HasSessionSummary => SessionSummary.Length > 0;

    public ICommand StartSessionCommand => _startSession ??= new AsyncCommand(() => Run(() =>
    {
        _recording = new SessionRecording(DateTimeOffset.Now);
        _recordLimit = SessionDuration.Value;
        SessionSummary = string.Empty;
        SessionStatus = T("Enregistrement en cours… lancez votre jeu.");
        IsRecording = true;
    }));

    public ICommand StopSessionCommand => _stopSession ??= new AsyncCommand(() => Run(FinishSession));

    public ICommand ExportSessionCommand => _exportSession ??= new AsyncCommand(ExportSessionAsync);

    /// <summary>Appelé à chaque mesure : ajoute l'échantillon et arrête l'enregistrement à la durée choisie.</summary>
    private void OnRecordedSample(SensorSnapshot snapshot)
    {
        if (_recording is not { } recording)
        {
            return;
        }

        recording.Add(snapshot);
        SessionStatus = T("Enregistrement en cours : {0:hh\\:mm\\:ss}, {1} mesures.", recording.Elapsed, recording.Count);
        if (_recordLimit is { } limit && recording.Elapsed >= limit)
        {
            FinishSession();
        }
    }

    private void FinishSession()
    {
        if (_recording is not { } recording)
        {
            return;
        }

        _recording = null;
        _lastRecording = recording;
        IsRecording = false;
        if (recording.Count < 5)
        {
            SessionStatus = T("Enregistrement trop court pour un bilan.");
            return;
        }

        var summary = recording.Summarize(Live.CpuMaxC);
        string Stat(string label, SessionStat stat, string unit) => stat.Max is null
            ? string.Empty
            : T("{0} : moyenne {1:0} {3}, maximum {2:0} {3}", label, stat.Average, stat.Max, unit) + Environment.NewLine;
        var text = new StringBuilder();
        text.Append(Stat(T("Processeur"), summary.CpuLoad, "%"))
            .Append(Stat(T("Fréquence du processeur"), summary.CpuMhz, "MHz"))
            .Append(Stat(T("Température du processeur"), summary.CpuTemperature, "°C"))
            .Append(Stat(T("Puissance du processeur"), summary.CpuPower, "W"))
            .Append(Stat(T("Carte graphique"), summary.GpuLoad, "%"))
            .Append(Stat(T("Température de la carte graphique"), summary.GpuTemperature, "°C"))
            .Append(Stat(T("Fréquence de la carte graphique"), summary.GpuClock, "MHz"))
            .Append(Stat(T("Puissance de la carte graphique"), summary.GpuPower, "W"))
            .Append(Stat(T("Mémoire vive"), summary.Memory, "%"))
            .AppendLine();
        foreach (var finding in summary.Findings)
        {
            text.AppendLine("• " + finding);
        }

        SessionSummary = text.ToString().TrimEnd();
        SessionStatus = T("Session terminée : {0:hh\\:mm\\:ss}, {1} mesures.", summary.Duration, summary.Samples);
    }

    private async Task ExportSessionAsync()
    {
        if (_lastRecording is not { } recording)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = T("MAUS-session") + $"-{recording.Start:yyyy-MM-dd-HHmm}.csv",
            DefaultExt = ".csv",
            Filter = T("Tableau CSV") + " (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Format régional de Windows (séparateur, virgule décimale), pas celui de la langue de MAUS : c'est lui qu'Excel attend.
            // Encodage avec BOM : Excel reconnaît alors les accents des en-têtes.
            await File.WriteAllTextAsync(dialog.FileName, recording.ToCsv(RegionalCulture), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SessionStatus = T("Relevé enregistré : {0}", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SessionStatus = T("Le relevé n'a pas pu être enregistré : {0}", ex.Message);
        }
    }
}
