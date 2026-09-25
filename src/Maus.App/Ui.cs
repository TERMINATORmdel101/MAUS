using static Maus.Core.Localization.Texts;

namespace Maus.App;

/// <summary>Textes fixes de l'interface (XAML : <c>{x:Static app:Ui.Nom}</c>), traduits dans la langue active.</summary>
public static class Ui
{
    public static string WindowTitle => T("MAUS — Audit et corrections de Windows");

    public static string Tagline => T("MAUS audite d'abord sans rien modifier. Les corrections ne s'appliquent qu'avec votre accord, et chacune peut être annulée.");

    public static string RunAudit => T("Lancer l'audit");

    public static string TabFindings => T("Constats");

    public static string Wanted => T("C'est voulu");

    public static string WantedTip => T("Ne plus signaler ce constat tant que la situation ne change pas");

    public static string ReportAgain => T("Signaler de nouveau");

    public static string TabFixes => T("Corrections");

    public static string OneClickProfiles => T("Profils en un clic :");

    public static string YourChoices => T("Vos choix");

    public static string GameBarProfile => T("Profil Game Bar :");

    public static string LaptopPower => T("Alimentation du portable :");

    public static string CreateRestorePoint => T("Créer un point de restauration vérifié avant toute modification (recommandé)");

    public static string EnableProtection => T("Activer la protection du système sur C: si elle est désactivée (nécessaire pour le point de restauration)");

    public static string VerifyNote => T("Après les corrections, MAUS relance l'audit pour vérifier l'état réel du PC.");

    public static string SaveReport => T("Enregistrer le rapport (HTML)");

    public static string SaveReportTip => T("Une page à garder ou à imprimer : ce qui a été vu, ce qui a été changé, et comment l'annuler");

    public static string RestartExplorer => T("Redémarrer l'Explorateur");

    public static string RestartExplorerTip => T("Applique les corrections de la barre des tâches sans fermer la session");

    public static string TechnicalDetails => T("Détails techniques");

    public static string TabHistory => T("Historique");

    public static string Refresh => T("Actualiser");

    public static string RevertSession => T("Annuler cette séance");

    public static string SessionChanges => T("Corrections de cette séance");

    public static string Revert => T("Annuler");

    public static string LaptopTitle => T("MAUS — votre portable");

    public static string LaptopQuestion => T("Comment voulez-vous utiliser votre portable ?");

    public static string LaptopIntro => T("MAUS adapte ses conseils d'alimentation à votre choix. Vous pourrez en changer à tout moment dans l'onglet Corrections.");

    public static string LaptopPerformance => T("Performance sur secteur, Équilibré sur batterie (recommandé)");

    public static string LaptopPerformanceDetail => T("Plus de chaleur et de bruit quand le PC est branché ; autonomie préservée sur batterie.");

    public static string LaptopEverywhere => T("Performance partout");

    public static string LaptopEverywhereDetail => T("Le PC reste aussi rapide sur batterie, mais l'autonomie baisse nettement.");

    public static string LaptopBattery => T("Autonomie et silence");

    public static string LaptopBatteryDetail => T("Équilibré sur secteur, économie d'énergie sur batterie : plus frais, plus silencieux, un peu moins réactif.");

    public static string Later => T("Plus tard");

    public static string Validate => T("Valider");

    public static string NavHome => T("Accueil");

    public static string NavWorkshop => T("Atelier");

    public static string HealthCaption => T("santé du PC · sur 100");

    public static string SlowPcTitle => T("Pourquoi mon PC est lent ?");

    public static string SlowPcDetail => T("Une minute de mesures, puis les causes principales et la façon de les corriger.");

    public static string TestPcTitle => T("Tester mon PC");

    public static string TestPcDetail => T("Stabilité du processeur, de la mémoire vive et de la mémoire vidéo, avec arrêt automatique en cas de surchauffe.");

    public static string FixPcTitle => T("Corriger en un clic");

