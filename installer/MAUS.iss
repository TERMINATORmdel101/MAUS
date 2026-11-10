; Installateur de MAUS (Inno Setup 7, https://jrsoftware.org/isinfo.php).
; Ne pas compiler à la main : installer\build-installer.ps1 publie l'application puis appelle ISCC avec
;   --define=AppVersion=<version de Directory.Build.props>  --define=SourceDir=<dossier publié>  --define=OutputDir=<sortie>
;
; Choix :
; - Program Files (protégé) : l'audit automatique de la semaine n'est proposé que depuis un dossier que seul un
;   administrateur peut modifier (ScheduledAudit.IsProtectedLocation).
; - Application autonome (.NET inclus) : rien d'autre à installer.
; - Désinstallation : retire les fichiers, les raccourcis et la tâche planifiée « MAUS\WeeklyAudit » de MAUS.
;   Le journal des corrections (%ProgramData%\MAUS\journal) est gardé : il contient les valeurs d'origine des réglages
;   modifiés, utiles si MAUS est réinstallé pour annuler une correction. Sur demande seulement (réponse par défaut : Non),
;   elle retire aussi les sauvegardes de pilotes, les journaux de diagnostic et les fichiers temporaires. Voir [Code].

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
AppComments={cm:MausAppComments}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=MAUS
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Installation de MAUS
VersionInfoCompany=Projet MAUS (open source)
VersionInfoCopyright=Contributeurs de MAUS. Licence GPL-3.0-only.
DefaultDirName={autopf}\MAUS
; Program Files est une protection : seul un administrateur peut y modifier MAUS.
DisableDirPage=yes
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

[CustomMessages]
fr.MausAppComments=Audit, réparation et optimisation transparente de Windows 11. Conçu et codé avec Claude, une IA d'Anthropic.
en.MausAppComments=Transparent audit, repair and optimization of Windows 11. Designed and coded with Claude, an AI by Anthropic.
es.MausAppComments=Auditoría, reparación y optimización transparente de Windows 11. Diseñado y programado con Claude, una IA de Anthropic.
fr.MausComment=Audit, réparation et optimisation de Windows 11
en.MausComment=Audit, repair and optimization of Windows 11
es.MausComment=Auditoría, reparación y optimización de Windows 11
fr.MausDeleteData=Supprimer aussi les sauvegardes de pilotes, les journaux de diagnostic et les fichiers temporaires de MAUS (%%ProgramData%%\MAUS) ?%n%nLes sauvegardes de pilotes peuvent peser plusieurs Go, mais elles ne pourront plus servir à remettre un pilote. Le journal des corrections est gardé dans tous les cas.
en.MausDeleteData=Also delete the driver backups, diagnostic logs and temporary files of MAUS (%%ProgramData%%\MAUS)?%n%nDriver backups can take several GB, but they can then no longer be used to restore a driver. The journal of fixes is kept in any case.
es.MausDeleteData=¿Eliminar también las copias de seguridad de controladores, los registros de diagnóstico y los archivos temporales de MAUS (%%ProgramData%%\MAUS)?%n%nLas copias de controladores pueden ocupar varios GB, pero ya no servirán para restaurar un controlador. El registro de correcciones se conserva en cualquier caso.
fr.MausPawnIoStays=Le pilote PawnIO, installé à votre demande pour les mesures avancées, reste sur ce PC. Pour le retirer : Paramètres > Applications > Applications installées > PawnIO.
en.MausPawnIoStays=The PawnIO driver, installed at your request for advanced measurements, stays on this PC. To remove it: Settings > Apps > Installed apps > PawnIO.
es.MausPawnIoStays=El controlador PawnIO, instalado a petición suya para las mediciones avanzadas, sigue en este PC. Para quitarlo: Configuración > Aplicaciones > Aplicaciones instaladas > PawnIO.

[Messages]
; Le journal reste après la désinstallation, mais c'est MAUS qui sait remettre les réglages : à dire avant, pas après.
fr.ConfirmUninstall=Voulez-vous vraiment désinstaller %1 ?%n%nPour retrouver vos réglages d'origine, annulez d'abord les corrections dans MAUS > Historique > « Tout annuler ».
en.ConfirmUninstall=Do you really want to uninstall %1?%n%nTo get your original settings back, first undo the fixes in MAUS > History > "Undo everything".
es.ConfirmUninstall=¿Seguro que desea desinstalar %1?%n%nPara recuperar sus ajustes originales, deshaga primero las correcciones en MAUS > Historial > «Deshacer todo».

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Fichiers nommés d'après la version de .NET : une mise à jour doit retirer ceux de la version précédente.
Type: files; Name: "{app}\mscordaccore_amd64_amd64_*.dll"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MAUS"; Filename: "{app}\MAUS.exe"; Comment: "{cm:MausComment}"
Name: "{autodesktop}\MAUS"; Filename: "{app}\MAUS.exe"; Tasks: desktopicon

[Run]
; MAUS s'ouvre sans droits administrateur (le moteur est lancé plus tard, à la demande, avec l'invite de Windows) : l'entrée
; postinstall est lancée avec les droits de l'utilisateur d'origine (comportement par défaut d'Inno Setup, runasoriginaluser).
Filename: "{app}\MAUS.exe"; Description: "{cm:LaunchProgram,MAUS}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Seulement la tâche de MAUS lui-même (créée par « Audit automatique chaque semaine ») ; sans elle, rien à faire.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MAUS\WeeklyAudit"" /F"; Flags: runhidden; RunOnceId: "RetirerAuditHebdomadaire"

[Code]
// Hors mode silencieux : une question (réponse par défaut : Non) et un rappel. Le journal et les choix ne sont jamais touchés.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;

  Data := ExpandConstant('{commonappdata}\MAUS');
  if (DirExists(Data + '\pilotes') or DirExists(Data + '\logs') or DirExists(Data + '\tasks'))
    and (MsgBox(CustomMessage('MausDeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    DelTree(Data + '\pilotes', True, True, True);
    DelTree(Data + '\logs', True, True, True);
    DelTree(Data + '\tasks', True, True, True);
  end;

  if RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO') then
    MsgBox(CustomMessage('MausPawnIoStays'), mbInformation, MB_OK);
end;
