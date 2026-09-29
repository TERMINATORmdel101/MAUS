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

    public static string Quit => T("Quitter");

    public static string AboutTitle => T("À propos de MAUS");

    public static string AboutVersion => T("Version {0}", Maus.Core.AppVersion.Display);

    public static string AboutMadeBy => T("Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. MAUS audite d'abord sans rien modifier ; chaque correction est expliquée, réversible et ne s'applique qu'avec votre accord.");

    public static string AboutLicenseTitle => T("Licence");

    public static string AboutLicense => T("Logiciel libre et gratuit, sous licence GPL-3.0 : vous pouvez l'utiliser, l'étudier, le modifier et le partager, en gardant la même licence. Le nom « MAUS » et son logo ne sont pas cédés : une version modifiée doit porter un autre nom (voir TRADEMARKS dans le dossier des licences).");

    public static string AboutPrivacyTitle => T("Confidentialité");

    public static string AboutPrivacy => T("Aucune télémétrie : MAUS n'envoie rien. Les rapports, les résumés et les relevés restent sur votre PC ; ils ne quittent votre machine que si vous les partagez vous-même, après les avoir relus.");

    public static string AboutComponentsTitle => T("Composants et sources");

    /// <summary>Composants tiers et sources des données techniques (détail dans THIRD-PARTY-NOTICES, dossier des licences).</summary>
    public static IReadOnlyList<string> AboutComponents =>
    [
        T("LibreHardwareMonitorLib 0.9.6 (MPL-2.0) et ses dépendances DiskInfoToolkit, RAMSPDToolkit (MPL-2.0), HidSharp (Apache-2.0) : capteurs avancés, avec le pilote PawnIO que vous installez vous-même."),
        T("Modules PawnIO.Modules 0.2.11 (LGPL-2.1) : lecture seule du contrôleur mémoire et des puces SPD des barrettes."),
        T("ZenStates-Core (GPL-3.0), memtest86+, coreboot, CoreFreq et le noyau Linux (GPL-2.0) : emplacements des registres de la mémoire, des faits matériels relus par le code de MAUS."),
        T("Fiches techniques Intel et AMD, normes JEDEC, documentation Microsoft et NVIDIA : chaque seuil et chaque réglage cité dans l'application renvoie à sa source."),
        T(".NET et WPF (MIT) : la base de l'application."),
    ];

    public static string ReportProblem => T("Signaler un problème");

    public static string ProjectPage => T("Page du projet");

    public static string LicensesFolder => T("Licences et sources");

    public static string ReportOnGitHub => T("Signaler sur GitHub");

    public static string ReportOnGitHubTip => T("Copie ce résumé, puis ouvre la page de signalement du projet sur GitHub, où vous le collez (compte GitHub gratuit nécessaire). MAUS n'envoie rien lui-même.");

    public static string GitHubPaste => T("Résumé copié : collez-le (Ctrl+V) dans la page GitHub qui vient de s'ouvrir, sous « Résumé copié par MAUS ».");

    /// <summary>Ce que veut dire le nom, sous le logo (en anglais et en espagnol aussi, les initiales restent M, A, U, S).</summary>
    public static string NameMeaning => T("Maintenance · Audit · Updates · Sécurité");

    public static string Continue => T("Continuer");

    public static string DisclaimerTitle => Maus.Core.Legal.Disclaimer.Title;

    public static IReadOnlyList<string> DisclaimerParagraphs => Maus.Core.Legal.Disclaimer.Paragraphs;

    public static string DisclaimerAcceptance => Maus.Core.Legal.Disclaimer.Acceptance;

    public static string ShowDisclaimer => T("Avertissements");

    public static string OperationReminder => Maus.Core.Legal.Disclaimer.OperationReminder;

    public static string NavHome => T("Accueil");

    public static string NavWorkshop => T("Atelier");

    public static string HealthCaption => T("santé du PC · sur 100");

    public static string ScoreWhy => T("Pourquoi ce score ?");

    public static string ScoreWindowTitle => T("Pourquoi ce score ? — MAUS");

    public static string ScoreLostTitle => T("Ce qui retire des points");

    public static string ScoreNothingLost => T("Rien : aucun constat ne retire de points.");

    public static string ScoreRulesTitle => T("Le barème de MAUS");

    /// <summary>Barème du score de santé (voir HealthScore), en clair.</summary>
    public static IReadOnlyList<string> ScoreRules =>
    [
        T("Chaque constat retire des points selon sa gravité : critique 20, importante 10, moyenne 4, faible 1. Sans gravité précisée, la couleur décide : rouge = importante, orange = moyenne, bleu = faible."),
        T("Les optimisations (gravité faible) retirent au plus 10 points en tout, quel que soit leur nombre."),
        T("Le score baisse de moins en moins vite : une longue liste de petits écarts ne fait pas tomber un PC qui fonctionne à 0."),
        T("Un constat critique limite le score à 49 (« à corriger en priorité »), un constat de gravité importante à 74 (« à améliorer »)."),
        T("Un même sujet contrôlé par deux modules (Secure Boot) ne compte qu'une fois. Un constat indéterminé, informatif ou marqué « voulu » ne coûte rien."),
        T("C'est un repère de MAUS pour suivre votre PC dans le temps, pas une mesure officielle. Ce barème date du 29/09/2026 : les scores précédents ne se comparent pas."),
    ];

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

    public static string UpdateSoftware => T("Mettre à jour les logiciels");

    public static string UpdateSoftwareTip => T("Liste les logiciels dont une version plus récente existe (winget, l'outil de Microsoft), puis met à jour ceux que vous cochez, dans une fenêtre visible.");

    public static string SoftwareTitle => T("Logiciels à mettre à jour");

    public static string SoftwareIntro => T("Cochez les logiciels à mettre à jour, puis fermez-les (navigateur, lecteur PDF…). winget télécharge chaque mise à jour depuis la source de l'éditeur et l'installe dans une fenêtre visible, où vous acceptez vous-même leurs conditions. Les mises à jour corrigent surtout des failles et des bugs : elles n'accélèrent pas le PC.");

    public static string SoftwareUpdateSelected => T("Mettre à jour la sélection");

    public static string Cancel => T("Annuler");

    public static string PawnIoTitle => T("Capteurs avancés (pilote PawnIO)");

    public static string InstallPawnIo => T("Installer PawnIO");

    public static string UninstallPawnIo => T("Retirer PawnIO");

    public static string AllSensors => T("Tous les capteurs");

    public static string ComponentColumn => T("Composant");

    public static string SensorColumn => T("Capteur");

    public static string ValueColumn => T("Valeur");

    public static string SectionMemory => T("Mémoire");

    public static string SectionDrivers => T("Pilotes");

    public static string RefreshList => T("Actualiser la liste");

    public static string CopyList => T("Copier la liste");

    public static string DriverBackupsFolder => T("Dossier des sauvegardes");

    public static string RestoreDriverBackup => T("Réinstaller une sauvegarde…");

    public static string DriversGpuTitle => T("Carte graphique : son pilote");

    public static string DriversIntro => T("Redémarrer le pilote répare souvent une image figée, un écran noir ou qui clignote. Retirer et redétecter la carte va plus loin. Sauvegarder puis supprimer le pilote sert à repartir sur une installation propre. Chaque action demande votre accord et s'ouvre dans une fenêtre visible.");

    public static string RestartDriver => T("Redémarrer le pilote");

    public static string RestartDriverTip => T("pnputil /restart-device : l'écran devient noir quelques secondes. Fermez d'abord les jeux.");

    public static string ReinstallDevice => T("Retirer et redétecter");

    public static string ReinstallDeviceTip => T("pnputil /remove-device puis /scan-devices : Windows réinstalle la carte et son pilote.");

    public static string BackupDriver => T("Sauvegarder le pilote");

    public static string RemoveDriver => T("Supprimer le pilote…");

    public static string RemoveDriverTip => T("Sauvegarde d'abord le pilote, puis le supprime (pnputil /delete-driver /uninstall). Téléchargez avant le nouveau pilote.");

    public static string DriverPage => T("Page officielle du pilote");

    public static string DriversAllTitle => T("Tous les pilotes installés");

    public static string DriverFilterLabel => T("Rechercher un pilote");

    public static string ColumnFamily => T("Famille");

    public static string ColumnDevice => T("Périphérique");

    public static string ColumnVersion => T("Version");

    public static string ColumnDate => T("Date");

    public static string ColumnProvider => T("Éditeur");

    public static string ColumnPackage => T("Paquet");

    public static string ColumnSignature => T("Signature");

    public static string ColumnAge => T("Âge");

    public static string ShowUnsignedDrivers => T("Voir les pilotes non signés");

    public static string DriversDateNote => T("Les pilotes de Microsoft datés du 21/06/2006 ne sont pas vieux : Windows leur donne volontairement cette date pour que les pilotes des fabricants gardent la priorité (Raymond Chen, Microsoft, « The Old New Thing », 2017). L'âge seul ne dit pas qu'un pilote pose problème : MAUS ne fixe aucun seuil.");

    public static string ReadMemory => T("Lire la mémoire");

    public static string CopyMemory => T("Copier la fiche");

    public static string MemoryControllerTitle => T("Contrôleur mémoire (réglages appliqués par le BIOS)");

    public static string MemoryAppliedTitle => T("Vitesse appliquée et tension de la mémoire");

    public static string MemoryHonest => T("Les timings affichés ici sont ceux réellement appliqués et ceux annoncés par la barrette. Resserrer les timings ou monter la fréquence se fait dans le BIOS : le gain est souvent de quelques pour cent, surtout dans les jeux limités par le processeur, et une instabilité peut corrompre des fichiers. Testez toujours après un changement (Atelier, Tests : mémoire vive).");

    public static string SessionTitle => T("Relevé pendant une partie");

    public static string Record => T("Enregistrer");

    public static string ExportCsv => T("Enregistrer en CSV");

    public static string ScoreHistoryTitle => T("Historique des scores");

    public static string ScoreHistoryIntro => T("Chaque test réussi est gardé sur ce PC (rien n'est envoyé) : la courbe montre l'effet d'un réglage, d'un nettoyage ou d'une mise à jour. Un écart de quelques pour cent d'un passage à l'autre est normal.");

    public static string WeeklyAudit => T("Audit automatique :");

    public static string WeeklyAuditTip => T("Un audit en lecture seule chaque semaine, sans fenêtre ; MAUS ne se montre que s'il trouve un problème rouge. Rien n'est envoyé.");

    public static string NotificationTitle => T("MAUS a trouvé un problème");

    public static string OpenMaus => T("Ouvrir MAUS");

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

    public static string CopySheet => T("Copier la fiche");

    public static string CopySheetTip => T("Copie toute la fiche en texte, pour un forum ou un signalement. Le nom d'utilisateur, le nom du PC et les e-mails sont masqués ; MAUS n'envoie rien.");

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

    public static string LoadLabel => T("Charge :");

    public static string Start => T("Démarrer");

    public static string StopTest => T("Arrêter le test");

    public static string TestSafety => T("Pendant un test, MAUS surveille les températures lisibles et s'arrête de lui-même au seuil de danger. Fermez les jeux et programmes lourds pour un résultat fiable.");

    public static string CoreTestTitle => T("Processeur : test cœur par cœur (Curve Optimizer, undervolt)");

    public static string CurveProgramTitle => T("Programme complet Curve Optimizer (plusieurs heures)");

    public static string CurveProgramIntro => T("Phase 1 : chaque cœur à son tour, pic de charge d'une seconde, chute au repos, réveil vérifié. Phase 2 : tous les cœurs chargés 5 à 10 secondes puis arrêtés au même instant, en boucle (la brusque chute de charge fait remonter la tension). Le PC est inutilisable pendant le programme ; la charge choisie ci-dessus sert aux deux phases.");

    public static string StartProgram => T("Lancer le programme");

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

    public static string SettingsTitle => T("Paramètres");

    public static string SettingsWindowTitle => T("Paramètres — MAUS");

    public static string SettingsIntro => T("Vos choix s'appliquent tout de suite et sont gardés pour les prochaines ouvertures de MAUS.");

    public static string AppearanceTitle => T("Apparence");

    public static string ThemeLabel => T("Thème");

    public static string AccentLabel => T("Couleurs");

    public static string AccentNote => T("Seules les couleurs des boutons et de la sélection changent. Les couleurs des constats gardent leur sens : vert conforme, bleu à optimiser, or à surveiller, rouge problème.");

    public static string AnimationsTitle => T("Animations");

    public static string AnimationsNote => T("Transitions entre les pages, apparition des cartes, jauge du score. « Comme Windows » suit le réglage Accessibilité > Effets visuels > Effets d'animation.");

    public static string RefreshTitle => T("Vitesse d'actualisation des mesures");

    public static string RefreshNote => T("Vaut pour l'atelier « En direct », les tests de stabilité et la fenêtre de surveillance. Plus c'est rapide, plus les mesures elles-mêmes sollicitent un peu le processeur.");

    public static string MonitorTitle => T("Fenêtre de surveillance");

    public static string MonitorIntro => T("Une fenêtre à part, à garder ouverte pendant un test de stabilité ou un jeu : température, consommation et fréquence de chaque composant (actuelle, minimale, maximale), erreurs matérielles et erreurs de Windows depuis son ouverture.");

    public static string MonitorOnTop => T("Toujours au premier plan");

    public static string OpenMonitor => T("Ouvrir la fenêtre de surveillance");

    public static string MonitorWindowTitle => T("Surveillance — MAUS");

    public static string MonitorReset => T("Remettre à zéro");

    public static string MonitorHardwareErrors => T("Erreurs matérielles (WHEA)");

    public static string MonitorHardwareTip => T("Erreurs signalées par le processeur, la mémoire ou le bus PCI Express (journal Système, source WHEA-Logger), même corrigées : pendant un test de stabilité, il ne doit y en avoir aucune.");

    public static string MonitorWindowsErrors => T("Erreurs de Windows");

    public static string MonitorWindowsTip => T("Événements de niveau Critique ou Erreur des journaux Système et Application (hors WHEA) : pilotes, services, applications qui plantent.");

    public static string MonitorWindowsDetail => T("journaux Système et Application");

    public static string MonitorEvents => T("Voir les événements relevés");

    public static string MonitorSensor => T("Mesure");

    public static string MonitorCurrent => T("Actuelle");

    public static string MonitorMin => T("Min.");

    public static string MonitorMax => T("Max.");

    public static string MonitorTestsHint => T("Pendant un test de stabilité, gardez un œil sur les températures, la consommation, les fréquences et les erreurs matérielles dans une fenêtre à part, même par-dessus un jeu.");

    public static string CalibrationTitle => T("Tension réglée dans le BIOS (pour étalonner la mesure)");

    public static string FindInputs => T("Chercher l'entrée");

    public static string UseInput => T("Utiliser cette entrée");

    public static string ForgetCalibration => T("Oublier l'étalonnage");

    public static string CompareTitle => T("Comparer avec une lecture précédente");

    public static string StartRecording => T("Démarrer le relevé");

    public static string StopRecording => T("Arrêter le relevé");
}
