; Installateur de MAUS (Inno Setup 7, https://jrsoftware.org/isinfo.php).
; Ne pas compiler à la main : installer\build-installer.ps1 publie l'application puis appelle ISCC avec
;   /DAppVersion=<version de Directory.Build.props>  /DSourceDir=<dossier publié>  /DOutputDir=<dossier de sortie>
;
; Choix :
; - Program Files (protégé) : l'audit automatique de la semaine n'est proposé que depuis un dossier que seul un
;   administrateur peut modifier (ScheduledAudit.IsProtectedLocation).
; - Application autonome (.NET inclus) : rien d'autre à installer.
; - Désinstallation : retire les fichiers, les raccourcis et la tâche planifiée « MAUS\WeeklyAudit » de MAUS.
;   Le journal des corrections (%ProgramData%\MAUS) est gardé : il contient les valeurs d'origine des réglages
;   modifiés, utiles si MAUS est réinstallé pour annuler une correction.

#ifndef AppVersion
  #error "AppVersion manquant : lancer installer\build-installer.ps1"
#endif
#ifndef SourceDir
  #error "SourceDir manquant : lancer installer\build-installer.ps1"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
; Identifiant fixe : ne jamais le changer, il relie les mises à jour et la désinstallation.
AppId={{6F3D5F82-4668-473E-9693-0114543FC242}
AppName=MAUS
AppVersion={#AppVersion}
AppVerName=MAUS {#AppVersion}
AppPublisher=Projet MAUS (open source)
AppPublisherURL=https://github.com/TERMINATORmdel101/MAUS
AppSupportURL=https://github.com/TERMINATORmdel101/MAUS/issues
AppUpdatesURL=https://github.com/TERMINATORmdel101/MAUS/releases
AppCopyright=Contributeurs de MAUS. Licence GPL-3.0-only.
AppComments=Audit, réparation et optimisation transparente de Windows 11. Conçu et codé avec Claude, une IA d'Anthropic.
VersionInfoVersion={#AppVersion}
VersionInfoProductName=MAUS
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Installation de MAUS
VersionInfoCompany=Projet MAUS (open source)
VersionInfoCopyright=Contributeurs de MAUS. Licence GPL-3.0-only.
DefaultDirName={autopf}\MAUS
DefaultGroupName=MAUS
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 23H2 (build 22631) ou plus récent, comme l'application.
MinVersion=10.0.22631
SetupIconFile=..\src\Maus.App\Assets\maus.ico
UninstallDisplayIcon={app}\MAUS.exe
UninstallDisplayName=MAUS
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=MAUS-{#AppVersion}-installation
; MAUS ouvert pendant l'installation ou la désinstallation : l'installateur demande de le fermer.
AppMutex=MAUS.FenetrePrincipale
CloseApplications=yes
ShowLanguageDialog=auto

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MAUS"; Filename: "{app}\MAUS.exe"; Comment: "Audit, réparation et optimisation de Windows 11"
Name: "{autodesktop}\MAUS"; Filename: "{app}\MAUS.exe"; Tasks: desktopicon

[Run]
; MAUS demande les droits administrateur : l'installateur, déjà administrateur, peut le lancer à la fin.
Filename: "{app}\MAUS.exe"; Description: "{cm:LaunchProgram,MAUS}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Seulement la tâche de MAUS lui-même (créée par « Audit automatique chaque semaine ») ; sans elle, rien à faire.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MAUS\WeeklyAudit"" /F"; Flags: runhidden; RunOnceId: "RetirerAuditHebdomadaire"
