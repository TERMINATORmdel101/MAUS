using System.IO;
using System.Windows.Input;
using Maus.Core.Follow;
using Maus.Core.Reporting;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>
/// Page « Suivi », le dossier du PC : le bouton lit le matériel, l'utilisateur relit ce qui sera inclus, puis il enregistre ou
/// imprime. Rien n'est lu à l'ouverture de la page, rien ne part, et le fichier va où l'utilisateur le choisit.
/// </summary>
public sealed partial class MainViewModel
{
    private PcFileDocument? _pcFile;

    private string _pcFilePreview = string.Empty;

    private string _pcFileStatus = string.Empty;

    private bool _isPreparingPcFile;

    private ICommand? _preparePcFile;

    private ICommand? _savePcFile;

    private ICommand? _printPcFile;

    public static string PcFileTitle => T("Le dossier du PC");

    public static string PcFileIntro => T("Une page à imprimer ou à enregistrer pour une revente, une réparation ou une garantie. Le fichier va où vous le choisissez.");

    public static string PcFileIncludes => T("Sera inclus, tel que lu : le matériel, la santé des disques, la batterie, la version de Windows, la date et le dernier score s'il existe. Jamais inclus : nom d'utilisateur, nom du PC, numéro de série, adresse MAC, e-mail. Vous relisez la page avant de l'enregistrer.");

    public static string PcFilePrepareLabel => T("Préparer le dossier");

    public static string PcFileSaveLabel => T("Enregistrer la page…");

    public static string PcFilePrintLabel => T("Imprimer…");

    public static string PcFilePreviewName => T("Contenu du dossier du PC, à relire avant de l'enregistrer");

    public ICommand PreparePcFileCommand => _preparePcFile ??= new AsyncCommand(PreparePcFileAsync);

    public ICommand SavePcFileCommand => _savePcFile ??= new AsyncCommand(SavePcFileAsync);

    public ICommand PrintPcFileCommand => _printPcFile ??= new AsyncCommand(PrintPcFile);

    /// <summary>Choix de l'emplacement de la page (nom proposé) ; <c>null</c> si l'utilisateur renonce. Remplaçable pour les tests.</summary>
    internal Func<string, string?> PickPcFilePath { get; set; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggested,
            DefaultExt = ".html",
            Filter = T("Page web") + " (*.html)|*.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Impression du texte relu ; <c>false</c> si l'utilisateur renonce. Remplaçable pour les tests.</summary>
    internal Func<string, bool> PrintPcFileText { get; set; } = Views.ReportPrinter.Print;

    /// <summary>Ce qui sera écrit, déjà masqué ; vide tant que rien n'est préparé.</summary>
    public string PcFilePreview
    {
        get => _pcFilePreview;
        private set
        {
            if (SetProperty(ref _pcFilePreview, value))
            {
                OnPropertyChanged(nameof(HasPcFilePreview));
            }
        }
    }

    public bool HasPcFilePreview => PcFilePreview.Length > 0;

    public string PcFileStatus
    {
        get => _pcFileStatus;
        private set => SetProperty(ref _pcFileStatus, value);
    }

    /// <summary>Lecture du matériel en cours (hors du fil de la fenêtre).</summary>
    public bool IsPreparingPcFile
    {
        get => _isPreparingPcFile;
        private set => SetProperty(ref _isPreparingPcFile, value);
    }

    internal Task PreparePcFileAsyncForTest() => PreparePcFileAsync();

    internal Task SavePcFileAsyncForTest() => SavePcFileAsync();

    private async Task PreparePcFileAsync()
    {
        if (IsPreparingPcFile)
        {
            return;
        }

        IsPreparingPcFile = true;
        PcFileStatus = T("Lecture du matériel…");
        try
        {
            // Le moteur s'il tourne déjà, sinon sans droits : cette lecture ne demande jamais d'autorisation à Windows.
            var reading = await Engine.ReadInventoryAsync(CancellationToken.None);
            var document = PcFile.Build(
                new PcFileInput
                {
                    At = DateTimeOffset.Now,
                    Inventory = reading.Inventory,
                    Details = reading.Details,
                    Windows = reading.Details?.System.Windows ?? _lastContext?.Windows,
                    ReadWithRights = EngineHost.IsElevated,
                    BatteryDetected = BatteryProbe.Read().HasBattery,
                    Audits = AuditsForPcFile(),
                },
                PrivacyFilter.ForCurrentUser());
            _pcFile = document;
            PcFilePreview = document.ToText();
            PcFileStatus = document.MaskingNote;
        }
        catch (Exception ex)
        {
            Maus.Core.Diagnostics.CrashLog.Write(ex, "suivi : dossier du PC");
            _pcFile = null;
            PcFilePreview = string.Empty;
            PcFileStatus = T("La lecture du matériel a échoué : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
        finally
        {
            IsPreparingPcFile = false;
        }
    }

    /// <summary>
    /// Audits gardés, puis celui de cette ouverture : lui seul a encore les valeurs lues. Sans droits, son score est partiel : il
    /// n'est pas repris, comme il n'est pas gardé.
    /// </summary>
    private List<AuditSnapshot> AuditsForPcFile()
    {
        List<AuditSnapshot> audits = [.. _snapshots];
        if (_lastContext is { } context && _results.Count > 0)
        {
            var current = AuditSnapshot.From(_results, context.Windows.FullBuild, context.Now);
            audits.Add(context.IsElevated ? current : current with { Score = AuditSnapshot.NoScore });
        }

        return audits;
    }

    private async Task SavePcFileAsync()
    {
        if (_pcFile is not { } document || PickPcFilePath(T("MAUS-dossier-du-pc") + $"-{DateTime.Now:yyyy-MM-dd-HHmm}.html") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, document.ToHtml(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            PcFileStatus = T("Dossier enregistré : {0} (ouvrez-le avec votre navigateur pour l'imprimer).", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PcFileStatus = T("Le dossier n'a pas pu être enregistré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
    }

    private void PrintPcFile()
    {
        if (_pcFile is not { } document)
        {
            return;
        }

        try
        {
            if (PrintPcFileText(document.ToText()))
            {
                PcFileStatus = T("Dossier envoyé à l'imprimante.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Printing.PrintSystemException)
        {
            PcFileStatus = T("Le dossier n'a pas pu être imprimé : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
        }
    }
}
