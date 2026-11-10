namespace Maus.Core;

/// <summary>Adresses du projet sur GitHub (dépôt du porteur). MAUS ne fait que les ouvrir dans le navigateur, à la demande.</summary>
public static class ProjectLinks
{
    public static Uri Repository { get; } = new("https://github.com/TERMINATORmdel101/MAUS");

    /// <summary>Site d'aide du porteur du projet (aussi « site web » et « assistance » de la fiche du Microsoft Store).</summary>
    public static Uri SecurityDesign { get; } = new("https://github.com/TERMINATORmdel101/MAUS/blob/main/docs/store-security-design.md");

    /// <summary>Comparaison avec un autre outil, avec le modèle du dépôt (.github/ISSUE_TEMPLATE/compatibilite.md).</summary>
    public static Uri HardwareReport { get; } = new("https://github.com/TERMINATORmdel101/MAUS/issues/new?template=compatibilite.md");

    /// <summary>Dernière version publiée : MAUS ne vérifie rien tout seul, l'utilisateur regarde dans son navigateur.</summary>
    public static Uri LatestRelease { get; } = new("https://github.com/TERMINATORmdel101/MAUS/releases/latest");

    public static Uri HelpSite { get; } = new("https://sites.google.com/view/maus-help/accueil");

    /// <summary>Nouveau signalement, avec le modèle « Signaler un problème » du dépôt (.github/ISSUE_TEMPLATE/probleme.md).</summary>
    public static Uri NewIssue { get; } = new("https://github.com/TERMINATORmdel101/MAUS/issues/new?template=probleme.md");
}
