namespace Maus.Core;

/// <summary>Adresses du projet sur GitHub (dépôt du porteur). MAUS ne fait que les ouvrir dans le navigateur, à la demande.</summary>
public static class ProjectLinks
{
    public static Uri Repository { get; } = new("https://github.com/TERMINATORmdel101/MAUS");

    /// <summary>Site d'aide du porteur du projet (aussi « site web » et « assistance » de la fiche du Microsoft Store).</summary>
    public static Uri HelpSite { get; } = new("https://sites.google.com/view/maus-help/accueil");

    /// <summary>Nouveau signalement, avec le modèle « Signaler un problème » du dépôt (.github/ISSUE_TEMPLATE/probleme.md).</summary>
    public static Uri NewIssue { get; } = new("https://github.com/TERMINATORmdel101/MAUS/issues/new?template=probleme.md");
}