    public static string MyPcTitle => T("Mon matériel");

    public static string MyPcDetail => T("Processeur, mémoire, carte graphique, disques : la fiche complète, avec les seuils de sécurité.");

    public static string LiveTitle => T("En direct");

    public static string LiveDetail => T("Charge, températures, fréquences et consommation, seconde par seconde.");

    public static string ReportTitle => T("Un rapport à garder");

    public static string ReportDetail => T("Une page HTML : ce qui a été vu, ce qui a été changé, et comment l'annuler.");

    public static string SpaceTitle => T("Qu'est-ce qui prend de la place ?");

    public static string SpaceDetail => T("Les dossiers et les fichiers les plus lourds, expliqués, sans rien supprimer.");

    public static string RepairWindows => T("Réparer les fichiers de Windows");

    public static string RepairWindowsTip => T("DISM puis SFC, les outils officiels de Microsoft, dans une fenêtre visible. Utile si Windows plante, si des mises à jour échouent ou si l'audit signale des fichiers abîmés.");

    public static string NetTestTitle => T("Connexion : latence et stabilité");

    public static string NetTestIntro => T("MAUS envoie des « ping » à votre box et à deux serveurs publics très utilisés (Cloudflare 1.1.1.1 et Google 8.8.8.8), pendant une quinzaine de secondes. Aucune donnée personnelle n'est transmise et aucun réglage n'est modifié. Le résultat dit si un souci vient du Wi-Fi ou de la box, ou bien de la ligne.");

    public static string HelpTitle => T("Demander de l'aide");

    public static string HelpDetail => T("Un résumé de votre PC à coller sur un forum, sans données personnelles.");

    public static string HelpWindowTitle => T("Résumé pour demander de l'aide");

    public static string HelpIntro => T("Complétez la première ligne avec votre problème, relisez, puis copiez le texte et collez-le (Ctrl+V) sur un forum, un Discord ou dans un message. MAUS a retiré votre nom d'utilisateur, le nom du PC et les adresses e-mail ; aucun numéro de série n'y figure. Rien n'est envoyé par MAUS.");

    public static string CopyText => T("Copier le texte");

    public static string CloseWindow => T("Fermer");

    public static string Copied => T("Copié : collez-le avec Ctrl+V.");

    public static string CopyFailed => T("Le presse-papiers est occupé : réessayez, ou sélectionnez le texte (Ctrl+A) puis copiez-le (Ctrl+C).");

    public static string WorkshopSubtitle => T("Votre matériel, en direct, comparé à ses limites de sécurité");

    public static string SectionPc => T("Mon PC");

    public static string SectionProcesses => T("Processus");

    public static string SectionTests => T("Tests");

    public static string SearchSheet => T("Rechercher la fiche");

    public static string FilterLabel => T("Filtrer :");

    public static string SearchWith => T("Recherche avec :");

    public static string ColumnProcess => T("Processus");

    public static string ColumnCpu => T("Processeur");

    public static string ColumnMemory => T("Mémoire");

    public static string ColumnDisk => T("Disque");

    public static string ColumnGpu => T("GPU");

    public static string ColumnTrust => T("Confiance");

    public static string SearchWeb => T("Rechercher sur le web");

    public static string ShowFolder => T("Ouvrir l'emplacement");

    public static string Terminate => T("Arrêter le processus");

    public static string PickProcess => T("Choisissez un processus dans la liste pour savoir ce que c'est, d'où il vient et s'il est sûr.");

    public static string PidLabel => T("Identifiant (PID)");

    public static string StartedLabel => T("Démarré le");

    public static string LocationLabel => T("Emplacement");

    public static string CpuTestTitle => T("Processeur : stabilité et performance");

    public static string CpuTestIntro => T("Tous les cœurs calculent des résultats connus d'avance : la moindre erreur trahit un processeur instable (surcadençage, tension trop basse, surchauffe). Le score se compare à vos passages précédents.");

