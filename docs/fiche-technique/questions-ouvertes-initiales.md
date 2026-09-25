## Questions ouvertes

Dix décisions restent à prendre par le porteur du projet avant le développement.

- [ ] **Portable, alimentation :** la demande dit « performance sur batterie et normal sur batterie » ; la spec retient Meilleures performances sur secteur et Équilibré sur batterie (Module 5). Autres options : performance partout, ou choix au premier lancement.
- [ ] **Ryzen X3D double CCD :** la spec garde Utilisation normale (Module 5) et impose la Game Bar (Module 7), contre « PC fixe en performance » et « laisser le choix ». Options : imposer, recommander avec choix, ou appliquer la demande à la lettre.
- [ ] **Pilotes via Windows Update :** ne jamais bloquer (défaut du Module 3), bloquer sur demande en Pro et plus (`ExcludeWUDriversInQualityUpdate`, Module 9), ou bloquer par défaut ; et inclure ou non les pilotes facultatifs dans la sous-catégorie « facultatives ».
- [ ] **Windows 10 :** garder le support (ESU grand public jusqu'au 12 octobre 2027, [source](https://www.microsoft.com/en-us/windows/extended-security-updates)), alors que .NET 10 ne liste pas Windows 10 22H2 grand public ([source](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)), ou viser Windows 11 seul.
- [ ] **Modèle économique :** gratuit, libre, payant ou freemium ; le choix pèse sur la directive 2024/2853 et sur la signature, un développeur individuel français ne pouvant pas utiliser Artifact Signing sans structure juridique.
- [ ] **Nom du produit :** sans « Windows », « Xbox » ni autre marque tierce ; disponibilité à vérifier auprès de l'INPI et de l'EUIPO.
- [ ] **Référence du benchmark :** mesures internes seules (Module 11), contributions anonymes volontaires avec consentement RGPD, ou base tierce sous licence.
- [ ] **Copilot, Recall, OneDrive :** Copilot retiré en mode Avancé et Recall configuré (Module 4) ; OneDrive non traité : ne rien faire, retirer du démarrage (Module 12), ou désinstaller.
- [ ] **Mode automatique ou expert :** un clic par profil (voir Suggestions), ou validation de chaque réglage ligne par ligne.
- [ ] **Périmètre du Module 13 :** intégrité de la mémoire et VBS seuls, ou aussi les atténuations CPU (`FeatureSettingsOverride`), aujourd'hui dans « Ce que le logiciel ne fera pas ».
