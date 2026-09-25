# Décisions prises par le porteur du projet (24/09/2026) — priment sur les sections de la fiche

- Principe général : partout où c'est possible, le choix revient à l'utilisateur, avec une valeur recommandée pré-sélectionnée.
- Portable, alimentation : choix au premier lancement ; proposé : Meilleures performances sur secteur, Équilibré sur batterie ; option « performance partout ».
- Ryzen X3D double CCD asymétrique : Utilisation normale + Game Bar recommandées (pré-sélectionnées), l'utilisateur peut choisir Haute performance / couper la Game Bar après avertissement.
- Pilotes via Windows Update : jamais bloqués par défaut ; blocage proposé au choix (Module 9 porte ce réglage).
- Anti-triche et Module 13 : avertissement renforcé si Vanguard ou FACEIT est détecté ; l'utilisateur décide (pas de blocage).
- Benchmark : mesures internes par défaut ; partage anonyme au choix, désactivé par défaut.
- Copilot, Recall, OneDrive : au choix de l'utilisateur ; OneDrive : ne rien faire par défaut.
- Mode : profils en un clic + chaque réglage modifiable ligne par ligne.
- Système cible : Windows 11 uniquement, à partir de 23H2 (build 22631). Windows 10 hors périmètre.
- Gratuit, open source GPL-3.0, publié sur le Microsoft Store. Aucune télémétrie de l'outil.
- Fabriqué par une IA, assumé.
- Nom : MAUS.

# Règles de la V0.1 (mode audit seul)

- Seule l'étape Detect est implémentée : AUCUNE écriture sur le système (registre, services, fichiers système, paramètres). Les corrections arrivent en V0.2.
- Les verdicts : Ok (vert), Info, Improvable (bleu : optimisation sans risque), Warning (orange), Problem (rouge), Unknown (gris).
- Mapping gravité → verdict pour un écart : Critical/High → Problem ; Medium → Warning ; Low → Improvable ; Info → Info.
- Tous les textes affichés sont en français, clairs pour un non-expert.