    public static string RamTestTitle => T("Mémoire vive : recherche d'erreurs");

    public static string RamTestIntro => T("MAUS écrit des motifs dans la mémoire puis les relit, et mesure au passage le débit et la latence. Une seule erreur suffit à expliquer des plantages aléatoires.");

    public static string VramTestTitle => T("Mémoire de la carte graphique");

    public static string VramTestIntro => T("MAUS remplit la mémoire de la carte graphique de motifs, puis les relit par Direct3D (déjà présent dans Windows). Une seule erreur trahit une mémoire vidéo instable (surcadençage, chaleur) ou défaillante.");

    public static string CardLabel => T("Carte :");

    public static string DurationLabel => T("Durée :");

    public static string SizeLabel => T("Quantité :");

    public static string Start => T("Démarrer");

    public static string StopTest => T("Arrêter le test");

    public static string TestSafety => T("Pendant un test, MAUS surveille les températures lisibles et s'arrête de lui-même au seuil de danger. Fermez les jeux et programmes lourds pour un résultat fiable.");

    public static string CoreTestTitle => T("Processeur : test cœur par cœur (Curve Optimizer, undervolt)");

    public static string CoreTestIntro => T("Utile si vous avez baissé le Curve Optimizer (AMD) ou fait un undervolt (Intel) : ces réglages lâchent quand un seul cœur monte à sa fréquence maximale, au réveil et aux changements de charge, rarement sous une charge continue. MAUS teste chaque cœur à tour de rôle avec des à-coups et des pauses. Un PC réglé d'origine n'a pas besoin de ce test.");

    public static string CrashTitle => T("Le PC a gelé pendant le dernier test");

    public static string Understood => T("J'ai compris");

    public static string HonestTitle => T("Soyons honnêtes : ce que ces corrections apportent vraiment");

    public static string HonestWindows => T("• Les réglages de Windows (effets visuels, confidentialité, Game Bar, démarrage) rendent le PC plus réactif, plus discret et plus régulier, mais font rarement gagner plus de 1 à 3 % d'images par seconde dans les jeux.");

    public static string HonestHardware => T("• Les vrais gains viennent du matériel bien réglé : mémoire à sa vitesse annoncée (XMP/EXPO), écran à sa bonne fréquence, pilote graphique à jour, Windows sur un SSD, applications inutiles retirées du démarrage. MAUS les signale dans l'audit.");

    public static string HonestOverclock => T("• Overclocking et undervolting : quelques pour cent, très variables d'une puce à l'autre, et seulement après des tests de stabilité (onglet Tests de l'Atelier).");

    public static string HonestSecurity => T("• MAUS ne coupe jamais une protection importante (antivirus, pare-feu, mises à jour, protections du processeur) ni une fonction utile de Windows pour gagner quelques pour cent.");

    public static string HonestWarning => T("• Méfiez-vous des outils qui promettent +30 % : la plupart de leurs réglages sont sans effet mesurable, ou retirent des fonctions de Windows dont vous aurez besoin un jour.");

    public static string SectionStorage => T("Stockage");

    public static string DriveLabel => T("Lecteur :");

    public static string Analyze => T("Analyser");

    public static string Stop => T("Arrêter");

    public static string GoUp => T("Remonter");

    public static string OpenInExplorer => T("Ouvrir dans l'Explorateur");

    public static string StorageSense => T("Libérer de la place avec Windows");

    public static string LargestTitle => T("Les plus gros fichiers");

    public static string OpenEntry => T("Ouvrir");

    public static string ShowEntry => T("Emplacement");

    public static string DiskTestTitle => T("Disque : vitesse de lecture et d'écriture");

    public static string DiskTestIntro => T("MAUS écrit un fichier temporaire, le relit puis le supprime, en contournant le cache de Windows. Un disque bien plus lent que prévu trahit un SSD presque plein ou qui chauffe, un mauvais port M.2, ou Windows installé sur un disque dur.");
}
