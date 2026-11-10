using static Maus.Core.Localization.Texts;

namespace Maus.App;

/// <summary>Textes fixes de l'interface (XAML : <c>{x:Static app:Ui.Nom}</c>), traduits dans la langue active.</summary>
public static class Ui
{
    public static string WindowTitle => T("MAUS {0} — Audit et corrections de Windows", Maus.Core.AppVersion.Display);

    public static string Tagline => T("MAUS audite d'abord sans rien modifier. Les corrections ne s'appliquent qu'avec votre accord, et chacune peut être annulée.");

    public static string SeeFindings => T("Lire les constats");

    public static string HomeTools => T("Outils");

    public static string OtherTools => T("Autres outils");

    public static string FixThis => T("Corriger");

    public static string OpenWebPage => T("Ouvrir la page web");

    public static string SeeSource => T("Voir la source");

    public static string FpsTitle => T("Images par seconde");

    public static string DriversToolsTitle => T("Outils des pilotes");

    public static string LiveMeasuresTitle => T("Mesures en direct");

    public static string ProgressLabel => T("Progression");

    public static string AlarmMarginsNote => T("Les limites de température viennent du processeur, de la carte graphique ou de leur fabricant. Les marges autour de ces limites (processeur : 3 °C avant, 5 °C après ; carte graphique : 5 °C avant) et l'alerte de mémoire à 95 % sont choisies par MAUS, sans source publiée.");

    public static string DriversListLabel => T("Liste :");

    public static string DriversBackupsLabel => T("Sauvegardes :");

    public static string DriversUpdateLabel => T("Windows Update :");

    public static string DriversDateWhy => T("Pourquoi certaines dates sont très anciennes ?");

    public static string BenchmarkTitle => T("Benchmark visuel : carte graphique et processeur");

    public static string BenchmarkIntro => T("Une douzaine de minutes en plein écran : cinq scènes calculées image par image (DirectX 11 ou 12), un test de lancer de rayons (DirectX 12, score à part), puis le processeur. Deux fois plus de points = deux fois plus rapide. La carte et le processeur chauffent comme dans un jeu très exigeant ; Échap arrête tout.");

    public static string BenchmarkRayTracingOption => T("Lancer de rayons (DirectX 12, score à part)");

    public static string BenchmarkRayTracingTip => T("Test « Galerie des glaces » : reflets, verre, ombres et lumière calculés par le lancer de rayons matériel de la carte (DXR 1.1). Il faut DirectX 12 et une carte qui le gère : MAUS le vérifie et passe le test sinon. Son score est compté à part, pour que les cartes sans lancer de rayons restent comparables.");

    public static string BenchmarkRayTracingTitle => T("Lancer de rayons (score à part)");

    public static string ApiLabel => T("Interface graphique");

    public static string ResolutionLabel => T("Résolution");

    public static string NavBenchmark => T("Benchmark");

    public static string BenchmarkSubtitle => T("Carte graphique et processeur poussés à fond, en plein écran. Les points se comparent à résolution égale, avec la même version du benchmark.");

    public static string BenchmarkScopeLabel => T("Tests");

    public static string BenchmarkStart => T("Lancer le benchmark");

    public static string BenchmarkNone => T("Aucun benchmark lancé pour l'instant. Le résultat s'affichera ici, avec l'historique de vos mesures.");

    public static string BenchmarkLast => T("Dernier résultat");

    public static string BenchmarkCombined => T("Score combiné");

    public static string BenchmarkCopy => T("Copier le résultat");

    public static string BenchmarkHistoryTitle => T("Historique des mesures");

    public static string BenchmarkMarkersNote => T("Les repères (traits verticaux sur la courbe, phrases dans la liste) disent seulement ce qui a changé entre deux mesures, pas pourquoi les points varient. Deux changements au même endroit ne se démêlent pas, et un écart de quelques pour cent d'une mesure à l'autre est normal.");

    public static string BenchmarkOpen => T("Ouvrir le benchmark");

    public static string BenchmarkImageTitle => T("Image du résultat à partager");

    public static string BenchmarkImageDetail => T("Scores, vignettes des scènes et matériel, sans aucune donnée personnelle : à coller sur Discord, un forum ou un réseau social.");

    public static string HistoryReportName => T("Résultat de la dernière annulation");

    public static string BenchmarkImageZoom => T("Voir la carte de score en grand");

    public static string BenchmarkTileHint => T("Voir le détail de ce score");

    public static string BenchmarkImageCopy => T("Copier l'image");

    public static string BenchmarkImageOpen => T("Ouvrir l'image");

    public static string BenchmarkImageFolder => T("Afficher dans le dossier");

    public static string BenchmarkCompareHeading => T("Comparer avec un autre résultat");

