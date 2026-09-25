## Ce que le logiciel ne fera pas

Le logiciel refuse 20 « optimisations » répandues, parce qu'elles n'ont pas d'effet mesurable ou qu'elles fragilisent Windows. Le Module 1 signale celles qu'il détecte déjà sur le PC.

| Réglage ou « astuce » | Pourquoi on ne le fait pas |
|---|---|
| Nettoyeur de registre | Aucun gain mesurable. Microsoft ne prend pas en charge ces utilitaires et prévient qu'ils peuvent imposer une réinstallation (politique de support, à vérifier). |
| « Libérer la RAM » (nettoyeurs de mémoire) | Windows garde la RAM libre en cache ; la vider force des relectures disque. Gain nul, parfois négatif. |
| Supprimer ou réduire le fichier d'échange | Il porte la limite de mémoire engagée et les vidages sur incident ; à la limite, figeages et plantages ([source](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file)). Le Module 1 le signale. |
| Désactiver SysMain à l'aveugle | Service de préchargement des applications : aucun gain démontré sur SSD, lancements parfois plus lents (à vérifier). À mesurer seulement, avec le Module 11. |
| Réglages TCP « gaming » (Nagle, `TcpAckFrequency`, `TCPNoDelay`) | Ils ne visent que TCP, alors que la plupart des jeux en ligne utilisent UDP (à vérifier). Une application peut déjà couper Nagle elle-même avec `TCP_NODELAY`. |
| Valeurs « magiques » de `Win32PrioritySeparation` | Déjà piloté par « Ajuster pour obtenir les meilleures performances des : Programmes » (à vérifier). Aucune mesure publique fiable d'un gain. |
| Outils de résolution du minuteur (« Timer Resolution ») | Depuis Windows 10 2004, `timeBeginPeriod` n'agit plus globalement ; Windows 11 ne garantit rien aux fenêtres masquées. Une résolution fine empêche aussi les états d'économie d'énergie ([source](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)). |
| `bcdedit` : `useplatformclock`, `useplatformtick`, `disabledynamictick`, `tscsyncpolicy` | Réservés au débogage selon Microsoft, qui prévient qu'une option BCD peut rendre le PC inutilisable ([source](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set)). |
| Désactiver le HPET | Windows choisit lui-même sa source d'horloge ; aucune consigne Microsoft ne le recommande. Effet variable selon la plateforme (à vérifier). |
| Couper les atténuations Spectre et Meltdown (`FeatureSettingsOverride` = 3, `FeatureSettingsOverrideMask` = 3) | Actives par défaut sur les postes clients ([source](https://support.microsoft.com/en-us/topic/kb4073119-windows-client-guidance-for-it-pros-to-protect-against-silicon-based-microarchitectural-and-speculative-execution-side-channel-vulnerabilities-35820a8a-ae13-1299-88cc-357f104f5b11)). Exclu, sauf décision contraire pour le Module 13 (voir Questions ouvertes). |
| Couper Defender, SmartScreen, UAC ou Windows Update | Perte de protection sans gain mesurable sur un PC récent. Le Module 1 rétablit ces réglages. |
| Désinstaller Edge ou WebView2 | Widgets et nombreuses applications reposent sur le runtime WebView2 (voir Module 1). |
| Désinstaller le Microsoft Store ou App Installer | App Installer fournit winget et le Store le met à jour ([source](https://learn.microsoft.com/en-us/windows/package-manager/winget/)). Les mises à jour d'applications cessent. |
| Forcer le mode MSI sur tous les périphériques | C'est l'INF du pilote qui active `MSISupported` ([source](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/enabling-message-signaled-interrupts-in-the-registry)). Le forcer sur un pilote non prévu peut rendre le périphérique muet ou bloquer le démarrage (à vérifier). |
| Supprimer des fichiers de WinSxS | Microsoft prévient que le PC peut ne plus démarrer ni se mettre à jour ([source](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)). Seul `/StartComponentCleanup` est proposé (voir Suggestions). |
| Désactiver en masse des services « inutiles » | Les types de démarrage varient selon la build ; impression, Bluetooth ou Store cessent de fonctionner. Le Module 1 restaure les valeurs du catalogue. |
| Bloquer la télémétrie par `hosts` ou pare-feu | Casse Windows Update, le Store ou l'activation (voir Module 4). |
| Images Windows « allégées » (Tiny11, AtlasOS, ReviOS) | Composants de sécurité et de mise à jour retirés (voir Module 1). |
| « Driver updaters » tiers | Pilotes de sources non officielles. Le Module 9 renvoie aux seuls sites des fabricants. |
| Overclocking automatique par l'outil | Risque matériel et perte de garantie. Le Module 15 renvoie seulement vers les outils officiels. |

## Mentions légales et décharge de responsabilité

Ce texte s'affiche au premier lancement, puis avant toute action BIOS (Module 8), overclocking (Module 15) ou baisse de sécurité (Module 13). Il informe des risques, mais ne peut pas exclure la responsabilité de l'éditeur envers un consommateur.

**Projet de texte :**

> {Produit} modifie des réglages de Windows. Chaque modification est enregistrée et peut être annulée depuis l'application. Un point de restauration est créé avant toute écriture, si la protection du système est active.
>
> Certaines opérations peuvent provoquer une panne ou une perte de données : mise à jour du BIOS, overclocking, désactivation de protections de sécurité. Elles exigent votre accord explicite, et les deux premières restent manuelles. Sauvegardez vos données avant de continuer.
>
> {Produit} est un logiciel indépendant, ni affilié à Microsoft, NVIDIA, AMD ou Intel, ni approuvé par eux. Ces avertissements ne limitent pas vos droits légaux de consommateur.

**Avant les actions sensibles :** rappel ciblé, case « J'ai compris les risques » non pré-cochée, accord horodaté dans le journal. La phrase « l'éditeur décline toute responsabilité » du Module 8 devient une simple information sur les risques.

**Limites juridiques :**
- Code de la consommation, article R212-1, 6° : toute clause qui supprime ou réduit le droit à réparation du consommateur est présumée abusive, sans preuve contraire possible ([source](https://www.legifrance.gouv.fr/codes/article_lc/LEGIARTI000032807196)).
- Directive (UE) 2024/2853 : le logiciel devient un produit, et la destruction de données non professionnelles devient un dommage indemnisable ([source](https://www.ibanet.org/European-Product-Liability-Directive-liability-for-software)). Transposition au 9 décembre 2026, pour les produits mis sur le marché après cette date ([source](https://www.gibsondunn.com/eu-product-liability-directive-responding-to-software-ai-and-complex-supply-chains/)).
- Le logiciel libre fourni hors activité commerciale en est exclu ([source](https://www.ibanet.org/European-Product-Liability-Directive-liability-for-software)). L'interdiction d'exclure cette responsabilité par contrat figure à l'article 15 (à vérifier).
- Faire relire la licence, les conditions d'utilisation et ces textes par un avocat avant toute diffusion.

**Marques :** le règlement (UE) 2017/1001 autorise l'usage d'une marque pour désigner le produit d'autrui, selon les usages honnêtes (article 14, §1 c et §2, [source](https://ipright.eu/trademark-regulation/en/Article-14)). Microsoft interdit ses logos sans licence et toute suggestion d'affiliation ; votre propre marque doit rester plus visible ([source](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks)). Windows, Xbox, Game Pass, NVIDIA, GeForce, AMD, Ryzen, Radeon et Intel apparaissent en texte seul, sans logo (règles de chaque marque à vérifier).

**Mention type :** « Windows et Xbox sont des marques du groupe Microsoft. Les autres marques citées appartiennent à leurs propriétaires respectifs. »

**RGPD :** par défaut, aucune donnée ne quitte le PC. Le téléchargement du catalogue expose l'adresse IP au serveur : journaux minimaux, conservation courte, hébergement dans l'UE (choix de conception). Tout envoi futur (rapport d'erreur, contribution au benchmark) exige consentement préalable, information claire et inscription au registre des traitements.
