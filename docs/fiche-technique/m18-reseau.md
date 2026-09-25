# Module 18 — Réseau

**But** : repérer les causes simples et fréquentes d'une connexion lente, sans rien envoyer sur Internet.

## Détection (lecture seule)

- `MSFT_NetAdapter` (`root\StandardCimv2`) : cartes physiques (`HardwareInterface`) connectées (`MediaConnectState` = 1).
- Ethernet (`NdisPhysicalMedium` = 14) : vitesse négociée `ReceiveLinkSpeed`. Moins de 1 Gb/s = optimisation (bleu) ; 10 Mb/s ou moins = orange. Cause typique : câble abîmé ou à 4 fils, prise mal enfoncée, port limité. Honnête : cela ne gêne que si la connexion Internet dépasse 100 Mb/s, ou pour les copies entre PC.
- Wi-Fi (`NdisPhysicalMedium` = 9) : API Wlan (`WlanQueryInterface`, connexion en cours et canal) : signal (0 à 100), norme (Wi-Fi 4 à 7), bande (donnée seulement quand elle est certaine), débits négociés. Signal < 50 % ou norme antérieure au Wi-Fi 4 = optimisation.

## À vérifier sur Windows

- Décalages de `WLAN_CONNECTION_ATTRIBUTES` (SSID à 520, type de couche physique à 568, signal à 576, débits à 580 et 584).
- Idée pour plus tard : test de latence (passerelle, serveur DNS) lancé à la demande depuis l'Atelier.
