# MAUS — Security design of the elevated engine (Microsoft Store review)

This document answers the ten points requested by Microsoft Store certification for the restricted capability `allowElevation` (policy 10.6.3). It describes MAUS **0.8.3**, the version submitted with this document (source: https://github.com/TERMINATORmdel101/MAUS). Every statement below was checked against the source code, and file paths are given so a reviewer can verify them. Where a protection does **not** exist, this document says so.

MAUS is a free, open-source (GPL-3.0) Windows 11 maintenance utility. It has no telemetry and no network service. It uses the network only when the user asks for it: winget, a web search opened in the browser, or the ping test.

**Contents.** §0 Architecture · §1 Allowlist of operations · §2 Commands, APIs, executables, arguments and targets · §3 Complete scope of system changes · §4 Inputs and validation · §5 Pipe authentication, authorization, process verification and anti-replay · §6 No arbitrary commands, scripts, paths, arguments or executables · §7 Elevation timing and engine termination per workflow · §8 Confirmation before changes, and declined UAC · §9 Restore point, partial failure, cancellation, logging and undo · §10 Separation of read-only and privileged functions.

---

## 0. Architecture

`MAUS.exe` (the user interface) runs **without administrator rights** (manifest `asInvoker`), and nothing is elevated at start.

MAUS opens in **limited mode**: every control that needs administrator rights is greyed out. When the user explicitly clicks **"All features"**, the interface starts a second copy of the **same executable**, `MAUS.exe --engine <pipe-name> <ui-pid> <key-fingerprint>`. It uses `ShellExecute` with the verb `runas`, so Windows shows its UAC prompt.

The elevated copy (the "engine") behaves as follows:
- It first validates its own command line. The pipe name must match `^MAUS-[0-9a-f]{32}$`, the PID must be a decimal number and the key fingerprint must be 64 lowercase hexadecimal digits; otherwise it exits with code 2 without doing anything (`src/Maus.App/App.xaml.cs`, `RunEngineAsync`).
- It accepts a **closed list of operations** over a named pipe, only from the process that started it, and only requests signed with that process's secret key, each with a sequence number that can be used once (§5).
- It stops when the interface exits, or after 15 minutes without any request.

The interface never sends a command line, a script, a registry path, a value to write or an executable path. It sends an operation name and identifiers. The engine recomputes everything it does from its own audit, its own lists and its built-in catalogs.

Code:
- `src/Maus.App/EngineHost.cs`: launch.
- `src/Maus.Core/Engine/Broker/`: `OnDemandMausEngine`, `EngineServer`, `RemoteMausEngine`, `EngineProtocol`, `LocalMausEngine`.

---

## 1. Exact allowlist of engine operations

The engine dispatcher (`EngineServer.cs`, `EngineServer.Workshop.cs`) recognises exactly the 32 operation names below. Any other name returns the error "Opération inconnue" (unknown operation), and nothing is executed.

**Needs administrator rights** column. "Yes" means the operation needs administrator rights. In limited mode (engine not running), every control that leads to such an operation is **greyed out**, and the only ways to start the engine are the "All features" button of the banner at the top of the window, the startup window, or the "Enable advanced sensors" button (§7); the operation itself then runs in the engine. "No" means the operation also works without rights: it goes through the engine only if the engine is already running (it then runs with administrator rights), otherwise it runs in the non-elevated interface. A "No" operation never starts the engine and never shows UAC (`OnDemandMausEngine.cs`, methods `Elevated` and `Available`).

| # | Operation | Purpose | Needs administrator rights | Changes the system? |
|---|---|---|---|---|
| 1 | `ping` | Check that the engine answers | — (sent once by the launcher) | No |
| 2 | `audit` | Full read-only audit (20 modules) | Yes | No |
| 3 | `module` | Re-run one audit module | Yes | No |
| 4 | `plan` | List the possible fixes, computed from the engine's own audit | Yes | No |
| 5 | `apply` | Apply fixes chosen by id from the engine's own plan | Yes | **Yes**, §3.1 |
| 6 | `verify` | Store the post-apply check note ("no effect", "restart needed") in the journal | Yes | MAUS journal file only |
| 7 | `revert` | Undo a session or one change, using journaled values | Yes | **Yes**: restores journaled values |
| 8 | `journal` | List journal sessions | Yes | No |
| 9 | `session` | Read one journal session | Yes | No |
| 10 | `prefs.load` | Read protected preferences | Yes | No |
| 11 | `prefs.save` | Save protected preferences | Yes | MAUS file only |
| 12 | `inventory` | Hardware inventory (CPUID, WMI, registry reads; memory SPD read through PawnIO when installed and elevated) | No | No |
| 13 | `drivers` | List drivers (WMI, cfgmgr32) | No | No |
| 14 | `drivers.backups` | List MAUS driver backups | Yes | No |
| 15 | `sensors` | Read sensors (PDH, D3DKMT, NVML; PawnIO read-only when installed and elevated) | No | No |
| 16 | `processes` | List processes | No | No |
| 17 | `process.terminate` | Terminate one process chosen by the user | No | **Yes**: one process |
| 18 | `memory` | Memory module SPD and memory-controller timings (PawnIO, read-only, §3.3) | Yes | No |
| 19 | `latency` | DPC/ISR latency measurement (ETW kernel session, at most 5 minutes) | Yes | Temporary ETW session |
| 20 | `frames.start` | Start the frame-rate counter (bundled PresentMon) | Yes | Temporary ETW session |
| 21 | `frames.read` | Read the counter | Never (engine must already run) | No |
| 22 | `frames.stop` | Stop the counter | Never | Closes the ETW session |
| 23 | `space` | Disk-space analysis (read-only enumeration) | No | No |
| 24 | `console` | Open a **visible** console for one of 6 fixed tasks (§2.2) | Yes | **Yes** |
| 25 | `restorepoint.list` | List restore points and shadow-storage space | Yes | No |
| 26 | `restorepoint.create` | Create a restore point | Yes | **Yes**: restore point |
| 27 | `restorepoint.driver` | Restore point before a driver action | Yes | **Yes**: restore point |
| 28 | `schedule.get` | Read the MAUS weekly-audit task | No | Only re-points the MAUS task after a Store update (§2.3) |
| 29 | `schedule.set` | Create or remove the MAUS weekly-audit task | Yes | **Yes**: task `\MAUS\WeeklyAudit` only |
| 30 | `software` | List winget upgrades (`winget upgrade`, read-only) | No | No |
| 31 | `battery.report` | `powercfg /batteryreport` to a fixed MAUS path, always run without administrator rights (never by the engine) | No | One report file |
| 32 | `benchmark` | Start the MAUS benchmark (same executable) | No | No |


---

## 2. Commands, APIs, executables, arguments and targets

No operation accepts a command line, an executable path, a script or a free-form argument from the interface. Executables are fixed in code and resolved from protected folders. An automated test (`tests/Maus.Core.Tests/Security/SecurityGuardTests.cs`) makes the build fail in these cases:
- a program is started from any source file other than the 8 reviewed launchers;
- `UseShellExecute = true` is used anywhere other than the `runas` launch of the engine;
- `cmd.exe` is started anywhere other than `VisibleConsole`.

### 2.1 `apply` and `revert` (fixes)

Writes are done only by `FixEngine` (`src/Maus.Core/Fixes/FixEngine.cs`) through `SettingsAccessor.Write` (`src/Maus.Core/Fixes/SettingsAccessor.cs`). There are exactly five kinds of write (`SettingKind`):

| Kind | API | Target |
|---|---|---|
| `Registry` | `RegistryKey.SetValue` / `DeleteValue` (Win32 registry API through .NET) | Only the values listed in §3.1 |
| `SystemParameter` | `SystemParametersInfoW` with `SPIF_UPDATEINIFILE \| SPIF_SENDCHANGE` | 10 visual-effect flags of the current user (§3.1, M06) |
| `MinimizeAnimation` | `SystemParametersInfoW(SPI_SETANIMATION)` | Minimize/maximize animation of the current user |
| `ActivePowerScheme` | `PowerSetActiveScheme` (powrprof.dll, documented API) | Active power plan: only the built-in Windows plans "High performance" or "Balanced" (fixed GUIDs), chosen by the engine from the audit; undo restores the previous plan's GUID read before the change |
| `ScheduledTask` | `%SystemRoot%\System32\schtasks.exe /Change /TN <path\name> /DISABLE` (undo: `/ENABLE`), arguments passed separately (`ArgumentList`) | Third-party logon tasks found by the M12 audit. Tasks under `\Microsoft\` and disabled tasks are never listed |

After an apply or undo that changed something, the engine broadcasts `WM_SETTINGCHANGE` (`SendMessageTimeoutW`, `SMTO_ABORTIFHUNG`, 2 s) so that Windows picks up the change.

### 2.2 `console`: six fixed tasks in a visible console window

`VisibleConsole.Run` starts `%SystemRoot%\System32\cmd.exe` with `/k`, or `/s /k "…"`, and a command line that MAUS builds itself (`src/Maus.Core/Platform/VisibleConsole.cs`). The working directory is System32. The window stays open so the user can read every message.

The command line contains only:
- fixed text;
- tool paths fixed in code;
- identifiers validated as described below.

Text displayed with `echo`, including package names from winget and device names from Windows, is escaped first (`RepairConsole.Escape`):
- `^ & | < > ( )` are escaped;
- `%` and `!` are removed;
- `"` becomes `'`;
- line breaks become spaces.

`winget.exe` is used **only** from the `Microsoft.DesktopAppInstaller` package folder under `%ProgramFiles%\WindowsApps`, which standard users cannot modify. The alias in the user profile is never used, because a standard user could replace it (`Winget.Locate`, `ReadOnlyCommandRunner.IsInProtectedAppsFolder`). A winget path that contains `"` is refused.

| Task (`ConsoleKind`) | Exact command | Input accepted and validation |
|---|---|---|
| `Repair` | `cmd.exe /k title MAUS & echo … & DISM.exe /Online /Cleanup-Image /RestoreHealth & sfc.exe /scannow & echo …` (working directory System32) | None |
| `WingetUpgrade` | For each chosen package: `"<WindowsApps>\winget.exe" upgrade --id <ID> --exact --source winget`. Winget stays interactive: the user accepts the publishers' licence terms in the console | Package ids. Each id must be in the list that the **engine itself** obtained from `winget upgrade`. If the engine has no list yet, it reads it before opening the console; it never uses the interface's list. Each id must contain only ASCII letters, digits and `. - _ +` (`SoftwareUpdate.CanTarget`) |
| `PawnIoInstall` | `"<WindowsApps>\winget.exe" install --id namazso.PawnIO --exact --source winget` | None (id fixed in code) |
| `PawnIoUninstall` | `"<WindowsApps>\winget.exe" uninstall --id namazso.PawnIO --exact --source winget` | None |
| `DriverAction` | `"%SystemRoot%\System32\pnputil.exe"` with one of: `/restart-device "<InstanceId>"` · `/remove-device "<InstanceId>"` then `/scan-devices` · `mkdir "<backup folder>"` then `/export-driver oem<N>.inf "<backup folder>"` · the same export, then `/delete-driver oem<N>.inf /uninstall` **only if the export succeeded** (`&&`) | Action from a closed enum (Restart, Reinstall, Backup, Remove). The device must be a **display adapter** in the driver list the engine reads itself, at the moment of the request. Its instance id must be under 400 characters and match `^[A-Za-z0-9_\\&.\-#{}]+$`; it is passed in quotes. Reinstall only for `PCI\` devices. Backup and Remove only for third-party packages `oem<number>.inf` (never a Windows inbox driver). The backup folder is built by MAUS: `%ProgramData%\MAUS\pilotes\<yyyyMMdd-HHmmss>-<inf name>` |
| `DriverRestore` | `"%SystemRoot%\System32\pnputil.exe" /add-driver "<backup folder>\*.inf" /subdirs /install` | Backup folder **name** only: 1–200 characters, no invalid file-name characters, no `\` or `/`, not `.` or `..`. It is resolved inside `%ProgramData%\MAUS\pilotes`, must exist and must contain an `.inf` file. Every path placed in a `pnputil` command must be under 250 characters and contain none of `" % ! & \| < > ^`, CR or LF (the winget path, inside WindowsApps, must not contain `"`) |

Exit codes of `pnputil` are interpreted in the console (0 = success, 3010 = restart needed, 259 = nothing to update, anything else = failure) and shown to the user (`GraphicsDriverActions.Outcome`).

### 2.3 Other operations that change something

| Operation | API / command | Target and limits |
|---|---|---|
| `process.terminate` | `Process.Kill(entireProcessTree: false)` | The engine re-reads the process list itself and finds the PID. If the interface gave a start time and it differs, nothing is done (reused PID). Just before killing, it compares the start time reported by Windows with its own reading. It refuses MAUS itself, the interface process, PID 0 and 4, and critical processes (`ProcessRules.CanTerminate`, `src/Maus.Core/Workshop/Processes.cs`: System, Idle, Registry, Memory Compression, Secure System, smss, csrss, wininit, winlogon, services, lsass, lsaiso, svchost, fontdrvhost, dwm, MsMpEng, NisSrv, SecurityHealthService) |
| `restorepoint.create`, `restorepoint.driver` | WMI `root\default:SystemRestore.CreateRestorePoint` (type `MODIFY_SETTINGS`, event `BEGIN_SYSTEM_CHANGE`). Just before the call, HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` is set to `0`, and its previous value is restored right after (Windows otherwise silently skips a point if another is less than 24 h old). The new point is then **verified** by re-reading the list of points | Description fixed by MAUS ("MAUS : point créé à la main", "MAUS : avant la suppression du pilote graphique"). These two operations **never** enable system protection |
| `schedule.set` | `%SystemRoot%\System32\schtasks.exe /Create /TN MAUS\WeeklyAudit /XML <file> /F`, or `/Delete /TN MAUS\WeeklyAudit /F` | Input is a day of the week, or none to delete. MAUS generates the XML (`ScheduledAudit.TaskXml`): weekly at noon, `LogonType InteractiveToken`, `RunLevel HighestAvailable`, action `MAUS.exe --scheduled-audit` (read-only audit; a notification only if a red problem is found; nothing is applied). The XML file is written in `%ProgramData%\MAUS\tasks` (SYSTEM + Administrators only) and deleted after use. Creation is **refused** unless `MAUS.exe` is under `Program Files` or `Program Files (x86)` (WindowsApps included), because a task with highest rights must not start an executable that a standard user could replace |
| `schedule.get` | `schtasks.exe /Query /TN MAUS\WeeklyAudit /XML` | Read. Only exception: if the existing MAUS task points to an **older version folder of the same Store package** (the WindowsApps folder name changes at each update), the engine re-creates the same task pointing to the current `MAUS.exe`, with the same rules as `schedule.set` |
| `latency` | ETW real-time session `NT Kernel Logger` (DPC/ISR events) | Duration: 0 < d ≤ 5 min; the session is stopped at the end |
| `frames.start` / `frames.stop` | Bundled `<MAUS folder>\PresentMon\PresentMon.exe` (Intel PresentMon 2.6.0). Its SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af` is checked before **each** start; a different file is never started. The file is opened with read-only sharing for the check and kept open until the process has started, so it cannot be replaced, renamed or deleted in between (`PresentMonCapture.OpenVerified`). Fixed arguments, passed separately: `--output_stdout --v1_metrics --no_console_stats --stop_existing_session --session_name <name> --exclude dwm.exe --exclude MAUS.exe`. Cleanup: `--terminate_existing_session --session_name <name>` | Session name must be `MAUS_Compteur` or `MAUS_Releve`. All captures are stopped when the engine stops |
| `battery.report` | `%SystemRoot%\System32\powercfg.exe /batteryreport /output <path>` | Path fixed by MAUS and enforced by the command allowlist: `%LOCALAPPDATA%\MAUS\rapports\rapport-batterie.html` (the previous report is deleted first). Always run by the interface without administrator rights, even when the engine is running: `powercfg.exe` writes into the user profile and does not inherit MAUS's process hardening |
| `benchmark` | The same `MAUS.exe` with `--benchmark --api <api> --resolution <res> --lang <lang>` [`--only <scope>`] [`--raytracing off`], arguments passed separately, no shell | Each value from a closed set: api {d3d11, d3d12}, resolution {720p, 1080p, 1440p, 4k}, scope {all, gpu, cpu}, language {fr, en, es}, ray tracing on/off. When the engine runs, the benchmark is started by it (so with administrator rights) so that the CPU temperature can be read through PawnIO for the safety stop. The benchmark process then applies the engine's process hardening (§6) and does not use its compiled-shader cache: shaders are compiled in memory |
| `prefs.save` | Atomic file write | `%ProgramData%\MAUS\settings\preferences.json` only |

### 2.4 Read-only commands used by the audit

The audit runs external tools only through `ReadOnlyCommandRunner` (`src/Maus.Core/Platform/ReadOnlyCommandRunner.cs`):
- the executable **and** its arguments are checked against an allowlist;
- a tool named without a folder is taken from `%SystemRoot%\System32` **only**: if it is not there, it is reported as missing, and the `PATH` is never searched;
- winget is accepted only from WindowsApps;
- anything else is refused before starting.

Allowed commands:
- `powercfg /getactivescheme | /list | /a | /availablesleepstates | /query`
- `powercfg /batteryreport /output <fixed MAUS path>`
- `bcdedit /enum`
- `fsutil behavior query …`
- `reagentc /info`
- `dsregcmd /status`
- `manage-bde -status`
- `netsh winhttp show …`
- `nvidia-smi --query-gpu … | -q … | --help`
- `winget upgrade --source winget --disable-interactivity` (fixed list of flags)
- `dism /English /Online /Cleanup-Image /CheckHealth` (exact sequence)

The audit also reads the registry, WMI/CIM, event logs, the package list and files through read-only interfaces (§10).

---

## 3. Complete scope of system changes

### 3.1 Registry values that a fix can write or delete

Fixes are built only from code and embedded catalogs (`src/Maus.Core/Catalog/*.json`, `src/Maus.Core/Modules/*`), and only for a value that the audit found different from the target. "Delete" means the fix removes a policy value so that Windows returns to its own default; MAUS never writes the "enabled" value of a policy to restore it. "(Pro+)" means the value is only written on Pro, Enterprise and Education editions. Values marked "optional" are never pre-checked.

**M01 — security defaults restored (HKLM)**

| Key | Values (action) |
|---|---|
| `SOFTWARE\Policies\Microsoft\Windows Defender` | `DisableAntiSpyware` (delete) |
| `SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection` | `DisableRealtimeMonitoring` (delete) |
| `SOFTWARE\Policies\Microsoft\WindowsFirewall\{DomainProfile, PrivateProfile, StandardProfile, PublicProfile}` | `EnableFirewall` (delete, only for profiles where it is 0) |
| `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` | `EnableLUA` = 1, `ConsentPromptBehaviorAdmin` = 5, `PromptOnSecureDesktop` = 1 |
| `SOFTWARE\Policies\Microsoft\Windows\System` | `EnableSmartScreen` and `ShellSmartScreenLevel` (delete) |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` | `WUServer`, `WUStatusServer`, `DoNotConnectToWindowsUpdateInternetLocations`, `TargetReleaseVersion`, `TargetReleaseVersionInfo`, `ProductVersion` (delete) |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` | `UseWUServer`, `NoAutoUpdate` (delete) |
| `SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` | `FlightSettingsMaxPauseDays`, `PauseUpdatesExpiryTime`, `PauseUpdatesStartTime`, `PauseFeatureUpdatesStartTime`, `PauseFeatureUpdatesEndTime`, `PauseQualityUpdatesStartTime`, `PauseQualityUpdatesEndTime` (delete) |
| `SYSTEM\CurrentControlSet\Services\<name>` | `Start` restored to the Windows default, **only if the service is currently disabled**: wuauserv 3, UsoSvc 2, WaaSMedicSvc 3, BITS 3, CryptSvc 2, TrustedInstaller 3, Audiosrv 2, WSearch 2, WinDefend 2, mpssvc 2 |
| `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` | `Shell` = `explorer.exe`, `Userinit` = `%windir%\system32\userinit.exe,` (only if replaced) |
| HKCU `Software\Microsoft\Windows NT\CurrentVersion\Winlogon` | `Shell` (delete, only if a per-user shell replacement exists) |
| `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows` and the `WOW6432Node` copy | `AppInit_DLLs` = empty, `LoadAppInit_DLLs` = 0 (only if a DLL is set) |
| `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>` and the `WOW6432Node` copy | `Debugger` (delete, only for values found by the audit) |

**M13 — security trade-offs (HKLM)**

| Key | Values (action) |
|---|---|
| `SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management` | `FeatureSettingsOverride`, `FeatureSettingsOverrideMask` (delete: this **re-enables** CPU mitigations; MAUS never disables them) |
| `SYSTEM\CurrentControlSet\Control\CI\Config` | `VulnerableDriverBlocklistEnable` = 1 |

**M04 — privacy**

| Hive and key | Values (action) |
|---|---|
| HKLM `SOFTWARE\Policies\Microsoft\Windows\DataCollection` | `AllowTelemetry` = 1 (0 on Enterprise/Education); `LimitDiagnosticLogCollection` = 1 and `LimitDumpCollection` = 1 (Pro+); `DoNotShowFeedbackNotifications` = 1 (Pro+) |
| HKLM `SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo` | `DisabledByGroupPolicy` = 1 (Pro+) |
| HKLM `SOFTWARE\Policies\Microsoft\Windows\Windows Search` | `EnableDynamicContentInWSB` = 0 (Pro+) |
| HKLM `SOFTWARE\Policies\Microsoft\Windows\System` | `EnableActivityFeed`, `PublishUserActivities`, `UploadUserActivities` = 0 |
| HKLM `SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization` | `DODownloadMode` = 0 (Pro+) |
| HKLM `SOFTWARE\Policies\Microsoft\Windows\WindowsAI` | `DisableAIDataAnalysis` = 1 (Recall; Pro+; reinforced warning; never pre-checked) |
| HKCU `Software\Microsoft\Siuf\Rules` | `NumberOfSIUFInPeriod` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\Privacy` | `TailoredExperiencesWithDiagnosticDataEnabled` = 0 |
| HKCU `Software\Policies\Microsoft\Windows\CloudContent` | `DisableTailoredExperiencesWithDiagnosticData` = 1 (Pro+) |
| HKCU `Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo` | `Enabled` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` | `Start_IrisRecommendations` = 0 |
| HKCU `Software\Policies\Microsoft\Windows\Explorer` | `DisableSearchBoxSuggestions` = 1 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\SearchSettings` | `IsDynamicSearchBoxEnabled` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager` | `RotatingLockScreenOverlayEnabled`, `SubscribedContent-338387Enabled`, `SubscribedContent-338389Enabled`, `SoftLandingEnabled`, `SilentInstalledAppsEnabled`, `SubscribedContent-338393Enabled`, `SubscribedContent-353694Enabled`, `SubscribedContent-353696Enabled` = 0 |

**M05 — power**
- HKLM `SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled` = 0 (fast startup).
- Active power plan (`PowerSetActiveScheme`, §2.1).

**M06 — visual effects (HKCU of the signed-in user, plus one HKLM policy)**

| Target | Values (action) |
|---|---|
| HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` | `ShowTaskViewButton` = 0, `IconsOnly` = 0, `ListviewAlphaSelect` = 1, `TaskbarAnimations` = 0, `ListviewShadow` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` | `EnableTransparency` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects` | `VisualFXSetting` = 3 |
| HKCU `Software\Microsoft\Windows\DWM` | `EnableAeroPeek` = 0, `AlwaysHibernateThumbnails` = 0 |
| `SystemParametersInfoW` flags | DragFullWindows (0x0026), FontSmoothing (0x004A), ClientAreaAnimation (0x1042), MenuAnimation (0x1002), TooltipAnimation (0x1016), SelectionFade (0x1014), CursorShadow (0x101A), DropShadow (0x1024), ComboBoxAnimation (0x1004), ListBoxSmoothScrolling (0x1006); minimize animation (SPI_SETANIMATION) |
| HKLM `SOFTWARE\Policies\Microsoft\Dsh` | `AllowNewsAndInterests` = 0 (Widgets; Pro+), or delete |
| "Windows defaults" (optional, advanced) | For each checkbox that Windows declares under HKLM `SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\<name>` (at most 64 entries), MAUS writes the default declared there. This is limited to the SPI flags above, or to a **DWORD under HKCU** at the `RegPath` / `ValueName` that Windows declares there. That HKLM key is writable by administrators only |

**M07 — Game Bar (profile chosen by the user)**
- HKCU `Software\Microsoft\GameBar`: `AutoGameModeEnabled` = 1, `UseNexusForGameBarEnabled` = 0.
- HKCU `System\GameConfigStore`: `GameDVR_Enabled` = 0.
- HKCU `Software\Microsoft\Windows\CurrentVersion\GameDVR`: `AppCaptureEnabled` = 0, `HistoricalCaptureEnabled` = 0.
- HKLM `SOFTWARE\Policies\Microsoft\Windows\GameDVR`: `AllowGameDVR` = 0 (Pro+), or delete.

**M09 — graphics**
- HKLM `SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` = 2.
- HKLM `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\ExcludeWUDriversInQualityUpdate` = 1 (optional, advanced, reinforced warning).
- HKLM `SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching\SearchOrderConfig` = 0 (optional, advanced, reinforced warning).

**M12 — startup**
- `Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run, Run32, StartupFolder}\<entry>`, under HKCU or HKLM depending on where the entry is registered: binary value in the format that Task Manager uses (disabled flag followed by the date). The startup program itself is not touched.
- Third-party logon scheduled tasks: disabled with `schtasks /Change /DISABLE` (§2.1).

**Not a fix, but written around a restore point**
- HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency`, set to 0 during the call and then restored (§2.3).

If the engine was elevated with a **different** administrator account than the signed-in user, every HKCU and per-user SPI change (apply and undo) is skipped and the user is told why (`FixContext.ElevatedAsAnotherUser`). This way, the wrong profile is never modified.

### 3.2 Files and folders written

| Path | Content | Written by | Protection |
|---|---|---|---|
| `%ProgramData%\MAUS\journal\` | Journal of original values (one JSON file per session) | Engine | Folder reserved to SYSTEM + Administrators; files owned by Administrators; atomic writes; a file with another owner is ignored |
| `%ProgramData%\MAUS\settings\preferences.json` | Protected choices | Engine | Same |
| `%ProgramData%\MAUS\tasks\maus-task-<guid>.xml` | Temporary XML of the weekly task, deleted after use (if an antivirus holds it, it stays in that protected folder) | Engine | Same |
| `%ProgramData%\MAUS\pilotes\<folder>` | Driver backups (`pnputil /export-driver`) | Engine (console) | Same: the engine re-applies this protection (and refuses a link) before listing, backing up or restoring |
| `%LOCALAPPDATA%\MAUS\rapports\rapport-batterie.html` | Windows battery report | Interface (`powercfg.exe` without administrator rights) | User profile |
| `%LOCALAPPDATA%\MAUS\` (settings, history, logs) | Display settings, score history, crash logs | Interface; also the engine (its own crash log) and the weekly audit (score history, crash log), both with RedirectionGuard (§6) | User profile |
| `%LOCALAPPDATA%\MAUS\benchmark-history.json`, `benchmark\`, `bench-shaders\` | Benchmark results, result images (older ones deleted), compiled-shader cache | Benchmark process: with administrator rights when the engine started it (§2.3), then with the engine's process hardening of §6 (RedirectionGuard included) and **without** reading or writing the shader cache; without rights, it uses the cache | User profile |
| `%TEMP%` | Temporary test files (disk speed test; benchmark thumbnails in `MAUS-benchmark-<pid>`), deleted after use | Interface; benchmark process for the thumbnails | User profile |

### 3.3 Services, tasks, drivers and other configuration

- **Services:** no service is started, stopped, created or deleted. Only the `Start` value of the 10 services listed in §3.1 can be restored to the Windows default, and only when that service is disabled.
- **Scheduled tasks:** `\MAUS\WeeklyAudit` can be created, deleted or re-pointed (§2.3). Third-party logon tasks can be disabled and re-enabled (§2.1). Tasks under `\Microsoft\` are never changed.
- **Drivers:** only the display adapter, only through `pnputil` (§2.2), with each action confirmed by the user. A verified restore point is created before a driver is deleted (if it fails, the user decides whether to continue; the driver is backed up first in any case). MAUS ships no kernel driver.
- **PawnIO:** PawnIO, a signed third-party driver, is installed or removed only through winget, at the user's request, in a visible console. When PawnIO is installed and MAUS has administrator rights, MAUS uses it **only to read** (`src/Maus.Core/Workshop/Memory/PawnIo/SpdBusDriver.cs`, `PawnIoMemorySources.cs`):
  - sensors, through LibreHardwareMonitorLib;
  - memory SPD chips;
  - memory-controller registers (Intel `IntelMCHBAR` / `IntelMSR` modules, AMD SMN register reads);
  - the AMD power-management table (an SMU command that refreshes the table, then a read).

  A software guard in front of the SMBus module refuses every SMBus write, except the page-selection writes that reading an SPD chip requires (addresses 0x36/0x37 for DDR4; register MR11 at addresses 0x50–0x57 for DDR5). MAUS therefore cannot modify an SPD chip or a DDR5 power-management IC.
- **System protection:** WMI `SystemRestore.Enable("C:\")` is called only during `apply`, only if system protection is off **and** the user ticked the option to allow it (`ApplyOptions.EnableProtectionIfNeeded`).
- **Temporary ETW sessions:** `NT Kernel Logger` (latency, at most 5 minutes) and `MAUS_Compteur` / `MAUS_Releve` (PresentMon), stopped at the end and when the engine stops.
- **Display:** the refresh-rate trial (`ChangeDisplaySettingsExW`) runs in the **non-elevated** interface, never in the engine. It is applied to the current session only and rolled back automatically after 15 seconds unless the user keeps it.
- **Never touched:** BIOS/UEFI, BCD and boot configuration, Defender exclusions, firewall rules, hosts file, proxy, page file, user accounts, credentials, CPU mitigations (MAUS only re-enables them).

---

## 4. Inputs, permitted values and validation per operation

General rules (`EngineServer.cs`, `EngineProtocol.cs`):
- A message is a 4-byte little-endian length followed by UTF-8 JSON. A length ≤ 0 or above **32 MiB** is refused before the body is read.
- Every request is signed (HMAC-SHA256) and carries a sequence number that can be used only once (§5). A request with a wrong signature or a used number closes the connection without an answer, before its content is read.
- One request per connection. A malformed message closes that connection; nothing is executed.
- JSON is deserialized into fixed record types. Unknown fields are ignored. A missing or wrongly typed field returns an error, and nothing is executed.
- Identifiers (`Checked`) must have 1–200 characters, with no control characters and no `\`, `/` or `:`. At most **500** identifiers per request.
- Language: if it is not one of fr, en, es, the engine uses the language of Windows. It only selects message texts.

| Operation | Inputs | Validation |
|---|---|---|
| `ping`, `audit`, `plan`, `journal`, `prefs.load`, `inventory`, `drivers`, `drivers.backups`, `sensors`, `processes`, `memory`, `restorepoint.list`, `restorepoint.create`, `restorepoint.driver`, `schedule.get`, `software`, `battery.report` | None | — |
| `module` | Module id | Checked id; an id that is not one of the 20 built-in modules returns nothing |
| `apply` | List of fix ids; options `CreateRestorePoint` (bool, pre-checked in the interface), `EnableProtectionIfNeeded` (bool), `ProceedWithoutRestorePoint` (bool), `RestorePointDescription` (text) | The engine **recomputes the plan** from its own audit and keeps only the ids found in it. An unknown id is ignored. The interface cannot supply a registry path, a value or a task name. If the engine restarted since its last audit, it audits again before planning. The description is only passed as the restore point name to WMI; the interface always sends the default "MAUS : avant corrections" |
| `verify` | Session id; list of check results (fix id, effect, message) | Only results "no effect" and "pending restart" are kept, as a display note on entries that are already applied. A value to write or to restore can never be changed this way |
| `revert` | Session id, optional change id, `force` (bool) | Session and change must exist in the protected journal. The values written back are the ones the engine journaled itself. The interface always sends `force = false`; only the command-line tool, started by an administrator, offers `--force` |
| `session` | Session id | Checked id; must exist in the journal |
| `prefs.save` | Preferences object | Deserialized into the fixed `UserPreferences` record. Preferences only choose between options built into MAUS (for example the Game Bar profile, or findings marked as wanted) |
| `process.terminate` | PID (int), optional start time | §2.3 |
| `latency` | Duration | 0 < duration ≤ 5 minutes |
| `frames.start` | Session name, optional buffer duration | Session ∈ {`MAUS_Compteur`, `MAUS_Releve`}; the duration only sizes the in-memory buffer |
| `frames.read` | Session name, optional time windows, minimum frame count | Same session check; count clamped |
| `frames.stop` | Session name | Same session check |
| `space` | Folder path | Fully qualified local path; UNC and device paths (`\\…`, `\\?\`, `\\.\`) refused; must exist. Read-only enumeration (depth ≤ 256) |
| `console` | Kind + ids (§2.2) | Kind ∈ the six values of §2.2; each id validated as described there |
| `schedule.set` | Day of week, or none | Enum only |
| `benchmark` | api, resolution, scope, language, ray tracing (bool) | Closed sets (§2.3); anything else refused |

---

## 5. Named-pipe authentication, authorization, process verification and anti-replay

| Control | Implementation |
|---|---|
| Engine command line | The pipe name must match `^MAUS-[0-9a-f]{32}$`, the client PID must be a number, and the key fingerprint must be 64 lowercase hexadecimal digits; otherwise the engine exits (code 2) |
| Unpredictable name | `MAUS-<random GUID>` (`Guid.NewGuid`, 122 random bits), generated by the interface for each engine start |
| Pipe squatting | The first instance is created with `PipeOptions.FirstPipeInstance`: if another process already owns the name, the engine fails to start instead of serving it |
| Access control list | Explicit `PipeSecurity` with two entries only: **Deny** `NETWORK` (S-1-5-2) full control, and **Allow** the account the engine runs under (`ReadWrite \| CreateNewInstance`). Remote clients are refused |
| Client verification (engine side) | For **every** connection, `GetNamedPipeClientProcessId` must equal the PID of the interface that started the engine (passed on the command line and validated). Any other process is disconnected without an answer, and the attempt is recorded in the engine's diagnostic breadcrumbs |
| Server verification (interface side) | For every connection, `GetNamedPipeServerProcessId` must equal the PID of the engine process that the interface started itself (`Process.Start` result). Otherwise the interface refuses to use the pipe and tells the user |
| Secret key | For each engine start, the interface draws a new 256-bit key from the Windows cryptographic random generator (`RandomNumberGenerator`). The key is **never** put on the command line, which other programs can read: only its SHA-256 fingerprint is. On its first connection, the interface presents the key over the pipe; the engine accepts it only if its SHA-256 matches the fingerprint it received at launch (constant-time comparison), and keeps it for its lifetime. Before that, every request is refused (`src/Maus.Core/Engine/Broker/EngineKey.cs`) |
| Request authentication | Every request is the exact JSON text of `{operation, language, arguments, sequence number}` with an HMAC-SHA256 signature computed with the key (domain-separated: `MAUS-REQ`). The engine checks the signature (constant time) **before** reading the request; a missing or wrong signature closes the connection without an answer |
| Anti-replay | Each request carries a sequence number that the interface increments. The engine accepts each number **once only**; it also refuses a number older than a window of 4,096 (`ReplayWindow`, same principle as the IPsec anti-replay window, RFC 4303 §3.4.3). The window allows several requests in parallel to arrive out of order. A recorded request sent again, or a request modified after signing, is refused without an answer |
| Response authentication | Every response (progress messages included) is signed with the key and bound to the sequence number of its request (domain `MAUS-RES`). The interface refuses a response that is not signed for its own request, so an old response cannot be replayed to it either |
| Authorization | The engine runs only operations from the closed list (§1), with the validation of §4. There is no "generic" operation |
| Message bounds | 32 MiB maximum, one request per connection (after the one-time key presentation), JSON into fixed types |
| Lifetime binding | The engine watches the interface process and exits when it exits |
| Security stop | After **3** refused connections (another process, a wrong key, a replayed or modified request), the engine stops by itself (`EngineServer.MaxRefusals`, exit code `SecurityStopExitCode`); the interface warns the user and returns to limited mode. The engine takes no other action, so this cannot be abused to harm the PC |

These controls are layered: a request is executed only if it arrives on a local connection, from the exact interface process that started this engine, on a pipe that only that account can open, signed with the key of this engine start, with a sequence number never used before. Even then, the engine only runs operations from its closed list, recomputed from its own audit and catalogs. Tests: `tests/Maus.Core.Tests/Engine/EngineKeyTests.cs` (signature, fingerprint, replay window) and `EngineBrokerTests.cs` (a real pipe: a request before the key presentation, a wrong key, a replayed request and a modified request are all refused; a response is valid only for its own request).

---

## 6. No arbitrary commands, scripts, paths, arguments or executables

- The protocol has no field for a command, script, executable path, registry path, value to write or argument list, and every request must be signed with the key of this engine start (§5). The interface sends an operation name from §1, plus identifiers and values from closed sets (§4).
- Every executable that the engine starts is fixed in code:
  - `cmd.exe`, `schtasks.exe`, `pnputil.exe` and `powercfg.exe` from `%SystemRoot%\System32` (the non-elevated interface also starts `%windir%\explorer.exe`, only to open a web page, a folder, a file or a Settings page with standard rights);
  - `DISM.exe` and `sfc.exe` started by that `cmd.exe`, whose working directory is System32;
  - the read-only tools of §2.4 from System32 only, never from the `PATH`;
  - `winget.exe` only from `%ProgramFiles%\WindowsApps`;
  - bundled PresentMon only after its SHA-256 check;
  - `MAUS.exe` itself for the benchmark.
- Programs are started with `ArgumentList` (each argument passed separately), except the visible consoles of §2.2. Those use a `cmd.exe` command line built by MAUS from constants and from identifiers that passed the validation of §2.2. Displayed text is escaped, a driver path containing a `cmd` special character is refused, and the winget path (inside WindowsApps) must not contain a quote.
- No PowerShell, no script host and no `cmd /c` with user text. `ShellExecute` is used only once: to start the engine itself with `runas`.
- Registry writes come only from the plan computed by the engine (§3.1). There is no "write this value" operation.
- These rules are enforced by an automated test that fails the build (`SecurityGuardTests`, §2), and every push is analysed by GitHub CodeQL (`security-extended`).
- **Process hardening** (`src/Maus.Core/Platform/ProcessHardening.cs`, `SetProcessMitigationPolicy`, applied at start before any request): the interface, the engine and the weekly audit refuse to load DLLs from a remote share or from a file with a low mandatory label (`NoRemoteImages`, `NoLowMandatoryLabelImages`). The elevated engine and the scheduled audit also search Windows DLLs in System32 first (`PreferSystem32Images`), disable legacy extension point DLLs (`ProcessExtensionPointDisablePolicy`), and enable RedirectionGuard (`EnforceRedirectionTrust`): they do not follow a filesystem junction created by a non-administrator account, which blocks the classic trick of redirecting an elevated write. These policies only restrict MAUS's own processes; a policy Windows refuses is logged and skipped. Microsoft notes that such policies are most effective when applied at process initialization: they are applied at the very start of each process, but do not cover what Windows loaded before that point. The benchmark process (`MAUS.exe --benchmark`) applies the engine's policies when it has administrator rights (started by the engine), and the interface's policies otherwise.

---

## 7. Elevation timing and engine termination per workflow

General behaviour:
- **At start**, there is no UAC prompt. The interface runs as a standard user. MAUS may show, in its own window, the disclaimer (first start, and again if its text changes), "What's new", and a window "How do you want to use MAUS?" that explains the two modes side by side: "Limited mode, without administrator rights" and "All features, with administrator rights". That window appears at each start until the user ticks "Don't ask me again" or turns it off in Settings > Administrator rights. UAC appears **only if** the user clicks "All features"; "Limited mode" keeps MAUS without rights.
- **Limited mode:** while the engine is not running, every control that needs administrator rights is greyed out (audit, fixes, undo, repair, software upgrades, PawnIO, driver actions and backups, restore points, weekly audit, protected choices, memory sheet, latency test, frame counter), and a banner at the top of the window says "Limited mode: greyed-out options need administrator rights" with an **"All features"** button (`src/Maus.App/ViewModels/ElevationState.cs`, `MainWindow.xaml`). No greyed-out control can be clicked, so no action starts a UAC prompt by surprise.
- **Switching to all features:** only an explicit click on "All features" (banner or startup window) or on "Enable advanced sensors" starts `MAUS.exe --engine` with `runas`; UAC appears at that moment. If two clicks happen together, only one prompt is shown. When the engine is ready, the greyed-out controls become available and the banner disappears.
- **Location check before elevation:** before starting the engine, the interface checks that `MAUS.exe` is in a folder that only administrators can modify (`Program Files`, `Program Files (x86)`, `WindowsApps`; `ScheduledAudit.IsInProtectedFolder`). If it is not (for example a copy in Downloads or on a USB stick), another program could have replaced `MAUS.exe`, and Windows would give administrator rights to it. MAUS then warns the user and asks whether to continue (default: No); on No, no UAC prompt is shown and MAUS stays in limited mode. Installed through the Microsoft Store, the installer or winget, MAUS is always in a protected folder, so this warning never appears.
- **Following actions** reuse the running engine, with no new prompt.
- **15 minutes without any request:** the engine exits (`EngineServer.IdleTimeout`), but never while a request is in progress. For example, a running latency measurement or benchmark counts as a request. The interface is notified when the engine process exits (`Process.Exited`): the controls are greyed out again and the banner comes back. If the engine restarted since the last audit, it audits again before any plan or apply, so it never acts on stale data.
- **Security stop:** after 3 refused connections (another process, a wrong key, a replayed or modified request; §5), the engine stops by itself with a dedicated exit code; the interface goes back to limited mode and warns the user that a program tried to control the engine, and advises a scan with Windows Security. Nothing else is done (no action on the PC that a malicious program could abuse).
- **Interface closed or crashed:** the engine detects that the interface process has exited and stops. If a fix was being written at that moment, the journal keeps it as `Pending` and "Undo" handles it (§9). Closing MAUS during a write asks for confirmation first.
- **MAUS started with "Run as administrator" by the user:** there is no separate engine; the same operations run in-process (`LocalMausEngine`).
- **Weekly audit (only if the user enabled it):** the task `\MAUS\WeeklyAudit` (§2.3) starts `MAUS.exe --scheduled-audit` with highest rights and **without a UAC prompt**: the user gave that consent once, when creating the task, which itself requires the engine. That process applies the engine's process hardening (§6), only reads, and exits unless a red problem is found. In that case it shows a notification; if the user clicks "Open MAUS", MAUS is started again **as a normal process, without administrator rights** (through Explorer, like a double-click), and the elevated audit process exits. The window never runs with the task's rights; to fix something, the user chooses "All features" and the usual UAC prompt appears.

| Workflow | Elevation | Engine runs until |
|---|---|---|
| "All features" (banner or startup window), "Enable advanced sensors" | UAC at the click: these are the only controls that start the engine | Idle timeout, security stop or interface exit |
| Audit, re-run a module, view and apply fixes, undo, history (journal), protected choices ("wanted" findings, Game Bar profile, laptop power) | Greyed out in limited mode; available with all features | Same |
| Repair Windows files, software upgrades, PawnIO install/remove, driver actions, driver backups and restore | Greyed out in limited mode; with all features, the console then runs on its own | The console window stays open until the user closes it, even after the engine stops |
| Restore points (list, create), weekly audit (enable/disable) | Greyed out in limited mode | Idle timeout, security stop or interface exit |
| Weekly audit run (if enabled) | Highest rights from the task, no UAC prompt (consent given when the task was created); "Open MAUS" starts a new window without rights | Exit after the audit, or when the notification is closed or "Open MAUS" is clicked |
| Memory sheet, latency test, frame counter | Greyed out in limited mode | Same; frame captures stop with the engine |
| Game session recording | Never asks. In limited mode it records without frames per second (they need the engine) | — |
| Benchmark | Never asks. With all features, it is started by the engine so that the CPU temperature can be watched through PawnIO; in limited mode it runs without that reading (the graphics card safety stop stays active) | Benchmark exit |
| Process list, process termination, disk-space analysis, drivers list, inventory, software list | Never asks. They run with administrator rights only if the engine is already running | — |
| Battery report | Never asks, never with administrator rights | — |
| Refresh-rate trial, Explorer restart, opening web pages, folders and Settings pages, CPU/RAM/VRAM/disk tests, ping test | Never elevated: they run in the interface process (web pages and folders are opened through `explorer.exe`) | — |

---

## 8. Confirmation before changes, and declined UAC

**First start:** the user must accept a disclaimer (what MAUS does, the risks, and that the user decides); otherwise MAUS closes.

**Before every modifying action**, the interface shows a Yes/No dialog whose default button is **No**. It describes what will change and how to undo it:
- **Apply fixes:**
  - the dialog shows the list of chosen changes, their warnings, whether a restore point will be created, and a reminder that the user decides;
  - fixes are chosen one by one in a checklist: "recommended" ones are pre-checked, "advanced" ones never are, and "warning" ones show a reinforced warning;
  - the restore-point checkbox is pre-checked.
- **Undo** a session or one fix.
- **Repair Windows files** (DISM/SFC).
- **Restart Explorer.**
- **Create a restore point.**
- **Enable the weekly audit.** Disabling it removes only MAUS's own task, without an extra dialog.
- **Terminate a process:** the dialog warns that unsaved work is lost.
- **Install or remove PawnIO.**
- **Each driver action** (restart, remove and re-detect, back up, back up then delete) and each driver restore. Before a delete, a verified restore point is created first; if it fails, the user is asked whether to continue.
- **Software upgrades:** an explicit checklist of packages, then a console where winget itself asks the user to accept licence terms.
- **Refresh-rate trial:** confirmation first, then a "Keep?" window that rolls back automatically after 15 seconds.
- Marking a finding as "wanted" and choosing a Game Bar profile on a Ryzen X3D also ask for confirmation.

Admin actions that only **read** (audit, sensors, memory sheet, latency, frame counter, journal) have no MAUS dialog: they are only available after the user explicitly clicked "All features" and accepted the UAC prompt, which is the consent.

**UAC declined:** Windows returns error 1223 (`ERROR_CANCELLED`). MAUS throws `EngineDeclinedException` and shows: "Autorisation administrateur refusée : rien n'a été lancé. MAUS reste en mode limité, sans droits administrateur." (Administrator permission refused: nothing was started. MAUS stays in limited mode, without administrator rights.)
- Nothing is started and nothing is written.
- MAUS stays in limited mode: the controls that need administrator rights stay greyed out, and the "All features" button can be clicked again later.
- There is no retry loop, and no fallback that performs the action without elevation.

---

## 9. Restore point, partial failure, cancellation, logging and undo

Implemented in `src/Maus.Core/Fixes/FixEngine.cs`, `RestorePoints.cs`, `JournalStore.cs` and `AtomicFile.cs`.

1. **Blocked cases.** No change is made in these cases:
   - the PC is managed (domain or MDM enrolment);
   - the engine is not elevated;
   - the journal folder cannot be secured (`JournalUnsafeException`).
2. **Restore point first.** When the restore-point option is on (pre-checked), a restore point is created before any write and then **verified** by re-reading the list of restore points.
   - If system protection is off, it is enabled only if the user ticked that option; otherwise the point fails.
   - If the point fails, the apply stops with "Aucune modification n'a été faite" (no change was made). The user is then asked whether to continue without a restore point (explicit `ProceedWithoutRestorePoint`; default: no).
   - If the user unchecks the option, the confirmation dialog says so beforehand, and the journal records "restore point not requested by the user".
3. **Journal before write.** For each value of a fix, the original value is read just before writing, together with whether its key existed. It is saved to the protected journal (state `Pending`) **before** anything is written.
   - If the original value cannot be read, that fix is skipped and nothing is written.
   - A value already changed by another fix of the same session is skipped.
   - A value already at the target is not written.
4. **Write, then read back.** Each write is read back. If any write of a fix fails, or reads back differently, **the whole fix is rolled back** in reverse order (all-or-nothing per fix), and each restored value is re-read. The result names the failed fix and the reason. The other fixes chosen in the same session are reported separately.
5. **Rollback failure.** If a rollback itself fails, the entry is marked `RollbackFailed` and shown to the user, and "Undo" stays available to retry.
6. **Journal failure during a session.** If the journal can no longer be written safely, no further write is made, and the writes already made in the session are rolled back.
7. **Interruption.** If MAUS is closed or crashes during a write, the next "Undo" handles the `Pending` entries:
   - if the original value is still in place, nothing had been written;
   - if the new value is in place, it is restored like an applied fix.
8. **Effect check.** After the apply, the interface runs a fresh audit and tells the user which fixes were written but have no visible effect (for example a policy ignored on the Home edition). This note is stored in the journal (`verify`). If that audit fails, the fixes are shown as "not verified", never as "no effect".
9. **Undo.** Undo works by session or by single change (History page, or CLI `--revert`), and each restored value is re-read. If a value was changed by something else since MAUS wrote it, MAUS **leaves it as it is** and reports "changed since". The interface never overwrites such a value; only the command-line tool, started by an administrator, offers `--force`.
10. **Logging.** The journal is one JSON file per session in `%ProgramData%\MAUS\journal` (SYSTEM + Administrators), written atomically. A session file whose owner is not SYSTEM or Administrators, or that is a link, is ignored and never replayed. Crash logs are written to `%LOCALAPPDATA%\MAUS\logs`. Nothing is sent anywhere.

---

## 10. Separation of read-only audit and privileged modification

| Layer | Rights | What it can do |
|---|---|---|
| Audit modules (`IAuditModule.Detect`, `IFixableModule.Plan`) | Read-only by contract | They receive only `AuditContext`: registry **reader**, WMI/CIM reader, event-log reader, package and file readers, and `ReadOnlyCommandRunner` (§2.4). They have no access to any writer. Unit tests run every module with fakes, and an apply/undo round-trip test checks each fixable module |
| `FixEngine` (`Apply`, `RecordVerification`, `Revert`) | The only writer of settings | It receives `FixContext` (writers + journal), which is separate from `AuditContext`, and writes only `PlannedChange` objects produced by the engine's own `Plan` |
| Engine Workshop operations (§2.2, §2.3) | Engine | Fixed executables, validated inputs, and a confirmation in the interface |
| Interface | Standard user | Displays results and sends operation names and ids. It never accesses the protected journal, the protected preferences or the privileged tools directly; everything goes through `EngineHost.Current` |

The audit can be run as many times as the user wants without changing anything. Nothing is ever applied automatically, including by the weekly scheduled audit, which only reads and notifies.

---

*Contact: https://sites.google.com/view/maus-help/accueil · Source code: https://github.com/TERMINATORmdel101/MAUS (GPL-3.0). This application and this document were written with the help of an AI (Claude, Anthropic), as stated in the application's "About" window.*