    public static string BenchmarkCompareDetail => T("Collez le texte copié par un ami avec le bouton « Copier le résultat » de son MAUS (il contient une ligne MAUS-BENCH-1). Rien n'est envoyé : tout se fait sur votre PC.");

    public static string BenchmarkCompareButton => T("Comparer");

    public static string BenchmarkCompareTest => T("Test");

    public static string BenchmarkCompareMine => T("Vous");

    public static string BenchmarkCompareTheirs => T("L'autre");

    public static string BenchmarkCompareGap => T("Écart");

    public static string BenchmarkPointer => T("Le benchmark visuel a sa propre page dans le menu de gauche : lancement, dernier résultat et historique.");

    public static string GpuLabel => T("Carte graphique");

    public static string CpuLabel => T("Processeur");

    public static string LatencyTitle => T("Latence des pilotes (craquements audio, micro-saccades)");

    public static string LatencyIntro => T("MAUS écoute la trace du noyau de Windows quelques secondes, sans rien modifier, et nomme les pilotes qui ont gardé le processeur le plus longtemps d'affilée. Droits administrateur nécessaires.");

    public static string OverlayShow => T("Afficher le compteur au-dessus du jeu pendant la mesure");

    public static string OverlayTip => T("Une petite fenêtre transparente aux clics, au premier plan, sans rien injecter dans le jeu (c'est l'injection que surveillent les anti-triche). Visible en fenêtré et en plein écran fenêtré ; en plein écran exclusif, Windows l'affiche dans la plupart des jeux récents, pas dans tous.");

    public static string OverlaySettings => T("Taille, opacité et position du compteur");

    public static string OverlaySize => T("Taille");

    public static string OverlayOpacity => T("Opacité");

    public static string OverlayPlace => T("Position");

    public static string FpsAverage => T("EN CE MOMENT");

    public static string FpsLow1 => T("1 % LE PLUS LENT");

    public static string FpsLow01 => T("0,1 % LE PLUS LENT");

    public static string FpsGpu => T("CARTE GRAPHIQUE");

    public static string FpsAverageTip => T("Images par seconde de la dernière seconde. La moyenne des dix dernières secondes est écrite au-dessus, à côté du nom du jeu.");

    public static string FpsLowTip => T("Images par seconde des images les plus lentes : 1 % (ou 0,1 %) des images ont duré au moins aussi longtemps. Un chiffre bas par rapport à la moyenne se ressent comme des saccades.");

    public static string FpsGpuTip => T("Part de la durée de chaque image pendant laquelle la carte graphique a travaillé (méthode « GPU Busy » d'Intel PresentMon). Proche de 100 % : la carte graphique limite. Nettement moins : le processeur, la mémoire ou une limite d'images freinent.");

    public static string FixThisTip => T("Coche la correction de ce constat et ouvre l'onglet Corrections : rien n'est modifié avant que vous cliquiez sur « Appliquer ».");

    public static string TryMaxRefresh => T("Essayer la fréquence maximale");

    public static string HomeLayoutTitle => T("Accueil");

    public static string HomeLayoutNote => T("L'accueil est simple : le score, la prochaine étape et trois outils. Les autres outils restent dans l'Atelier, dans les Corrections et dans la barre de gauche. Le mode technicien (onglet Comportement) montre l'accueil complet.");

    public static string SymptomsTitle => T("Que se passe-t-il ?");

    public static string LastAuditNotCurrent => T("Ce n'est pas l'état actuel de votre PC : relancez l'audit pour le voir, et pour le détail de chaque constat.");

    public static string GlanceTitle => T("Votre PC en un coup d'œil");

    public static string WhatsNewClose => T("Compris");

    public static string WhatsNewAll => T("Toutes les nouveautés");

    public static string FamiliesLine => T("Points à voir par famille (cliquez pour ouvrir les Constats)");

    public static string NoAuditIllustration => T("Aucun audit pour l'instant");

    public static string AllGoodIllustration => T("Tout va bien");

    public static string ShortcutsHint => T("Raccourcis : Ctrl+1 à Ctrl+7 pour changer de partie.");

    public static string TryMaxRefreshTip => T("Essai sans enregistrement : vous gardez la nouvelle fréquence d'un clic, sinon l'ancienne revient seule après 15 secondes.");

    public static string ByImportance => T("Par importance");

    public static string ByModule => T("Par module");

    public static string FindingsBeforeAudit => T("Lancez l'audit : les constats s'afficheront ici, en commençant par ce qui demande votre attention.");

    public static string NothingToHandle => T("Rien à traiter : aucun problème relevé, rien à surveiller et aucune optimisation proposée.");

    public static string OnlyOptionalLeft => T("Aucun problème relevé et rien à surveiller. Les optimisations ci-dessous sont facultatives.");

