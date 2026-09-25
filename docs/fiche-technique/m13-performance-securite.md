## Module 13 — Performance contre sécurité

Option experte désactivée par défaut : couper l'intégrité de la mémoire (HVCI), et plus rarement toute la VBS. Gain en jeu publié : environ 2 à 6 % sur CPU récent. Peut empêcher Valorant (Riot Vanguard) ou FACEIT de fonctionner.

Qui y gagne : Microsoft indique que HVCI fonctionne mieux sur Intel Kaby Lake (7e gén.) et plus (MBEC) et AMD Zen 2 et plus (GMET). Les CPU plus anciens émulent (Restricted User Mode) → impact plus fort. Donc « surtout AMD » vrai pour Ryzen 1000/2000 (Zen, Zen+), pas pour les Ryzen récents. Source : https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity

Détection : Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard (administrateur).
- VirtualizationBasedSecurityStatus : 0 non activée, 1 activée mais pas en cours, 2 en cours.
- SecurityServicesRunning (tableau) : 1 Credential Guard, 2 intégrité mémoire (HVCI), 3 System Guard Secure Launch, 4 SMM Firmware Measurement, 5 Kernel-mode Hardware-enforced Stack Protection, 6 idem en audit, 7 Hypervisor-Enforced Paging Translation.
- AvailableSecurityProperties (tableau) : 1 hyperviseur, 2 Secure Boot, 3 protection DMA, 4 Secure Memory Overwrite, 5 NX, 6 SMM, 7 MBEC/GMET, 8 virtualisation APIC.
Registre : HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity : Enabled (DWORD), Locked (DWORD, verrou UEFI), WasEnabledBy. HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard : EnableVirtualizationBasedSecurity, RequirePlatformSecurityFeatures, Locked.

Dépendances vérifiées :
- Anti-triche : Riot Vanguard (service « vgc »/pilote « vgk ») et FACEIT (service « FACEIT ») : si détecté → avertissement renforcé, l'utilisateur décide (décision : ne pas bloquer).
- Credential Guard (SecurityServicesRunning contient 1), Recall, Windows Hello renforcé : VBS obligatoire.
- Hyper-V (Microsoft-Hyper-V-All), WSL2 / Plateforme de machine virtuelle (VirtualMachinePlatform), Windows Sandbox (Containers-DisposableClientVM) : l'hyperviseur reste chargé, gain réduit. (Win32_OptionalFeature, InstallState 1 = activé).

Constats attendus en V0.1 (lecture seule) :
- État HVCI (en cours ou non) — c'est une information, pas un problème : HVCI actif = Ok (sécurité), avec explication de l'option experte.
- MBEC/GMET absent + HVCI actif → Info/Improvable : impact plus fort sur ce CPU.
- Verrou UEFI présent → info : désactivation impossible par logiciel.
- Anti-triche détecté → info : garder HVCI.
- Atténuations CPU (HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management FeatureSettingsOverride / FeatureSettingsOverrideMask) présentes → Problem (voir Module 1 ; jamais proposé).
- Liste de blocage des pilotes vulnérables : HKLM\SYSTEM\CurrentControlSet\Control\CI\Config VulnerableDriverBlocklistEnable (1 = activée ; absente = défaut Windows 11 activé, à vérifier) → si 0 : Problem.

Ce que l'outil ne fait jamais : désactiver la liste de blocage, ni les atténuations CPU.
Message : « L'intégrité de la mémoire empêche un pilote malveillant de prendre le contrôle de Windows. La couper peut faire gagner quelques images par seconde, surtout sur un processeur ancien, mais rend le PC plus vulnérable. Valorant et FACEIT peuvent refuser de se lancer. »
