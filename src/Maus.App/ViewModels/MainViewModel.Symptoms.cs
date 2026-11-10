using System.Windows.Input;
using Maus.App.ViewModels.Workshop;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une ligne de « Que se passe-t-il ? » : le symptôme en langage courant, où elle mène, et le bouton qui y va.</summary>
public sealed record SymptomRow(string Title, string Detail, string ActionLabel, ICommand Action)
{
    public override string ToString() => Title;

    public string AccessibleName => Title + " : " + ActionLabel;
}

/// <summary>
/// « Que se passe-t-il ? » (0.8.6) : les réponses qui existent déjà, cherchées par symptôme plutôt que par module. Chaque ligne
/// dit où elle mène : c'est un chemin vers un outil, pas un diagnostic. Aucune détection nouvelle, aucune opération du moteur.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Recalculée à chaque audit : une ligne qui a besoin de l'audit le dit tant qu'il n'est pas fait, et le propose.</summary>
    public IReadOnlyList<SymptomRow> Symptoms =>
    [
        new(T("Mon PC est lent"), T("Mesure pendant une minute ce qui occupe votre PC."), T("Mesurer"), SlowPcCommand),
        FromAudit(T("Mon PC démarre lentement"), T("Ouvre les constats du démarrage : sa durée et les programmes lancés à l'ouverture de session."), new ParameterCommand(_ => ShowModuleFindings("M12"))),
        FromAudit(T("Mon PC se réveille la nuit"), T("Ouvre les constats de l'alimentation : ce qui peut le réveiller."), new ParameterCommand(_ => ShowModuleFindings("M05"))),
        new(T("Mon PC a planté ou s'est éteint tout seul"), T("Ouvre la page Suivi : la carte « Mon PC a planté » montre les 30 derniers jours sur une seule liste."), T("Voir"), new ParameterCommand(_ => SelectedTab = TabFollow)),
        new(T("Je n'ai plus de place sur le disque"), T("Ouvre l'analyse de l'espace disque."), T("Voir"), Open(WorkshopViewModel.SectionStorage)),
        new(T("Internet rame"), T("Ouvre la page Tests : le test de connexion est en bas de la page."), T("Voir"), Open(WorkshopViewModel.SectionTests)),
        new(T("Le son craque ou les jeux saccadent"), T("Ouvre la page Tests : le test de latence est en bas de la page, avec les droits administrateur."), T("Voir"), Open(WorkshopViewModel.SectionTests)),
        FromAudit(T("Mon PC est-il bien protégé ?"), T("Ouvre les constats de la famille Sécurité."), new ParameterCommand(_ => ShowSecurityFindings())),
        FromAudit(T("Je veux demander de l'aide"), T("Prépare un résumé du dernier audit, à relire avant de le copier."), HelpSummaryCommand),
    ];

    /// <summary>Sans audit, la destination serait vide : la ligne le dit, et son bouton lance l'audit.</summary>
    private SymptomRow FromAudit(string title, string detail, ICommand action) => HasAudit
        ? new(title, detail, T("Voir"), action)
        : new(title, T("La réponse vient de l'audit, pas encore lancé dans cette fenêtre. Il ne modifie rien."), HomeAuditLabel, RunAuditCommand);

    private ParameterCommand Open(int section) => new(_ => GoToCommand.Execute(30 + section));

    /// <summary>« Est-il bien protégé ? » : la liste par importance, sur la seule famille Sécurité.</summary>
    private void ShowSecurityFindings()
    {
        FindingFilterChoice = FindingFilterOptions[0];
        FamilyFilterChoice = FamilyFilterOptions.First(o => o.Value == ModuleFamily.Security);
        ShowByModule = false;
        SelectedTab = TabFindings;
    }
}
