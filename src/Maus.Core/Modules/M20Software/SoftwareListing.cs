using Maus.Core.Platform;

namespace Maus.Core.Modules.M20Software;

/// <summary>Logiciels à mettre à jour par winget.</summary>
public sealed record SoftwareListing(bool WingetFound, IReadOnlyList<SoftwareUpdate> Updates)
{
    /// <summary>
    /// Lit la liste dans ce processus, celui de la fenêtre. Ce n'est plus une opération du moteur (0.8.6) : winget, outil propre au
    /// compte de l'utilisateur, n'est jamais lancé avec les droits administrateur (<see cref="ReadOnlyCommandRunner"/> le refuse).
    /// </summary>
    public static async Task<SoftwareListing> ReadHereAsync(CancellationToken cancellationToken)
    {
        var winget = await Task.Run(() => Winget.Locate(new WinRtPackageInventory(), new LocalFileSystemReader()), cancellationToken).ConfigureAwait(false);
        if (winget is null)
        {
            return new SoftwareListing(false, []);
        }

        var updates = await Winget.ListUpgradesAsync(new ReadOnlyCommandRunner(), winget, SoftwareUpdatesModule.ListTimeout, cancellationToken).ConfigureAwait(false);
        return new SoftwareListing(true, updates);
    }
}