    public static string NothingToHandleInRead => T("Dans ce qui a pu être lu : rien à traiter, aucun problème relevé, rien à surveiller et aucune optimisation proposée.");

    public static string OnlyOptionalLeftInRead => T("Dans ce qui a pu être lu : aucun problème relevé et rien à surveiller. Les optimisations ci-dessous sont facultatives.");

    public static string ShowConforming => T("Afficher aussi ce qui est conforme");

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

    public static string SaveReportTip => T("Une page à garder (ce qui a été vu, ce qui a été changé, et comment l'annuler), ou les constats en JSON ou CSV : à choisir en bas de la fenêtre d'enregistrement");

    public static string RestartExplorer => T("Redémarrer l'Explorateur");

    public static string RestartExplorerTip => T("Applique les corrections de la barre des tâches sans fermer la session");

    public static string TechnicalDetails => T("Détails techniques");

    public static string TabHistory => T("Historique");

    public static string WhatChangedTitle => T("Qu'est-ce qui a changé ?");

    public static string WhatChangedIntro => T("Quand « ça marchait hier » : ce qui a bougé entre deux audits gardés sur ce PC. MAUS montre ce qui a changé et quand, pas ce qui cause votre souci : une chose apparue au même moment n'en est pas forcément l'origine.");

    public static string WhatChangedCompareLabel => T("Comparer le dernier audit avec :");

    public static string WhatChangedNotesTitle => T("Non comparable : MAUS préfère ne pas conclure");

    public static string WhatChangedCopyLabel => T("Copier cette liste");

    public static string TabFollow => T("Suivi");

    public static string FollowIntro => T("Ce qui a bougé sur ce PC au fil du temps, d'après ce que MAUS a déjà relevé ici. Rien ne part de ce PC.");

    public static string Refresh => T("Actualiser");

    public static string RevertSession => T("Annuler cette séance");

    public static string SessionChanges => T("Corrections de cette séance");

    public static string Revert => T("Annuler cette correction");

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

    public static string AboutVersion => T("Version {0}", Maus.Core.AppVersion.Full);

    public static string AboutMadeBy => T("Conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. MAUS audite d'abord sans rien modifier ; chaque correction est expliquée, réversible sauf ce qu'elle signale et ne s'applique qu'avec votre accord.");

    public static string AboutLicenseTitle => T("Licence");

    public static string AboutLicense => T("Logiciel libre et gratuit, sous licence GPL-3.0 : vous pouvez l'utiliser, l'étudier, le modifier et le partager, en gardant la même licence. Le nom « MAUS » et son logo ne sont pas cédés : une version modifiée doit porter un autre nom (voir TRADEMARKS dans le dossier des licences).");

    public static string AboutPrivacyTitle => T("Confidentialité");

    public static string WeeklyNotificationLabel => T("Prévenir après l'audit");

    public static string AuditWhenElevatedOption => T("Lancer l'audit dès que les droits administrateur sont accordés");

    public static string AboutSecurityTitle => T("Sécurité");

    public static string AboutSecurity => T("La fenêtre de MAUS tourne sans droits administrateur. Un moteur séparé les reçoit seulement après votre clic et l'autorisation de Windows ; il n'accepte qu'une liste fermée de 31 opérations, s'arrête après 15 minutes sans demande, et confirme lui-même les actions les plus sensibles. "
        + "MAUS ne touche jamais au BIOS, aux comptes, aux mots de passe ni aux règles du pare-feu. Le code a été relu et des protections ont été renforcées en 0.8.6 ; une relecture ne prouve pas qu'il ne reste aucun défaut.");

    public static string CheckInstallation => T("Vérifier mon installation");

    public static string AboutSecurityDocument => T("Lire le document de sécurité complet (en anglais)");

    public static string AboutPrivacy => T("Aucune télémétrie : MAUS n'envoie rien à ses auteurs, et n'utilise le réseau que pour une action que vous lancez. Les rapports, les résumés et les relevés restent sur votre PC ; ils ne quittent votre machine que si vous les partagez vous-même, après les avoir relus.");

    public static string AboutComponentsTitle => T("Composants et sources");

