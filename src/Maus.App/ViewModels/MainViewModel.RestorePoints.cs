using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using Maus.Core.Fixes;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>
/// Onglet Historique : les points de restauration de Windows (demande du porteur, 27/10/2026). La liste et la création
/// demandent les droits administrateur (moteur) ; revenir à un point se fait avec l'outil de Windows (rstrui), qui demande
/// lui-même l'autorisation.
/// </summary>
public sealed partial class MainViewModel
{
    private string _restorePoints = T("Les points de restauration de Windows permettent de remettre les réglages et les programmes du système comme à une date passée, sans toucher à vos documents. « Afficher les points » les liste (Windows demande l'autorisation administrateur).");
    private ICommand? _showRestorePoints;
    private ICommand? _createManualRestorePoint;
    private ICommand? _openSystemRestore;

    public string RestorePointsText
    {
        get => _restorePoints;
        private set => SetProperty(ref _restorePoints, value);
    }

    public ICommand ShowRestorePointsCommand => _showRestorePoints ??= new AsyncCommand(ShowRestorePointsAsync);

    public ICommand CreateManualRestorePointCommand => _createManualRestorePoint ??= new AsyncCommand(CreateManualRestorePointAsync);

    /// <summary>Outil de Windows pour revenir à un point (Restauration du système), qui demande lui-même l'autorisation.</summary>
    public ICommand OpenSystemRestoreCommand => _openSystemRestore ??= new AsyncCommand(() =>
    {
        ShellLauncher.OpenFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rstrui.exe"));
        return Task.CompletedTask;
    });

    private async Task ShowRestorePointsAsync()
    {
        RestorePointsText = T("Lecture des points de restauration…");
        try
        {
            RestorePointsText = Describe(await EngineHost.Current.ReadRestorePointsAsync(CancellationToken.None));
        }
        catch (Maus.Core.Engine.Broker.EngineException ex)
        {
            RestorePointsText = ex.Message;
        }
    }

    private async Task CreateManualRestorePointAsync()
    {
        if (!Confirm(T("Créer un point de restauration ?"),
                T("Windows va enregistrer l'état actuel du système (réglages, pilotes, programmes), sans vos documents. Cela prend en général moins d'une minute et utilise un peu d'espace disque. Continuer ?")))
        {
            return;
        }

        RestorePointsText = T("Création du point de restauration…");
        try
        {
            var outcome = await EngineHost.Current.CreateRestorePointAsync(CancellationToken.None);
            var reading = await EngineHost.Current.ReadRestorePointsAsync(CancellationToken.None);
            RestorePointsText = outcome.Message + Environment.NewLine + Environment.NewLine + Describe(reading);
        }
        catch (Maus.Core.Engine.Broker.EngineException ex)
        {
            RestorePointsText = ex.Message;
        }
    }

    private static string Describe(Maus.Core.Engine.Broker.RestorePointsReading reading)
    {
        var text = new StringBuilder();
        text.AppendLine(reading.Protection switch
        {
            ProtectionState.Enabled => T("Protection du système : activée sur le lecteur de Windows."),
            ProtectionState.Disabled => T("Protection du système : désactivée sur le lecteur de Windows. Aucun point ne peut être créé tant qu'elle reste coupée (Paramètres système avancés > Protection du système)."),
            ProtectionState.DisabledByPolicy => T("Protection du système : coupée par une stratégie de l'organisation."),
            _ => T("Protection du système : état inconnu."),
        });

        if (reading.UsedBytes is { } used)
        {
            text.AppendLine(reading.MaxBytes is > 0 and var max
                ? T("Espace utilisé : {0:0.0} Go sur {1:0.0} Go réservés.", used / 1073741824.0, max / 1073741824.0)
                : T("Espace utilisé : {0:0.0} Go.", used / 1073741824.0));
        }

        if (reading.Points.Count == 0)
        {
            text.Append(T("Aucun point de restauration."));
            return text.ToString();
        }

        text.AppendLine(T("{0} point(s), du plus récent au plus ancien :", reading.Points.Count));
        foreach (var point in reading.Points.Reverse())
        {
            var when = point.CreatedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? T("date inconnue");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"• {when} — {point.Description}"));
        }

        return text.ToString().TrimEnd();
    }
}
