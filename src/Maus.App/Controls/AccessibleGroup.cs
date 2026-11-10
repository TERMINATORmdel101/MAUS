using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Maus.App.Controls;

/// <summary>
/// Présente un petit affichage (icône et texte) aux lecteurs d'écran comme un seul élément, lu par son nom
/// (<c>AutomationProperties.Name</c>). Un nom posé sur un simple cadre n'est jamais lu : un cadre n'existe pas pour eux, et
/// ses morceaux, icônes comprises, seraient annoncés un par un.
/// </summary>
public sealed class AccessibleGroup : Decorator
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private sealed class GroupPeer(AccessibleGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(AccessibleGroup);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