    /// <summary>Composants tiers et sources des données techniques (détail dans THIRD-PARTY-NOTICES, dossier des licences).</summary>
    public static IReadOnlyList<string> AboutComponents =>
    [
        T("PresentMon 2.6.0 (Intel, MIT) : images par seconde des jeux."),
        T("LibreHardwareMonitorLib 0.9.6 (MPL-2.0) et ses dépendances DiskInfoToolkit, RAMSPDToolkit (MPL-2.0), HidSharp (Apache-2.0) : capteurs avancés, avec le pilote PawnIO que vous installez vous-même."),
        T("Modules PawnIO.Modules 0.2.11 (LGPL-2.1) : lecture seulement (avec les commandes que cette lecture exige : voir le document de sécurité) du contrôleur mémoire et des puces SPD des barrettes."),
        T("ZenStates-Core (GPL-3.0), memtest86+, coreboot, CoreFreq et le noyau Linux (GPL-2.0) : emplacements des registres de la mémoire, des faits matériels relus par le code de MAUS."),
        T("Fiches techniques Intel et AMD, normes JEDEC, documentation Microsoft et NVIDIA : chaque limite de sécurité du matériel renvoie à sa source, et un seuil sans source publiée est annoncé comme choisi par MAUS."),
        T(".NET et WPF (MIT) : la base de l'application."),
    ];

    public static string ReportProblem => T("Signaler un problème");

    public static string ProjectPage => T("Page du projet");

    public static string CheckNewVersion => T("Y a-t-il une nouvelle version ?");

    public static string CheckNewVersionTip => T("Ouvre la page de la dernière version dans votre navigateur. MAUS ne vérifie rien tout seul et n'envoie rien. Sur le Microsoft Store, les mises à jour arrivent toutes seules.");

    public static string RevertEverythingTip => T("Annule toutes les corrections encore en place, de la plus récente à la plus ancienne : à faire avant de désinstaller MAUS pour retrouver vos réglages d'origine. Une valeur changée entre-temps n'est pas écrasée.");

    public static string FindingSearchLabel => T("Chercher un constat");

    public static string FindingSearchEmpty => T("Aucun constat ne contient ce texte.");

    public static string FindingSearchTip => T("Cherche dans le titre, l'explication et l'identifiant de tous les constats, y compris ceux qui sont conformes. Exemple : antivirus, TPM, M01.");

    public static string LicensesFolder => T("Licences et sources");

    public static string ReportOnGitHub => T("Signaler sur GitHub");

    public static string ReportOnGitHubTip => T("Copie ce résumé, puis ouvre la page de signalement du projet sur GitHub, où vous le collez (compte GitHub gratuit nécessaire). MAUS n'envoie rien lui-même.");

    public static string GitHubPaste => T("Résumé copié : collez-le (Ctrl+V) dans la page GitHub qui vient de s'ouvrir, sous « Résumé copié par MAUS ».");

    /// <summary>Ce que veut dire le nom, sous le logo (en anglais et en espagnol aussi, les initiales restent M, A, U, S).</summary>
    public static string NameMeaning => T("Maintenance · Audit · Updates · Sécurité");

    public static string Continue => T("Continuer");

    public static string DisclaimerTitle => Maus.Core.Legal.Disclaimer.Title;

