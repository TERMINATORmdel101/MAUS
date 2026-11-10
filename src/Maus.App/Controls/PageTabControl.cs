using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Maus.App.Controls;

/// <summary>
/// Le cadre des pages de la fenêtre : son modèle porte aussi la barre de gauche et le bandeau du haut. Un onglet WPF ne présente
/// aux lecteurs d'écran que ses pages : sans cela, ces boutons, dont « Toutes les fonctionnalités », n'existeraient pas pour eux.
/// </summary>
public sealed class PageTabControl : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PagePeer(this);

    private sealed class PagePeer(PageTabControl owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = base.GetChildrenCore() ?? [];
            Collect((DependencyObject)Owner, children);
            return children;
        }

        /// <summary>
        /// Tout ce que le modèle affiche, sauf les pages et le contenu de la page ouverte, que l'onglet présente déjà. Une zone qui
        /// contient la liste des pages (le défilement de la barre de gauche) n'a pas de pair : il présenterait chaque page une seconde fois.
        /// </summary>
        private void Collect(DependencyObject parent, List<AutomationPeer> children)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TabItem or Panel { IsItemsHost: true } || child is FrameworkElement { Name: "PART_SelectedContentHost" })
                {
                    continue;
                }

                if (child is UIElement element && !HostsPages(element) && UIElementAutomationPeer.CreatePeerForElement(element) is { } peer)
                {
                    children.Add(peer);
                }
                else
                {
                    Collect(child, children);
                }
            }
        }

        private bool HostsPages(UIElement element) =>
            ((ItemsControl)Owner).ItemContainerGenerator.ContainerFromIndex(0) is Visual firstPage && firstPage.IsDescendantOf(element);
    }
}
