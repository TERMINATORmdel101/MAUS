# Sécurité / Security

## Signaler une faille (français)

Le moteur de MAUS fonctionne avec les droits administrateur : une faille peut avoir des conséquences sérieuses. **Ne la publiez pas dans un ticket public.**

Signalez-la en privé par GitHub : onglet **Security** du dépôt, puis **Report a vulnerability** (https://github.com/TERMINATORmdel101/MAUS/security/advisories/new). Seul le responsable du projet voit le signalement.

Merci d'indiquer : la version de MAUS (« À propos » ou `maus --version`), la version de Windows, ce qu'il faut faire pour reproduire le problème et ce qu'un attaquant pourrait obtenir.

MAUS est un projet bénévole en version alpha : nous répondons dès que possible, sans délai garanti. Une correction est publiée dans une nouvelle version, et le signalement est rendu public une fois la correction disponible, avec vos remerciements si vous le souhaitez.

Seule la **dernière version** publiée reçoit des corrections de sécurité.

## Reporting a vulnerability (English)

The MAUS engine runs with administrator rights, so a vulnerability can have serious consequences. **Please do not report it in a public issue.**

Report it privately through GitHub: **Security** tab of the repository, then **Report a vulnerability** (https://github.com/TERMINATORmdel101/MAUS/security/advisories/new). Only the project maintainer can see the report.

Please include: the MAUS version ("About" or `maus --version`), the Windows version, the steps to reproduce the problem and what an attacker could achieve.

MAUS is a volunteer alpha project: we answer as soon as we can, without a guaranteed delay. A fix is released in a new version, and the report is made public once the fix is available, with credit to you if you wish.

Only the **latest released version** receives security fixes.

## Ce que MAUS fait déjà pour se protéger / What MAUS already does

- La fenêtre de MAUS tourne sans droits administrateur (depuis la 0.8.0). Seul un moteur séparé les reçoit, avec l'autorisation de Windows et seulement quand une action en a besoin (0.8.1) : il n'accepte qu'une liste fermée d'opérations, par un canal privé réservé au compte de l'utilisateur et à la fenêtre qui l'a lancé (fermé au réseau, processus vérifié des deux côtés, chaque demande signée et utilisable une seule fois depuis la 0.8.3 ; coupure après 3 tentatives refusées ; protections de Windows contre l'injection de DLL et les jonctions de dossier), et s'arrête après 15 minutes sans demande. / The MAUS window runs without administrator rights; only a separate engine gets them, with the Windows permission prompt and only when an action needs it. It accepts a closed list of operations over a private channel restricted to the user and to the window that started it (since 0.8.3, every request is signed and can be used only once, the engine stops after 3 refused attempts, and Windows process mitigations block DLL injection paths and junction redirection), and stops after 15 minutes without a request.
- L'audit est en lecture seule ; les corrections ne sont écrites qu'après un choix de l'utilisateur, avec un point de restauration et un journal qui permet de tout annuler.
- Les outils externes (PresentMon, winget) ne sont lancés que depuis des dossiers protégés, et PresentMon seulement si son empreinte SHA-256 est exactement celle de la copie d'Intel.
- Le journal des corrections et les choix de l'utilisateur sont rangés dans un dossier réservé aux administrateurs (`%ProgramData%\MAUS`).
- Aucune donnée n'est envoyée (voir [PRIVACY](PRIVACY)).
- L'installateur est fabriqué par GitHub Actions à partir du code public, après le passage de tous les tests ; son empreinte SHA-256 est publiée avec chaque version, avec une attestation de provenance vérifiable (`gh attestation verify`).
