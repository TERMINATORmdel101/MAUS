using System.Windows;
using Maus.Core.Modules.M20Software;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Un logiciel à cocher : les plus exposés (navigateurs, PDF, compression…) sont en tête et cochés.</summary>
public sealed class SoftwareChoice(SoftwareUpdate update)
{
    public SoftwareUpdate Update { get; } = update;

    public bool CanUpdate => Update.CanTarget;

    public bool IsChecked { get; set; } = update.CanTarget;

    public string Title => Update.Name;

    public string Detail => !Update.CanTarget
        ? T("{0} → {1} · identifiant tronqué par winget : à mettre à jour depuis le logiciel lui-même", Update.Version, Update.Available)
        : SoftwareUpdatesModule.IsExposed(Update)
            ? T("{0} → {1} · prioritaire : ouvre des pages ou des fichiers venus d'Internet", Update.Version, Update.Available)
            : T("{0} → {1}", Update.Version, Update.Available);
}

/// <summary>Choix des logiciels à mettre à jour par winget ; rien n'est lancé sans validation.</summary>
public partial class SoftwareUpdatesWindow : Window
{
    private readonly List<SoftwareChoice> _choices;

    public SoftwareUpdatesWindow(IEnumerable<SoftwareUpdate> updates)
    {
        InitializeComponent();
        _choices = updates
            .OrderByDescending(SoftwareUpdatesModule.IsExposed)
            .ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new SoftwareChoice(u))
            .ToList();
        Choices.ItemsSource = _choices;
    }

    public IReadOnlyList<SoftwareUpdate> Selected { get; private set; } = [];

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        Selected = _choices.Where(c => c.IsChecked && c.CanUpdate).Select(c => c.Update).ToList();
        DialogResult = true;
    }
}