    public static IReadOnlyList<string> DisclaimerParagraphs => Maus.Core.Legal.Disclaimer.Paragraphs;

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
        T("Un module qui n'a pas pu être vérifié (erreur, délai dépassé) ne retire ni n'ajoute de points : le score est alors dit partiel et n'est pas gardé dans l'historique."),
        T("C'est un repère de MAUS pour suivre votre PC dans le temps, pas une mesure officielle. Ce barème date de la 0.8.6 : les scores précédents ne se comparent pas, la courbe repart de là."),
    ];

    public static string SlowPcTitle => T("Pourquoi mon PC est lent ?");

    public static string SlowPcDetail => T("Une minute de mesures, puis les causes principales et la façon de les corriger.");

    public static string TestPcTitle => T("Tester mon PC");

    public static string TestPcDetail => T("Stabilité du processeur, de la mémoire vive et de la mémoire vidéo, avec arrêt automatique en cas de surchauffe quand la température et sa limite sont lisibles.");

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

    public static string HelpSite => T("Site d'aide de MAUS");

    public static string RestorePointsTitle => T("Points de restauration de Windows");

    public static string ShowRestorePoints => T("Afficher les points");

    public static string CreateRestorePointNow => T("Créer un point maintenant");

    public static string OpenSystemRestore => T("Revenir à un point (outil de Windows)");

    public static string EnableAdvancedSensors => T("Activer les capteurs avancés");

    public static string UninstallPawnIo => T("Retirer PawnIO");

    public static string AllSensors => T("Tous les capteurs");

    public static string ComponentColumn => T("Composant");

    public static string SensorColumn => T("Capteur");

    public static string ValueColumn => T("Valeur");

    public static string SectionMemory => T("Mémoire");

    public static string SectionDrivers => T("Pilotes");

    public static string RefreshList => T("Actualiser la liste");

    public static string CopyList => T("Copier la liste");

    public static string DriverBackupsFolder => T("Voir les sauvegardes");

    public static string RestoreDriverBackup => T("Réinstaller une sauvegarde…");

    public static string DriversGpuTitle => T("Carte graphique : son pilote");

    public static string DriversIntro => T("Redémarrer le pilote répare souvent une image figée, un écran noir ou qui clignote. Chaque action demande votre accord et s'ouvre dans une fenêtre visible.");

    public static string DriversIntroTechnician => T("Retirer et redétecter la carte va plus loin. Sauvegarder puis supprimer le pilote sert à repartir sur une installation propre.");

    public static string RestartDriver => T("Redémarrer le pilote");

    public static string RestartDriverTip => T("pnputil /restart-device : l'écran devient noir quelques secondes. Fermez d'abord les jeux.");

    public static string ReinstallDevice => T("Retirer et redétecter");

    public static string ReinstallDeviceTip => T("pnputil /remove-device puis /scan-devices : Windows réinstalle la carte et son pilote. Seulement pour une vraie carte (PCI) : un écran virtuel ne serait pas recréé.");

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

    public static string OpenInWindows => T("Ouvrir dans Windows");

    public static string DriverUpdates => T("Mises à jour de pilotes (Windows Update)");

    public static string DriverUpdatesTip => T("Ouvre la page des mises à jour facultatives de Windows, où Windows propose des pilotes : cochez seulement ceux dont vous avez besoin.");

    public static string ShowUnsignedDrivers => T("Voir les pilotes non signés");

    public static string DriversDateNote => T("Trois dates ne disent rien de l'âge réel : le 21/06/2006, que Windows donne volontairement à ses pilotes pour que ceux des fabricants gardent la priorité (Raymond Chen, Microsoft, « The Old New Thing », 2017), le 18/07/1968, date de fondation d'Intel donnée à son « Chipset Device Software » pour qu'il passe après tout autre pilote (Intel, article 000095169), et le 01/01/1970, début du compteur de temps des programmes, quand un paquet ne donne pas de vraie date. L'âge seul ne dit pas qu'un pilote pose problème : MAUS ne fixe aucun seuil.");

    public static string ReadMemory => T("Lire la mémoire");

    public static string CopyMemory => T("Copier la fiche");

    public static string ReportMemory => T("Copier et signaler");

    public static string ReportMemoryTip => T("Copie la fiche, sans nom d'utilisateur ni nom du PC, puis ouvre la page de signalement du projet dans votre navigateur : vous y collez la fiche et dites ce que donne CPU-Z ou ZenTimings. MAUS n'envoie rien lui-même.");

    public static string MemoryControllerTitle => T("Contrôleur mémoire (réglages appliqués par le BIOS)");

    public static string MemoryAppliedTitle => T("Vitesse appliquée et tension de la mémoire");

    public static string ReadMemoryNeedsRights => T("« Lire la mémoire » demande les droits administrateur : cliquez sur « Toutes les fonctionnalités », en haut de la fenêtre.");

    public static string MemoryHonest => T("Les timings affichés ici sont ceux réellement appliqués et ceux annoncés par la barrette. Resserrer les timings ou monter la fréquence se fait dans le BIOS : le gain est souvent de quelques pour cent, surtout dans les jeux limités par le processeur, et une instabilité peut corrompre des fichiers. Testez toujours après un changement (Atelier, Tests : mémoire vive).");

    public static string SessionTitle => T("Relevé pendant une partie");

    public static string Record => T("Enregistrer");

    public static string ExportCsv => T("Enregistrer en CSV");

    public static string ScoreHistoryTitle => T("Historique des scores");

    public static string ScoreHistoryIntro => T("Chaque test réussi est gardé sur ce PC (rien n'est envoyé) : la courbe montre l'effet d'un réglage, d'un nettoyage ou d'une mise à jour. Un écart de quelques pour cent d'une mesure à l'autre est normal.");

    public static string WeeklyAudit => T("Audit automatique :");

    public static string WeeklyAuditTip => T("Un audit en lecture seule chaque semaine, sans fenêtre ; MAUS se signale selon votre choix (par défaut : seulement un problème rouge). Rien n'est envoyé.");

    public static string NotificationTitle => T("MAUS a trouvé un problème");

    public static string GaugeOrangeZone => T("zone orange à 10 °C de la limite (choix de MAUS)");

    /// <summary>Sépare un libellé de sa valeur, selon la typographie de la langue (espace avant les deux-points en français).</summary>
    public static string LabelSeparator => T("{0} : {1}", string.Empty, string.Empty);

    public static string WeeklyAuditDoneTitle => T("Audit de la semaine terminé");

    public static string ScheduleOutdated => T("L'audit de la semaine ne se lance plus depuis la mise à jour de MAUS : sa tâche pointe encore vers l'ancien dossier.");

    public static string ScheduleMove => T("Remettre en place");

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

    public static string SectionSheet => T("Fiche du PC");

    public static string SectionSensors => T("Capteurs");

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

    public static string ColumnIo => T("Lectures et écritures");

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

    public static string CpuTestIntro => T("Tous les cœurs calculent des résultats connus d'avance : la moindre erreur trahit un processeur instable (surcadençage, tension trop basse, surchauffe). Le score se compare à vos mesures précédentes.");

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

    public static string TestSafety => T("Pendant un test, MAUS surveille les températures lisibles : il s'arrête de lui-même au seuil de danger, et par prudence si ses mesures cessent de répondre ou si une température suivie n'est plus lue. Fermez les jeux et programmes lourds pour un résultat fiable.");

    public static string CoreTestTitle => T("Processeur : test cœur par cœur (Curve Optimizer, undervolt)");

    public static string CurveProgramTitle => T("Programme complet Curve Optimizer (plusieurs heures)");

    public static string CurveProgramIntro => T("Phase 1 : chaque cœur à son tour, pic de charge d'une seconde, chute au repos, réveil vérifié. Phase 2 : tous les cœurs chargés 5 à 10 secondes puis arrêtés au même instant, en boucle (la brusque chute de charge fait remonter la tension). Le PC est inutilisable pendant le programme ; la charge choisie ci-dessus sert aux deux phases.");

    public static string StartProgram => T("Lancer le programme");

    public static string CoreTestIntro => T("Utile si vous avez baissé le Curve Optimizer (AMD) ou fait un undervolt (Intel) : ces réglages lâchent quand un seul cœur monte à sa fréquence maximale, au réveil et aux changements de charge, rarement sous une charge continue. MAUS teste chaque cœur à tour de rôle avec des à-coups et des pauses. Un PC réglé d'origine n'a pas besoin de ce test.");

    public static string CrashTitle => T("Le PC a gelé pendant le dernier test");

    public static string Understood => T("J'ai compris");

    public static string HonestTitle => T("Soyons honnêtes : ce que ces corrections apportent vraiment");

    public static string HonestWindows => T("• Les réglages de Windows (effets visuels, confidentialité, Game Bar, démarrage) rendent le PC plus réactif, plus discret et plus régulier, mais leur gain en images par seconde dans les jeux est faible, souvent impossible à mesurer.");

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

    public static string ThemeLabel => T("Thème");

    public static string AccentLabel => T("Couleurs");

    public static string AccentNote => T("Seules les couleurs des boutons et de la sélection changent. Les couleurs des constats gardent leur sens : vert conforme, bleu à optimiser, or à surveiller, rouge problème.");

    public static string AnimationsTitle => T("Animations");

    public static string HelpTipsOption => T("Afficher les petits messages d'aide au survol");

    public static string LevelQuick => T("Rapide");

    public static string LevelNormal => T("Normal");

    public static string LevelLong => T("Long");

    public static string MoreOptions => T("Plus d'options");

    public static string FullCheckStart => T("Tout tester");

    public static string FullCheckIntro => T("Processeur, mémoire vive, mémoire vidéo et disque, l'un après l'autre : une seule confirmation, et tous les résultats au même endroit.");

    public static string FineTuningShow => T("Afficher le test cœur par cœur et le programme Curve Optimizer");

    public static string TileCpu => T("Processeur");

    public static string TileRam => T("Mémoire");

    public static string TileGpu => T("Carte graphique");

    public static string TileDisk => T("Disque");

    public static string TileNet => T("Réseau");

    public static string TileDevices => T("Écran et périphériques");

    public static string TipTileCpu => T("Vérifie que le processeur calcule sans erreur, même à pleine charge.");

    public static string TipTileRam => T("Cherche les erreurs dans la mémoire vive.");

    public static string TipTileGpu => T("Vérifie la mémoire de la carte graphique et mesure ses performances.");

    public static string TipTileDisk => T("Mesure la vitesse de vos disques.");

    public static string TipTileNet => T("Mesure la latence et la stabilité de votre connexion.");

    public static string TipTileDevices => T("Vérifie l'écran, le clavier, la souris et les haut-parleurs.");

    public static string OpenTest => T("Ouvrir le test");

    public static string StartOrStop => T("Lancer ou arrêter");

    public static string ScreenTestTitle => T("Écran : pixels morts et dégradés");

    public static string ScreenTestIntro => T("Plein écran, une couleur à la fois : elle ne change que lorsque vous cliquez. Échap pour quitter.");

    public static string InputTestTitle => T("Clavier et souris");

    public static string InputTestIntro => T("Chaque touche qui répond, le rythme réel de la souris et les doubles clics involontaires. Seulement dans sa fenêtre, rien n'est enregistré.");

    public static string SpeakerTestTitle => T("Haut-parleurs");

    public static string SpeakerTestIntro => T("Un son à gauche, à droite, puis des deux côtés, pour vérifier le branchement.");

    public static string SoundLeft => T("Gauche");

    public static string SoundRight => T("Droite");

    public static string SoundBoth => T("Les deux");

    public static string BatteryTestTitle => T("Batterie : autonomie réelle");

    public static string BatteryTestIntro => T("10 minutes de mesure, chargeur débranché, pour estimer l'autonomie à votre usage. C'est une estimation : Windows arrondit le niveau au pour cent.");

    public static string WifiWalkTitle => T("Balade Wi-Fi");

    public static string WifiWalkIntro => T("Marchez dans la maison avec le portable : la courbe montre le signal en direct et où il faiblit.");

    public static string UsbTestTitle => T("Clé USB : vraie capacité");

    public static string UsbTestIntro => T("Détecte les clés contrefaites : écrit sur toute la place libre, relit, puis retire ses fichiers. Vos fichiers ne sont pas touchés.");

    public static string BeforeAfterTitle => T("Avant et après, en vrai");

    public static string BeforeAfterIntro => T("Pour chaque séance de corrections qui peut toucher au démarrage : les démarrages mesurés par Windows avant et après, avec le temps habituel et l'écart. Si les écarts se chevauchent, MAUS le dit.");

    public static string HealthLogTitle => T("Carnet de santé du PC");

    public static string HealthLogIntro => T("Une ligne datée à chaque audit, comparée uniquement à ce PC lui-même. Aucune prédiction de panne, aucun seuil : MAUS suit les nombres que ses constats vous disent de surveiller.");

    public static string CoolingTestTitle => T("Refroidissement");

    public static string CoolingTestIntro => T("Mesure la montée en température sous charge et le retour au calme, puis compare avec la dernière fois. Peut révéler un ventilateur encrassé ou une pâte thermique fatiguée.");

    public static string CombinedTestTitle => T("Charge combinée");

    public static string CombinedTestIntro => T("Le processeur et la mémoire vidéo travaillent en même temps : utile si le PC gèle en jeu. La puissance de calcul de la carte graphique se teste avec le benchmark.");

    public static string SaveCheck => T("Enregistrer le contrôle technique");

    public static string TipSaveCheck => T("Une page qui réunit le dernier audit et les résultats des tests, sans donnée personnelle : pratique pour acheter ou vendre un PC.");

    public static string TipNeedsAudit => T("Disponible après le premier audit. Lancez-le depuis l'accueil.");

    public static string MoreDetails => T("Plus de détails");

    public static string FixesMoreTools => T("Autres outils et réglages");

    public static string TipScore => T("Note de 0 à 100, calculée à partir des constats de l'audit. C'est un repère.");

    public static string TipLimitedMode => T("MAUS regarde sans rien modifier. Les options grisées ont besoin de votre accord.");

    public static string TipAllFeatures => T("Windows demandera votre autorisation. Chaque action reste confirmée avant de s'appliquer.");

    public static string TipSafetyGauge => T("Plus le repère va vers le rouge, plus la mesure approche de sa limite.");

    public static string AnimationsNote => T("Transitions entre les pages, apparition des cartes, jauge du score. « Comme Windows » suit le réglage Accessibilité > Effets visuels > Effets d'animation.");

    public static string RefreshTitle => T("Vitesse d'actualisation des mesures");

    public static string RefreshNote => T("Vaut pour l'atelier « En direct », les tests de stabilité et la fenêtre de surveillance. Plus c'est rapide, plus les mesures elles-mêmes sollicitent un peu le processeur.");

    public static string MonitorTitle => T("Fenêtre de surveillance");

    public static string MonitorIntro => T("Une fenêtre à part, à garder ouverte pendant un test de stabilité ou un jeu : température, consommation et fréquence de chaque composant (actuelle, minimale, maximale), erreurs matérielles et erreurs de Windows depuis son ouverture.");

    public static string MonitorOnTop => T("Toujours au premier plan");

    public static string OfferAllFeaturesTitle => T("Droits administrateur");

    public static string OfferAllFeaturesIntro => T("Sans droits administrateur, MAUS fonctionne en mode limité : les options qui en ont besoin sont grisées. Le bouton « Toutes les fonctionnalités », en haut de la fenêtre, demande l'autorisation de Windows ; après 15 minutes sans utilisation, les droits se referment seuls.");

    public static string OfferAllFeatures => T("À l'ouverture, me demander : mode limité ou toutes les fonctionnalités");

    public static string LimitedModeBanner => T("Mode limité : les options grisées ont besoin des droits administrateur.");

    public static string EnableAllFeatures => T("Toutes les fonctionnalités");

    public static string WindowsUndidTitle => T("Windows a-t-il défait mes réglages ?");

    public static string WindowsUndidIntro => T("Après une mise à jour de Windows : vos corrections tiennent-elles ? MAUS compare son journal au dernier audit, sans rien relancer tout seul.");

    public static string CloseRights => T("Refermer les droits");

    public static string CloseRightsTip => T("Arrête le moteur administrateur de MAUS tout de suite : MAUS repasse en mode limité. Vous pourrez rouvrir les droits à tout moment.");

    public static string EngineSecurityStop => T("Un programme a essayé de commander le moteur administrateur de MAUS. Par sécurité, le moteur s'est arrêté : ses droits administrateur sont refermés et rien n'a été exécuté pour ce programme.") + Environment.NewLine + Environment.NewLine
        + T("Faites une analyse avec Sécurité Windows (Protection contre les virus et menaces). MAUS reste utilisable en mode limité.");

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

    public static string CompareTitle => T("Comparer avec une lecture précédente");

    public static string StartRecording => T("Démarrer le relevé");

    public static string StopRecording => T("Arrêter le relevé");

    public static string KeptRefreshTitle => T("Fréquences d'écran gardées");

    public static string CompareWith => T("Comparer avec l'audit du");

    public static string ReadinessTitle => T("Avant une mise à jour");

    public static string ReadinessIntro => T("Aucune mise à jour n'est sans risque. MAUS ne les installe pas et ne les bloque pas ; il vous aide à les faire au bon moment.");

    public static string ModulesChoiceNote => T("Un module décoché n'est plus vérifié : le score devient partiel et il n'est pas gardé dans l'historique. L'audit de la semaine reste complet. Le moteur demande confirmation.");

    public static string ApplyStepsTitle => T("Déroulement des corrections");

    public static string ZoomLabel => T("Taille de l'affichage");

    public static string ZoomNote => T("Recommandé : Normal. Vaut pour la fenêtre principale ; Ctrl + molette change la taille, Ctrl + 0 revient à 100 %.");

    public static string WhyItMatters => T("Pourquoi ça compte");

    public static string JourneyTitle => T("Votre parcours");

    public static string AuditProgressList => T("Avancement de l'audit, module par module");

    public static string SettingsTabAppearance => T("Apparence");

    public static string SettingsTabBehavior => T("Comportement");

    public static string ThemeNote => T("Recommandé : « Comme Windows ». MAUS suit alors le thème clair ou sombre de Windows.");

    public static string TechnicianTitle => T("Mode technicien");

    public static string TechnicianOption => T("Afficher partout les détails techniques");

    public static string TechnicianNote => T("Déplie d'office les détails techniques : identifiant du constat, valeur lue et attendue, clé écrite, message d'origine de Windows. Donne aussi l'accueil complet et les actions avancées du pilote graphique. Recommandé : désactivé, sauf pour un technicien.");

    public static string OfferAllFeaturesNote => T("Recommandé : activé. MAUS s'ouvre toujours sans droits administrateur ; la demande d'autorisation de Windows ne vient qu'après votre clic.");

    public static string AuditWhenElevatedNote => T("Recommandé : désactivé. Activé, l'audit (lecture seule) démarre de lui-même dès que les droits sont accordés, sans nouvelle demande d'autorisation.");

    public static string MonitorOnTopNote => T("Recommandé : désactivé, sauf pendant un test ou un jeu en fenêtre.");

    public static string BestFixesTitle => T("Ce qui rapporte le plus de points");

    public static string BestFixesTip => T("Estimation selon le barème du score. « Corriger » coche la correction : rien n'est appliqué avant votre confirmation.");

    public static string AllGreen => T("Tout est au vert : aucun point à traiter, aucun contrôle indéterminé.");

    public static string FindingFilterLabel => T("Quels constats afficher");

    public static string FamilyFilterLabel => T("Quelle famille afficher");

    public static string FilterHidesAll => T("Aucun constat ne correspond à ce filtre, mais d'autres restent à traiter : changez le filtre pour les voir.");

    public static string RevertTodayTip => T("Annule toutes les corrections faites aujourd'hui, de la plus récente à la plus ancienne. Une valeur changée entre-temps n'est pas écrasée.");

    public static string CopyMarkdown => T("Copier pour un forum");

    public static string CopyMarkdownTip => T("Le même texte, mis en forme en Markdown (Reddit, GitHub, Discord…)");

    public static string BenchmarkRankingTitle => T("Mon classement");

    public static string BenchmarkRankingIntro => T("Vos mesures qui se comparent à la dernière (même édition, même résolution). Changez un réglage, relancez le benchmark, et comparez.");

    public static string BenchmarkRankingCompare => T("Comparer à la dernière");
}
