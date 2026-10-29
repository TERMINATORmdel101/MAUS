# MAUS — Security design of the elevated engine (Microsoft Store review)

This document answers the ten points requested by Microsoft Store certification for the restricted capability `allowElevation` (policy 10.6.3). It describes MAUS **0.8.2** (source: https://github.com/TERMINATORmdel101/MAUS, branch `main`). Every statement below is taken from the source code; file paths are given so a reviewer can check them. Where a protection does **not** exist, this document says so.

MAUS is a free, open-source (GPL-3.0) Windows 11 maintenance utility. It has no telemetry and no network service. The only network use is user-initiated (winget, web search opened in the browser, ping test).

---

## 0. Architecture in one paragraph

`MAUS.exe` (the user interface) runs **without administrator rights** (manifest `asInvoker`). Nothing is elevated at start. When the user clicks an action that needs administrator rights, the interface starts a second copy of the **same signed executable**, `MAUS.exe --engine <pipe-name> <ui-pid>`, through `ShellExecute` with the verb `runas`. Windows then shows its UAC prompt. The elevated copy (the "engine") accepts a **closed list of operations** over a named pipe, and only from the process that started it. It stops when the interface exits, or after 15 minutes without a request. The interface never sends a command line, a script, a registry path or an executable path: it sends an operation name and identifiers. The engine recomputes everything it does from its own audit and its own built-in catalogs.

Code: `src/Maus.App/EngineHost.cs` (launch), `src/Maus.Core/Engine/Broker/` (`OnDemandMausEngine`, `EngineServer`, `RemoteMausEngine`, `EngineProtocol`, `LocalMausEngine`).

---

## 1. Exact allowlist of elevated engine operations

The engine dispatcher (`EngineServer.cs`, `EngineServer.Workshop.cs`) recognises exactly the operation names below. Any other name returns the error "Opération inconnue" (unknown operation) and nothing is executed.

| # | Operation | Purpose | Modifies the system? |
|---|---|---|---|
| 1 | `ping` | Check the engine is alive | No |
| 2 | `audit` | Run the full read-only audit (20 modules) | No |
| 3 | `module` | Re-run one audit module (id `M01`…`M20`) | No |
| 4 | `plan` | Compute the list of possible fixes from the engine's own audit | No |
| 5 | `apply` | Apply fixes chosen by id from the engine's own plan | **Yes** (see §3) |
| 6 | `verify` | Re-audit and compare after apply | No |
| 7 | `revert` | Undo a session or one change from the journal | **Yes** (restores journaled values) |
| 8 | `journal` | List journal sessions | No |
| 9 | `session` | Read one journal session | No |
| 10 | `prefs.load` | Read protected preferences | No |
| 11 | `prefs.save` | Write protected preferences (`%ProgramData%\MAUS\settings\preferences.json`) | MAUS file only |
| 12 | `inventory` | Hardware inventory (CPUID, WMI, registry reads) | No |
| 13 | `drivers` | List drivers (WMI, cfgmgr32) | No |
| 14 | `drivers.backups` | List MAUS driver backups in `%ProgramData%\MAUS\pilotes` | No |
| 15 | `sensors` | Read sensors (PDH, D3DKMT, NVML, optional PawnIO read-only) | No |
| 16 | `processes` | List processes | No |
| 17 | `process.terminate` | Terminate one user-selected process | **Yes** (one process) |
| 18 | `memory` | Read memory module SPD and controller timings (read-only, PawnIO) | No |
| 19 | `latency` | DPC/ISR latency measurement (ETW kernel session, ≤ 5 min) | ETW session only |
| 20 | `frames.start` / `frames.read` / `frames.stop` | Frame-rate counter (bundled PresentMon) | ETW session only |
| 21 | `space` | Disk space analysis (read-only) | No |
| 22 | `console` | Open a **visible** console for one of 5 fixed tasks (see §2) | **Yes** |
| 23 | `restorepoint.list` | List restore points | No |
| 24 | `restorepoint.create` | Create a restore point | **Yes** (restore point) |
| 25 | `restorepoint.driver` | Restore point before a driver action | **Yes** (restore point) |
| 26 | `schedule.get` | Read the MAUS weekly-audit task | No |
| 27 | `schedule.set` | Create or remove the MAUS weekly-audit task | **Yes** (one task `\MAUS\WeeklyAudit`) |
| 28 | `software` | List winget upgrades (`winget upgrade`, read-only) | No |
| 29 | `battery.report` | `powercfg /batteryreport` into a MAUS folder | MAUS file only |
| 30 | `benchmark` | Start the MAUS benchmark (same executable) | No |

Operations that **do not need** administrator rights (`inventory`, `drivers`, `sensors`, `processes`, `process.terminate`, `space`, `schedule.get`, `software`, `battery.report`, `benchmark`) run inside the non-elevated interface when the engine is not running. They go through the engine only if the engine is already running, so that its readings are complete. They never start the engine or show UAC (`OnDemandMausEngine.cs`, `Available` set).

Operations that start the engine (and therefore UAC) are the `Elevated` set in `OnDemandMausEngine.cs`: audit, module, plan, apply, verify, revert, journal, session, protected preferences, driver backups, memory, latency, frames.start, console, restore points, schedule.set.

---

## 2. Commands, APIs, executables, arguments and targets per operation

No operation accepts a command line, a path to an executable, a script or a free-form argument from the interface. Executables are fixed in code and resolved from protected folders.

### 2.1 `apply` / `revert` (fixes)

Writes are done only by `FixEngine` (`src/Maus.Core/Fixes/FixEngine.cs`) through `SettingsAccessor.Write` (`src/Maus.Core/Fixes/SettingsAccessor.cs`). There are exactly five kinds of write (`SettingKind`):

| Kind | API | Target |
|---|---|---|
| `Registry` | `RegistryKey.SetValue` / `DeleteValue` (.NET, Win32 registry API) | Fixed values listed in §3.1 |
| `SystemParameter` | `SystemParametersInfoW` with `SPIF_UPDATEINIFILE \| SPIF_SENDCHANGE` | Visual-effect flags of the current user (§3.1, M06) |
| `MinimizeAnimation` | `SystemParametersInfoW(SPI_SETANIMATION)` | Minimize/maximize animation of the current user |
| `ActivePowerScheme` | `PowerSetActiveScheme` (powrprof.dll, documented API) | Active power plan; GUID must be one listed by Windows on this PC |
| `ScheduledTask` | `%SystemRoot%\System32\schtasks.exe /Change /TN <task> /ENABLE` or `/DISABLE` | Third-party logon tasks found by the M12 audit; tasks under `\Microsoft\` are never targeted |

After registry/SPI changes the engine broadcasts `WM_SETTINGCHANGE` (`SendMessageTimeoutW`, `SMTO_ABORTIFHUNG`, 2 s) so Windows picks them up.

### 2.2 `console` (five fixed tasks, visible console window)

All executables are taken from `%SystemRoot%\System32` or, for winget, from `%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*` only (an alias in the user profile is refused, because a non-admin could replace it).

| Task (`ConsoleKind`) | Exact command | Input accepted |
|---|---|---|
| `Repair` | `cmd.exe /k` running `DISM.exe /Online /Cleanup-Image /RestoreHealth` then `sfc.exe /scannow` | None |
| `WingetUpgrade` | `winget.exe upgrade --id <ID> --exact --source winget` (interactive: the user accepts licences in the console) | Package ids; each must be in the list the **engine itself** obtained from `winget upgrade` (`software` op), and contain only ASCII letters, digits and `.-_+` |
| `PawnIoInstall` | `winget.exe install --id namazso.PawnIO --exact --source winget` | None (id fixed in code) |
| `PawnIoUninstall` | `winget.exe uninstall --id namazso.PawnIO --exact --source winget` | None |
| `DriverAction` | `pnputil.exe /restart-device <InstanceId>` · `pnputil.exe /remove-device <InstanceId>` then `/scan-devices` · `pnputil.exe /export-driver oem<N>.inf <backup folder>` · `pnputil.exe /delete-driver oem<N>.inf /uninstall` (only after a backup) | Device instance id: must be a **display adapter** in the driver list the engine read itself, and match `^[A-Za-z0-9_\\&.\-#{}]+$`; driver package must match `oem<number>.inf` |
| `DriverRestore` | `pnputil.exe /add-driver <backup folder>\*.inf /subdirs /install` | Backup folder **name** only (≤ 200 chars, no path separators, not `.`/`..`), resolved inside `%ProgramData%\MAUS\pilotes`, must exist and contain `*.inf` |

### 2.3 Other modifying operations

| Operation | API / command | Target / limits |
|---|---|---|
| `process.terminate` | `Process.Kill` | Engine re-reads the process by PID **and** start time (refuses if the PID was reused); refuses MAUS itself, PID 0 and 4, and critical processes (`ProcessRules.CanTerminate` in `src/Maus.Core/Workshop/Processes.cs`: System, Registry, Secure System, smss, csrss, wininit, winlogon, services, lsass, lsaiso, svchost, fontdrvhost, dwm, MsMpEng, NisSrv, SecurityHealthService) |
| `restorepoint.create`, `restorepoint.driver` | WMI `root\default:SystemRestore.CreateRestorePoint`; before the call, HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` is set to `0` and restored to its previous value afterwards | System protection is **never** enabled by these operations (`enableProtectionIfNeeded: false`) |
| `schedule.set` | `schtasks.exe /Create /TN MAUS\WeeklyAudit /XML <file> /F` and `schtasks.exe /Delete /TN MAUS\WeeklyAudit /F` | XML generated by MAUS (`ScheduledAudit.TaskXml`), written in `%ProgramData%\MAUS\tasks` (ACL SYSTEM + Administrators). Action = `MAUS.exe --scheduled-audit` (read-only audit, notification only). Refused unless MAUS is installed in a folder that standard users cannot modify |
| `latency` | ETW `NT Kernel Logger` (DPC/ISR events) | Duration 0 < d ≤ 5 min |
| `frames.start` | Bundled `third-party\PresentMon\PresentMon.exe` (version 2.6.0), SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af` verified before each start; arguments fixed: `--output_stdout --v1_metrics --no_console_stats --stop_existing_session --session_name <MAUS_Compteur\|MAUS_Releve> --exclude dwm.exe --exclude MAUS.exe` | Session name must be one of the two constants |
| `battery.report` | `powercfg.exe /batteryreport /output <path>` | Path fixed by MAUS (MAUS folder), not taken from the interface |
| `benchmark` | `MAUS.exe --benchmark --api … --resolution … --only … --lang …` (same executable, `ArgumentList`, no shell) | Each value from a closed set: api {d3d11, d3d12}, resolution {720p, 1080p, 1440p, 4k}, scope {all, gpu, cpu}, language {fr, en, es} |
| `prefs.save` | Atomic file write | `%ProgramData%\MAUS\settings\preferences.json` only |

### 2.4 Read-only commands used by the audit

The audit runs external tools only through `ReadOnlyCommandRunner` (`src/Maus.Core/Platform/ReadOnlyCommandRunner.cs`), which checks executable **and** arguments against an allowlist, resolves the executable from System32, and refuses anything else:

`powercfg /getactivescheme | /list | /a | /availablesleepstates | /query` · `bcdedit /enum` · `fsutil behavior query …` · `reagentc /info` · `dsregcmd /status` · `manage-bde -status` · `netsh winhttp show …` · `nvidia-smi --query-gpu | -q` · `winget upgrade` (fixed list of flags, WindowsApps copy only) · `dism /English /Online /Cleanup-Image /CheckHealth` (exact sequence).

---

## 3. Complete scope of system changes

### 3.1 Registry values that a fix can write or delete

Fixes are built only from code and embedded catalogs (`src/Maus.Core/Catalog/*.json`). The complete set is below. "Delete" means the fix removes a policy value so that Windows returns to its default; MAUS never writes the "enabled" value of a policy to restore it.

**HKLM — security defaults restored (M01, M13)**
- `SOFTWARE\Policies\Microsoft\Windows Defender\DisableAntiSpyware` (delete)
- `SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring` (delete)
- `SOFTWARE\Policies\Microsoft\WindowsFirewall\{DomainProfile,PrivateProfile,StandardProfile,PublicProfile}\EnableFirewall` (delete)
- `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\EnableLUA` (=1), `ConsentPromptBehaviorAdmin` (=5), `PromptOnSecureDesktop` (=1)
- `SOFTWARE\Policies\Microsoft\Windows\System\EnableSmartScreen`, `ShellSmartScreenLevel` (delete)
- `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\WUServer`, `WUStatusServer`, `DoNotConnectToWindowsUpdateInternetLocations`, `TargetReleaseVersion`, `TargetReleaseVersionInfo`, `ProductVersion`, `AU\UseWUServer`, `AU\NoAutoUpdate` (delete)
- `SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` — pause values (delete)
- `SYSTEM\CurrentControlSet\Services\{wuauserv,UsoSvc,WaaSMedicSvc,BITS}\Start` (Windows default start type)
- `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (=`explorer.exe`), `Userinit` (=`%windir%\system32\userinit.exe,`)
- `SOFTWARE\[WOW6432Node\]Microsoft\Windows NT\CurrentVersion\Windows\AppInit_DLLs` (empty), `LoadAppInit_DLLs` (=0)
- `SOFTWARE\[WOW6432Node\]Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>\Debugger` (delete, only for values found by the audit)
- `SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\FeatureSettingsOverride`, `FeatureSettingsOverrideMask` (delete: **re-enables** CPU mitigations; MAUS never disables them)
- `SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable` (=1)

**HKLM — privacy and comfort (M04, M05, M06, M07, M09)**
- `SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry`, `DoNotShowFeedbackNotifications`, `LimitDiagnosticLogCollection`, `LimitDumpCollection`
- `SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy`
- `SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode` (=0)
- `SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB` (=0)
- `SOFTWARE\Policies\Microsoft\Windows\System\EnableActivityFeed`, `PublishUserActivities`, `UploadUserActivities`
- `SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis` (=1, Pro and higher only, with a reinforced warning)
- `SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled` (=0, fast startup)
- `SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests` (=0 or delete)
- `SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR` (=0 or delete)
- `SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` (=2)
- `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\ExcludeWUDriversInQualityUpdate` (=1, optional, never in a profile)
- `SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching\SearchOrderConfig` (=0, optional)
- `SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\<Run|Run32|StartupFolder>\<name>` (M12, same mechanism as Task Manager)

Group Policy values are written only on Pro editions and higher.

**HKCU of the signed-in user (M04, M06, M07, M12)**
- `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowTaskViewButton`, `IconsOnly`, `ListviewAlphaSelect`, `TaskbarAnimations`, `ListviewShadow`, `Start_IrisRecommendations`, `Start_TrackProgs`
- `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency`
- `Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\VisualFXSetting`, and the per-effect values whose path is read from HKLM `…\Explorer\VisualEffects` ("Windows defaults" button)
- `Software\Microsoft\Windows\DWM\EnableAeroPeek`, `AlwaysHibernateThumbnails`
- `Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo\Enabled`
- `Software\Policies\Microsoft\Windows\CloudContent\DisableTailoredExperiencesWithDiagnosticData`
- `Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\*` (suggestion values listed in `src/Maus.Core/Modules/M04Privacy/`)
- `Software\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions`
- `Software\Microsoft\Windows\CurrentVersion\Privacy\TailoredExperiencesWithDiagnosticDataEnabled`
- `Software\Microsoft\Windows\CurrentVersion\SearchSettings\IsDynamicSearchBoxEnabled`
- `Software\Microsoft\Siuf\Rules\NumberOfSIUFInPeriod`
- `Control Panel\International\User Profile\HttpAcceptLanguageOptOut`
- `Software\Microsoft\InputPersonalization\RestrictImplicitInkCollection`, `RestrictImplicitTextCollection`
- `Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy\HasAccepted`
- `Software\Microsoft\GameBar\AutoGameModeEnabled`, `UseNexusForGameBarEnabled`; `System\GameConfigStore\GameDVR_Enabled`; `Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled`, `HistoricalCaptureEnabled`
- `Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (delete, only if the audit found a replaced shell)
- `Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\…` (M12)

If the engine was elevated with a **different** administrator account than the signed-in user, every HKCU and per-user SPI change is skipped and the user is told why (`SessionUser`), so the wrong profile is never modified.

### 3.2 Files and folders written

| Path | Content | Protection |
|---|---|---|
| `%ProgramData%\MAUS\journal\` | Journal of original values (JSON) | ACL SYSTEM + Administrators only; atomic writes |
| `%ProgramData%\MAUS\settings\preferences.json` | Protected choices | Same ACL |
| `%ProgramData%\MAUS\tasks\` | XML of the weekly task | Same ACL |
| `%ProgramData%\MAUS\pilotes\<folder>` | Driver backups (`pnputil /export-driver`) | Same ACL |
| `%LOCALAPPDATA%\MAUS\` | Interface settings, history, logs, battery report (written by the non-elevated interface) | User profile |
| `%TEMP%` | Temporary test files (disk speed test, benchmark thumbnails), deleted after use | User profile |

### 3.3 Services, tasks, drivers, configuration

- **Services:** no service is started, stopped, created or deleted. Only the `Start` value of `wuauserv`, `UsoSvc`, `WaaSMedicSvc` and `BITS` can be restored to the Windows default (§3.1).
- **Scheduled tasks:** `\MAUS\WeeklyAudit` (create/delete), and enable/disable of third-party logon tasks found by M12. Tasks under `\Microsoft\` are never changed.
- **Drivers:** only the display adapter, only through `pnputil` (§2.2), each action confirmed by the user, with a restore point offered first. No kernel driver is shipped by MAUS. PawnIO (a signed third-party driver) is installed or removed only through winget at the user's request.
- **Power:** active power plan (documented API) and fast startup.
- **Not touched at all:** BIOS/UEFI, BCD, boot configuration, Defender exclusions, firewall rules, hosts file, proxy, page file, user accounts, credentials.

---

## 4. Input types, permitted values and validation per operation

General rules (`EngineServer.cs`):
- Message = 4-byte little-endian length + UTF-8 JSON, maximum **32 MiB** (`EngineProtocol.MaxMessageBytes`); larger messages are refused before parsing.
- One request per connection.
- Identifiers (`Checked`): 1–200 characters, no control characters, no `\`, `/` or `:`; at most **500** identifiers per request.
- Unknown fields are ignored; missing or wrongly typed fields → error, nothing executed.

| Operation | Inputs | Validation |
|---|---|---|
| `audit`, `plan`, `verify`, `journal`, `inventory`, `drivers`, `drivers.backups`, `sensors`, `processes`, `software`, `restorepoint.list`, `schedule.get`, `memory` | none (language code only) | Language ∈ {fr, en, es} |
| `module` | module id | Must be an existing module id (`M01`…`M20`) |
| `apply` | list of fix ids + `ProceedWithoutRestorePoint` (bool) | The engine **recomputes the plan** from its own audit (`FixEngine.Plan`) and keeps only ids present in it. An id that is not in the engine's plan is ignored. The interface cannot supply a registry path, a value or a task name |
| `revert` | session id, optional change id, `force` (bool) | Must exist in the protected journal; values written back are the ones the engine journaled itself |
| `session` | session id | Must exist in the journal |
| `prefs.save` | preferences object | Deserialized into a fixed record type; unknown fields dropped |
| `process.terminate` | PID (int) + start time | Re-read by the engine; refused if start time differs, if MAUS, or if critical (§2.3) |
| `latency` | duration | 0 < duration ≤ 5 minutes |
| `frames.start` | session name | ∈ {`MAUS_Compteur`, `MAUS_Releve`} |
| `space` | folder path | Fully qualified local path; UNC (`\\`) and device paths refused; must exist. Read-only enumeration |
| `console` | kind + ids | Kind ∈ the five values of §2.2; ids validated as described there |
| `restorepoint.create`, `restorepoint.driver` | description | Fixed text chosen by MAUS |
| `schedule.set` | enabled (bool) | Bool only |
| `battery.report` | none | Output path fixed by MAUS |
| `benchmark` | api, resolution, scope, language | Each from the closed sets of §2.3 |

---

## 5. Named-pipe authentication, authorization, process verification and anti-replay

| Control | Implementation |
|---|---|
| Unpredictable name | `MAUS-<random GUID>`, generated by the interface for each engine start |
| Pipe squatting | Created with `PipeOptions.FirstPipeInstance`: if another process already owns the name, the engine fails to start |
| Access control list | Explicit `PipeSecurity`: **Deny** `NETWORK` (S-1-5-2) full control; **Allow** only the interactive user's SID (`ReadWrite \| CreateNewInstance`). No other principal has access |
| Client verification (engine side) | For every connection, `GetNamedPipeClientProcessId` must equal the PID of the interface that started the engine (passed on the command line). Any other process is disconnected without an answer |
| Server verification (interface side) | `GetNamedPipeServerProcessId` must equal the PID of the engine process the interface started itself (`Process.Start` result) |
| Message bounds | 32 MiB maximum; one request per connection; JSON parsed into fixed types |
| Lifetime binding | The engine watches the interface process and exits when it exits |

**Anti-replay:** MAUS does **not** use a nonce, sequence number or session secret. Replay protection relies on the controls above: a request is accepted only on a new connection whose client process is the interface that started this engine, on a pipe that only that user can open locally. A replayed `apply` could only re-apply fixes that the engine itself recomputed from its own audit and catalog (already-compliant settings produce no change). A replayed `revert` only restores values the engine journaled. If the Store team requires it, a per-start random secret passed on the engine command line and checked on every request can be added in the next version.

---

## 6. No arbitrary commands, scripts, paths, arguments or executables

- The protocol has no field for a command, script, executable path, registry path, value, or argument list. The interface sends an operation name from §1 and identifiers.
- Every executable the engine starts is fixed in code: `cmd.exe`, `DISM.exe`, `sfc.exe`, `pnputil.exe`, `schtasks.exe`, `powercfg.exe` from `%SystemRoot%\System32`; `winget.exe` only from `%ProgramFiles%\WindowsApps`; bundled PresentMon only after a SHA-256 check; and `MAUS.exe` itself for the benchmark.
- Every argument is either a constant or a value validated as in §4 (package ids from the engine's own list, device ids from the engine's own driver list, backup folder names resolved inside a protected folder).
- No PowerShell, no script host, no `cmd /c` with user text. In the `Repair` console the only dynamic text is a localized message, escaped before being echoed.
- Processes are started with `ArgumentList` (each argument passed separately) or fixed strings, never by concatenating user input into a shell command.
- Registry writes come only from the plan computed by the engine (§3.1); there is no "write this value" operation.

---

## 7. Elevation timing and engine termination per workflow

| Moment | Behaviour |
|---|---|
| App start | No UAC prompt. The interface runs as standard user. On first start (and until the user turns it off in Settings > Administrator rights), MAUS shows an in-app question "Enable all features?". UAC appears **only if** the user clicks Yes; "No" keeps MAUS in standard mode |
| First admin action (audit, fix, repair, restore point, driver action, advanced sensors, memory sheet, latency, frame counter, weekly task) | The interface starts `MAUS.exe --engine` with `runas` → UAC prompt at that moment |
| Following actions | Reuse the running engine, no new prompt |
| 15 minutes without any request (none in progress) | Engine exits (`EngineServer.IdleTimeout`); the next admin action asks UAC again. If the engine restarted, its audit is re-run before any plan or apply, so it never acts on stale data |
| Interface closed or crashed | Engine detects the interface process exit and stops |
| MAUS started "Run as administrator" by the user | No separate engine: the same operations run in-process (`LocalMausEngine`) |
| Actions that never need admin | Display refresh-rate trial (`ChangeDisplaySettingsExW`, current session only, 15-second automatic rollback), Explorer restart, web search, ping test, CPU/RAM/VRAM/disk tests, benchmark start: run in the non-elevated interface |

---

## 8. Confirmation before changes, and declined UAC

Before every modifying action the interface shows a Yes/No dialog (default button **No**) describing what will change and how to undo it:
- Apply fixes: list of chosen changes, risk notes, restore-point notice, reminder that the user decides.
- Repair Windows files (DISM/SFC), restart Explorer, create restore point, enable/disable weekly audit, terminate a process, install/remove PawnIO, each driver action (restart, remove, back up, delete after backup, reinstall a backup), software upgrades (checkbox list, then console where winget itself asks to accept licences), refresh-rate trial.
- Fixes marked `Advanced` are never pre-checked; `Warning` fixes show a reinforced warning.

**UAC declined:** Windows returns error 1223. MAUS throws `EngineDeclinedException`, shows "Administrator permission refused: this action is cancelled. MAUS remains usable without rights (read-only).", and **only that action** is cancelled. Nothing is written, MAUS keeps running in standard mode, and the next admin action asks again. There is no retry loop and no fallback that performs the action without elevation.

---

## 9. Restore point failure, partial failure, cancellation, logging and undo

Implemented in `src/Maus.Core/Fixes/FixEngine.cs`, `RestorePoints.cs`, `JournalStore.cs`.

1. **Blocked cases** — no change is possible if the PC is managed (domain or MDM enrolment), or the engine is not elevated.
2. **Restore point first** — before applying, a restore point is created and then **verified** by reading the list of restore points. If creation fails, the apply stops with "No change was made", and the user is asked whether to continue without a restore point (explicit `ProceedWithoutRestorePoint`; default: no).
3. **Journal before write** — for each setting, the original value (or its absence) is saved to the protected journal with state `Pending` **before** writing.
4. **Write, then read back** — each write is read back. If any write of a fix fails or reads back differently, **the whole fix is rolled back** from the journal (all-or-nothing per fix), and the result says which fix failed and why. Other fixes chosen in the same session are reported separately.
5. **Rollback failure** — if a rollback itself fails, the entry is marked `RollbackFailed`, shown to the user, and "Undo" remains available to retry.
6. **Cancellation / crash** — if MAUS is closed or crashes during a write, the `Pending` journal entries are handled by the next "Undo", which restores the saved original values. Closing during a write asks for confirmation first.
7. **Effect check** — after apply, a fresh audit checks whether each fix took effect; a fix that was written but has no effect is reported as such.
8. **Undo** — by session or by single change (History page, CLI `--revert`). If a value was changed by something else since MAUS wrote it, MAUS shows the conflict and asks before overwriting (`force`).
9. **Logging** — journal JSON in `%ProgramData%\MAUS\journal` (SYSTEM + Administrators, atomic writes). Crash logs in `%LOCALAPPDATA%\MAUS\logs`. Nothing is sent anywhere.

---

## 10. Separation of read-only audit and privileged modification

| Layer | Rights | What it can do |
|---|---|---|
| Audit modules (`IAuditModule.Detect`, `IFixableModule.Plan`) | Read-only by contract | Receive only `AuditContext`: read-only registry reader, WMI reader, event-log reader, `ReadOnlyCommandRunner` (§2.4). They have no access to any writer. Unit tests use fakes that fail on any write |
| `FixEngine` (`Apply`, `Verify`, `Revert`) | The only writer of settings | Receives `FixContext` (writers + journal), separate from `AuditContext`; writes only `PlannedChange` objects produced by the engine's own `Plan` |
| Workshop actions (§2.2, §2.3) | Engine only | Fixed executables, validated inputs, confirmation in the interface |
| Interface | Standard user | Displays results, sends operation names and ids. It does not access the registry, WMI, journal or tools directly (all through `EngineHost.Current`) |

The audit can be run as many times as wanted without changing anything. Nothing is ever applied automatically, including by the weekly scheduled audit, which only reads and notifies.

---

*Contact: https://sites.google.com/view/maus-help/accueil — source code: https://github.com/TERMINATORmdel101/MAUS (GPL-3.0). This application and this document were written with the help of an AI (Claude, Anthropic), as stated in the app's "About" window.*
