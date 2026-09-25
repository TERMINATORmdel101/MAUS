## Module 12 — Applications au démarrage

Ce module liste tout ce qui se lance à l'ouverture de session, classe chaque entrée en quatre familles et la désactive comme le Gestionnaire des tâches, sans rien supprimer. C'est souvent le premier levier de performance réel, surtout avec 8 Go de RAM ou un disque dur.

Texte pédagogique affiché : « Chaque application lancée au démarrage retarde l'ouverture de session, occupe de la mémoire et parfois le processeur en arrière-plan. Au-delà des réglages Windows, c'est ici que se gagne la performance au quotidien. Désactiver une entrée ne désinstalle rien : l'application se lance toujours quand vous l'ouvrez. »

Détection : l'outil lit les sources du tableau, puis relie chaque entrée à son exécutable : éditeur, signature (WinVerifyTrust), chemin, mémoire utilisée maintenant. L'« Impact au démarrage » du Gestionnaire des tâches n'a pas d'API documentée : l'outil mesure plutôt la durée de démarrage avant et après.

| Source | Emplacement | Désactivation |
|---|---|---|
| Registre utilisateur | HKCU\Software\Microsoft\Windows\CurrentVersion\Run | HKCU\...\Explorer\StartupApproved\Run |
| Registre machine | HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run et HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run | HKLM\...\StartupApproved\Run et Run32 |
| Dossiers Démarrage | shell:startup (%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup) et shell:common startup (%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp) | ...\StartupApproved\StartupFolder |
| Exécution unique | RunOnce (HKCU, HKLM) | Affichage seul |
| Apps du Store (MSIX) | Tâches StartupTask déclarées par les paquets | Lien vers ms-settings:startupapps |
| Tâches planifiées à l'ouverture de session | déclencheur de type ouverture de session, hors \Microsoft\ | Disable-ScheduledTask, avec accord (V0.2) |
| Services tiers automatiques | Win32_Service StartMode = Auto, éditeur non Microsoft | Affichage seul |

Format StartupApproved : valeurs binaires ; premier octet 02 (ou 06) = activé, 03 (ou autre) = désactivé ; les octets 4 à 11 portent la date (FILETIME) de désactivation. Valeur absente dans StartupApproved = activé.

Classement (catalogue JSON) :
| Famille | Exemples | Ce que l'utilisateur perd | Recommandation |
|---|---|---|---|
| Lanceurs de jeux | Steam, Epic Games Launcher, EA app, Ubisoft Connect, Battle.net, GOG Galaxy | Mises à jour des jeux en tâche de fond, statut en ligne | Désactiver sans problème |
| Messagerie | Discord, Teams (personnel), WhatsApp, Telegram | Messages reçus seulement une fois l'app ouverte | Désactiver, selon usage |
| Médias | Spotify, iTunes Helper | Rien d'important | Désactiver sans problème |
| Navigateurs | Edge (Boost de démarrage), Chrome (apps en arrière-plan) | Ouverture du navigateur un peu plus lente | Désactiver dans les réglages du navigateur |
| Aides logicielles | Adobe Creative Cloud, Acrobat, Java Update Scheduler | Vérification des mises à jour au lancement de l'app | Désactiver si usage occasionnel |
| Cloud et sauvegarde | OneDrive, Google Drive, Dropbox, iCloud | Synchronisation et sauvegarde arrêtées : risque de perte de données | Garder si utilisé ; question posée (OneDrive : ne rien faire par défaut) |
| Périphériques | Logitech G HUB, Razer Synapse, Corsair iCUE, SteelSeries GG, Armoury Crate, MSI Center | Macros, profils, éclairage, parfois ventilation | Selon usage |
| Pilotes et système | Console audio Realtek, pavé tactile Synaptics ou ELAN, utilitaires Bluetooth, logiciel du GPU | Touches spéciales, gestes, audio | Garder |
| Sécurité | SecurityHealthSystray (Sécurité Windows), antivirus tiers, gestionnaire de mots de passe, VPN | Protection absente au démarrage | Toujours garder, non décochable |
| Inconnu | Entrée absente du catalogue | Indéterminé | Afficher éditeur, signature et chemin ; laisser le choix |
| Suspect | Non signé, dans %TEMP%, nom aléatoire sous AppData, script .vbs ou .ps1 caché | — | Voir Module 1 : analyse Defender |

Mesure du gain : journal Microsoft-Windows-Diagnostics-Performance/Operational, événement 100 (durée de démarrage) et 101 (application qui ralentit le démarrage). Avec le démarrage rapide actif, la mesure est faussée (voir Module 5).

Action (V0.2) : l'utilisateur coche ; familles « sans problème » pré-cochées. Les valeurs Run ne sont jamais supprimées. Services tiers en lecture seule.
