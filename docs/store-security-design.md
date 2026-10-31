# MAUS — Security design of the elevated engine (Microsoft Store review)

This document answers the ten points requested by Microsoft Store certification for the restricted capability `allowElevation` (policy 10.6.3). It describes MAUS **0.8.5**, the version submitted with this document (source: https://github.com/TERMINATORmdel101/MAUS). Every statement below was checked against the source code, and file paths are given so a reviewer can verify them. Where a protection does **not** exist, this document says so. Version 0.8.5 follows a complete review of the code against this document (30 October 2026): the engine was changed where the document promised more than the code did (battery report never served by the engine, forced undo ignored from the channel, every driver action confirmed by the engine, bounded free texts, bounded `schtasks` wait), and the document was corrected where the code is the intended behaviour.

MAUS is a free, open-source (GPL-3.0) Windows 11 maintenance utility. It has no telemetry and no network service. The audit itself uses the network in two read-only ways: the Windows Update module asks the Windows Update Agent for pending updates (M03), and the software module lists available upgrades with `winget upgrade` (M20); both also run during the optional weekly scheduled audit. Everything else that uses the network is started by the user: Windows repair (DISM can download from Windows Update), software upgrades and PawnIO through winget, links and web searches opened in the browser, the ping test. MAUS sends nothing about the PC to its author or to any service of its own; a web search that the user starts sends the searched name to the search engine the user chose. Some links that the user opens also carry a hardware model in the URL: tutorial searches on YouTube (CPU, GPU or PC model) and BIOS searches on the vendor's site (PC or motherboard model). Nothing is sent unless the user opens the link.

**Contents.** §0 Architecture · §1 Allowlist of operations · §2 Commands, APIs, executables, arguments and targets · §3 Complete scope of system changes · §4 Inputs and validation · §5 Pipe authentication, authorization, process verification and anti-replay · §6 No arbitrary commands, scripts, paths, arguments or executables · §7 Elevation timing and engine termination per workflow · §8 Confirmation before changes, and declined UAC · §9 Restore point, partial failure, cancellation, logging and undo · §10 Separation of read-only and privileged functions.

---

## 0. Architecture

`MAUS.exe` (the user interface) runs **without administrator rights** (manifest `asInvoker`), and nothing is elevated at start.

MAUS opens in **limited mode**: every control that needs administrator rights is greyed out. When the user explicitly clicks **"All features"**, the interface starts a second copy of the **same executable**, `MAUS.exe --engine <pipe-name> <ui-pid> <key-fingerprint>`. It uses `ShellExecute` with the verb `runas`, so Windows shows its UAC prompt.

The elevated copy (the "engine") behaves as follows:
- It first validates its own command line. The pipe name must match `^MAUS-[0-9a-f]{32}$`, the PID must be a decimal number and the key fingerprint must be 64 lowercase hexadecimal digits; otherwise it exits with code 2 without doing anything (`src/Maus.App/App.xaml.cs`, `RunEngineAsync`).
- It accepts a **closed list of operations** over a named pipe, only from the process named on its command line, and only requests signed with that process's secret key, each with a sequence number that can be used once (§5).
- It stops when the interface exits, or after 15 minutes without any request.

The interface never sends a command line, a script, a registry path, a registry value to write or an executable path. It sends an operation name, identifiers and options from closed sets, plus three bounded free texts that are never executed and never written to Windows settings: the restore-point description (passed to Windows as the name of the point), the verification notes stored in the MAUS journal, and the protected preferences object (§4). It can also send one folder path, for the disk-space analysis only (`space`, §4): the interface only sends the roots of fixed drives, the engine refuses only a path that starts with `\\` (forward-slash spellings are not filtered in this version), and the folder is only enumerated, read-only. The engine recomputes everything it does from its own audit, its own lists and its built-in catalogs.

Code:
- `src/Maus.App/EngineHost.cs`: launch.
- `src/Maus.Core/Engine/Broker/`: `OnDemandMausEngine`, `EngineServer`, `RemoteMausEngine`, `EngineProtocol`, `LocalMausEngine`.

---

## 1. Exact allowlist of engine operations

The engine dispatcher (`EngineServer.cs`, `EngineServer.Workshop.cs`) recognises exactly the 31 operation names below. Any other name returns the error "Opération inconnue" (unknown operation), and nothing is executed.

**Needs administrator rights** column. "Yes" means the operation needs administrator rights. In limited mode (engine not running), every control that leads to such an operation is **greyed out**, and the controls made to start the engine are the "All features" button of the banner at the top of the window, the startup window and the "Enable advanced sensors" button (§7 also lists three narrow exceptions, each after a user action); the operation itself then runs in the engine. "No" means the operation also works without rights: it goes through the engine only if the engine is already running (it then runs with administrator rights), otherwise it runs in the non-elevated interface. A "No" operation never starts the engine and never shows UAC (`OnDemandMausEngine.cs`, methods `Elevated` and `Available`).

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
| 12 | `inventory` | Hardware inventory (CPUID, WMI, registry reads; memory SPD read through PawnIO when installed and elevated) | No | No (limits of PawnIO reads: §3.3) |
| 13 | `drivers` | List drivers (WMI, cfgmgr32) | No | No |
| 14 | `drivers.backups` | List MAUS driver backups | Yes | MAUS folder permissions only (§3.2) |
| 15 | `sensors` | Read sensors (PDH, D3DKMT, NVML; PawnIO when installed and elevated, §3.3) | No | No (limits of PawnIO reads: §3.3) |
| 16 | `processes` | List processes | No | No |
| 17 | `process.terminate` | Terminate one process chosen by the user | No | **Yes**: one process |
| 18 | `memory` | Memory module SPD and memory-controller timings (PawnIO, to read data, within the limits stated in §3.3) | Yes | No |
| 19 | `latency` | DPC/ISR latency measurement (ETW kernel session, at most 5 minutes) | Yes | Temporary ETW session |
| 20 | `frames.start` | Start the frame-rate counter (bundled PresentMon) | Yes | Temporary ETW session |
| 21 | `frames.read` | Read the counter | Never (engine must already run) | No |
| 22 | `frames.stop` | Stop the counter | Never | Closes the ETW session |
| 23 | `space` | Disk-space analysis (read-only enumeration) | No | No |
| 24 | `console` | Open a **visible** console for one of 6 fixed tasks (§2.2) | Yes | **Yes** |
| 25 | `restorepoint.list` | List restore points and shadow-storage space | Yes | No |
| 26 | `restorepoint.create` | Create a restore point | Yes | **Yes**: restore point |
| 27 | `restorepoint.driver` | Restore point before a driver action | Yes | **Yes**: restore point |
| 28 | `schedule.get` | Read the MAUS weekly-audit task | No (the re-creation below only succeeds in the engine) | **Yes** in one case: after a Store update, it re-creates the MAUS task on the new folder (§2.3) |
| 29 | `schedule.set` | Create or remove the MAUS weekly-audit task | Yes | **Yes**: task `\MAUS\WeeklyAudit` only |
| 30 | `software` | List winget upgrades (`winget upgrade`, read-only) | No | No |
| 31 | `benchmark` | Start the MAUS benchmark (same executable) | No | No |

The battery report (`powercfg /batteryreport /output <fixed MAUS path>`) is not an engine operation since 0.8.5: the non-elevated interface runs it itself (§2.3), and the engine dispatcher does not recognise the name.


---

## 2. Commands, APIs, executables, arguments and targets

No operation accepts a command line, an executable path or a script from the interface, and no value from the interface is placed on a program's command line as free text; the free texts and the `space` folder of §0 are the only free-form inputs. Executables are fixed in code and resolved from protected folders; winget then runs the publishers' installers (§6). An automated test (`tests/Maus.Core.Tests/Security/SecurityGuardTests.cs`) makes the CI run fail in the cases below. The `Tests` workflow runs it on every push and pull request; the script that builds the Store package (`installer/msix/build-msix.ps1`) does not run tests itself.
- a program is started from any source file other than the 8 reviewed launchers;
- `UseShellExecute = true` is used anywhere other than the `runas` launch of the engine;
- `cmd.exe` is started anywhere other than `VisibleConsole`.

### 2.1 `apply` and `revert` (fixes)

Fixes are written only by `FixEngine` (`src/Maus.Core/Fixes/FixEngine.cs`) through `SettingsAccessor.Write` (`src/Maus.Core/Fixes/SettingsAccessor.cs`). The one other caller of that accessor is `RestorePointCreator`, for the restore-point frequency value (§2.3). There are exactly five kinds of write (`SettingKind`):

| Kind | API | Target |
|---|---|---|
| `Registry` | `RegistryKey.CreateSubKey`, `SetValue`, `DeleteValue`; on undo or rollback, `DeleteSubKey` (Win32 registry API through .NET) | Only the values of the plan that the engine computes from its own audit and catalogs: those listed in §3.1. The accessor itself has no key allowlist; the plan is the barrier, and the channel carries no registry path or value |
| `SystemParameter` | `SystemParametersInfoW` with `SPIF_UPDATEINIFILE \| SPIF_SENDCHANGE` | 10 visual-effect flags of the current user (§3.1, M06) |
| `MinimizeAnimation` | `SystemParametersInfoW(SPI_SETANIMATION)` | Minimize/maximize animation of the current user |
| `ActivePowerScheme` | `PowerSetActiveScheme` (powrprof.dll, documented API) | Active power plan: only the built-in Windows plans "High performance" or "Balanced" (fixed GUIDs), chosen by the engine from the audit (the accessor accepts any parsable GUID, but the GUID comes from the engine's plan, never from the channel); undo restores the previous plan's GUID read before the change |
| `ScheduledTask` | `%SystemRoot%\System32\schtasks.exe /Change /TN <path\name> /DISABLE` (undo: `/ENABLE`), arguments passed separately (`ArgumentList`) | Third-party logon tasks found by the M12 audit. Tasks under `\Microsoft\` and disabled tasks are never listed |

Besides these writes, `apply` can make the calls of §2.3 for a restore point (WMI `SystemRestore.CreateRestorePoint`, with the frequency value set to 0 around the call) when the option is on, and WMI `SystemRestore.Enable("C:\")` when protection is off and the user allowed it. `apply`, `revert` and `verify` also write the journal files of §3.2. `revert` creates no restore point.

After an apply or undo that changed something, the engine broadcasts `WM_SETTINGCHANGE` (`SendMessageTimeoutW`, `SMTO_ABORTIFHUNG`, 2 s) so that Windows picks up the change.

### 2.2 `console`: six fixed tasks in a visible console window

`VisibleConsole.Run` starts `%SystemRoot%\System32\cmd.exe` with `/k`, or `/s /k "…"`, and a command line that MAUS builds itself (`src/Maus.Core/Platform/VisibleConsole.cs`). The working directory is System32. The window stays open so the user can read every message; it then remains an open elevated `cmd.exe` prompt (§6).

The command line contains only:
- fixed text;
- tool paths fixed in code, or for winget located as below;
- identifiers validated as described below;
- display text from winget and Windows, escaped as below.

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

For restart, the delete step of Remove, and restore, the console interprets the exit code of `pnputil` (0 = success, 3010 = restart needed, 259 = nothing to update, for restore only, anything else = failure) and shows the result (`GraphicsDriverActions.Outcome`). Reinstall and Backup interpret no exit code: the user reads pnputil's own message, and after a Backup the final line names the backup folder even if the export failed.

### 2.3 Other operations that change something

| Operation | API / command | Target and limits |
|---|---|---|
| `process.terminate` | `Process.Kill(entireProcessTree: false)` | The engine re-reads the process list itself and finds the PID. If the interface gave a start time and it differs, nothing is done (reused PID). Just before killing, it compares the start time reported by Windows with its own reading. It refuses MAUS itself, the interface process, PID 0 and 4, and critical processes (`ProcessRules.CanTerminate`, `src/Maus.Core/Workshop/Processes.cs`: System, Idle, Registry, Memory Compression, Secure System, smss, csrss, wininit, winlogon, services, lsass, lsaiso, svchost, fontdrvhost, dwm, MsMpEng, NisSrv, SecurityHealthService) |
| `restorepoint.create`, `restorepoint.driver` | WMI `root\default:SystemRestore.CreateRestorePoint` (type `MODIFY_SETTINGS`, event `BEGIN_SYSTEM_CHANGE`). Just before the call, HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` is set to `0`, and its previous value is restored right after (Windows otherwise silently skips a point if another is less than 24 h old; §9, item 7, gives the exception when the engine is interrupted). The new point is then **verified** by re-reading the list of points | Description fixed by MAUS ("MAUS : point créé à la main", "MAUS : avant la suppression du pilote graphique"). These two operations **never** enable system protection |
| `schedule.set` | `%SystemRoot%\System32\schtasks.exe /Create /TN MAUS\WeeklyAudit /XML <file> /F`, or `/Delete /TN MAUS\WeeklyAudit /F` | Input is a day of the week, or none to delete. MAUS generates the XML (`ScheduledAudit.TaskXml`): weekly at noon, `LogonType InteractiveToken`, `RunLevel HighestAvailable`, action `MAUS.exe --scheduled-audit` (audit that changes no setting; a notification only if a red problem is found; nothing is applied). The XML file is written in `%ProgramData%\MAUS\tasks` (SYSTEM + Administrators only) and deleted after use. Creation is **refused** unless `MAUS.exe` is under `Program Files` or `Program Files (x86)` (WindowsApps included), because a task with highest rights must not start an executable that a standard user could replace |
| `schedule.get` | `schtasks.exe /Query /TN MAUS\WeeklyAudit /XML` | Read. Only exception: if the existing MAUS task points to an **older version folder of the same Store package** (the WindowsApps folder name changes at each update), the engine re-creates the same task pointing to the current `MAUS.exe`, with the same rules as `schedule.set`. This re-creation keeps the day of the existing task, names the account of the engine process as the task's user, checks the existing task only by its action path, and does not ask the user again |
| `battery.report` (interface only) | `%SystemRoot%\System32\powercfg.exe /batteryreport /output <path>` | Not an engine operation since 0.8.5. Path fixed by MAUS and enforced by the command allowlist: `%LOCALAPPDATA%\MAUS\rapports\rapport-batterie.html` (the previous report is deleted first). Run by the interface without administrator rights, even when the separate engine is running; it runs with those rights only when MAUS itself was started with "Run as administrator" (the whole interface is elevated then) |
| `latency` | ETW real-time session `NT Kernel Logger` (DPC/ISR events) | Duration: 0 < d ≤ 5 min; the session is stopped at the end |
| `frames.start` / `frames.stop` | Bundled `<MAUS folder>\PresentMon\PresentMon.exe` (Intel PresentMon 2.6.0). Its SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af` is checked before **each** start; a different file is never started. The file is opened with read-only sharing for the check and kept open until the process has started, so it cannot be replaced, renamed or deleted in between (`PresentMonCapture.OpenVerified`). Fixed arguments, passed separately: `--output_stdout --v1_metrics --no_console_stats --stop_existing_session --session_name <name> --exclude dwm.exe --exclude MAUS.exe`. Cleanup: `--terminate_existing_session --session_name <name>` | Session name must be `MAUS_Compteur` or `MAUS_Releve`. All captures are stopped when the engine stops normally (§9, item 11) |
| `benchmark` | The same `MAUS.exe` with `--benchmark --api <api> --resolution <res> --lang <lang>` [`--only <scope>`] [`--raytracing off`], arguments passed separately, no shell | Each value from a closed set: api {d3d11, d3d12}, resolution {720p, 1080p, 1440p, 4k}, scope {all, gpu, cpu}, language {fr, en, es}, ray tracing on/off. When the engine runs, the benchmark is started by it (so with administrator rights) so that the CPU temperature can be read through PawnIO for the safety stop. The benchmark process then applies the engine's process hardening (§6) and does not use its compiled-shader cache: shaders are compiled in memory |
| `prefs.save` | Atomic file write | `%ProgramData%\MAUS\settings\preferences.json` only |

### 2.4 Read-only commands used by the audit

The audit runs external tools only through `ReadOnlyCommandRunner` (`src/Maus.Core/Platform/ReadOnlyCommandRunner.cs`):
- the executable **and** its arguments are checked against an allowlist;
- a tool named without a folder is taken from `%SystemRoot%\System32` **only**: if it is not there, it is reported as missing, and the `PATH` is never searched;
- winget is accepted only from WindowsApps;
- anything else is refused before starting.

Allowed commands:
- `powercfg /getactivescheme | /list | /a | /availablesleepstates | /query` (the documented aliases `/l` and `/q` are accepted too)
- `bcdedit /enum`
- `fsutil behavior query …`
- `reagentc /info`
- `dsregcmd /status`
- `manage-bde -status`
- `netsh winhttp show …`
- `nvidia-smi --query-gpu … | -q … | --help`
- `winget upgrade --source winget --disable-interactivity` (fixed list of flags)
- `dism /English /Online /Cleanup-Image /CheckHealth` (exact sequence)

The runner also admits `powercfg /batteryreport /output <fixed MAUS path>`, which writes that one report; no audit module uses it, only the interface (§2.3).

The audit also reads the registry, WMI/CIM, event logs, the package list and files through read-only interfaces (§2.5, §10).

### 2.5 Read-only operations

APIs and targets of the operations that §2.1 to §2.3 do not cover. None of them accepts a command, and none starts a program other than those of §2.4.

| Operation | API / source | Target |
|---|---|---|
| `ping` | None | Returns the engine's version, process id and elevation state |
| `audit`, `module` | The readers of `AuditContext` (§10): `ReadOnlyCommandRunner` (§2.4), registry reads, WMI/CIM queries (`WmiCimReader`), `EventLogReader`, `PackageManager.FindPackagesForUser`, file reads, `SystemParametersInfoW` reads. Direct read calls in some modules: Windows Update Agent COM objects `Microsoft.Update.Session` and `Microsoft.Update.AutoUpdate` (M03); Task Scheduler COM object `Schedule.Service` (`Connect`, `GetFolder`, `GetTasks`); `WinVerifyTrust` with revocation checks off (M01); `DismCheckImageHealth` outside the Store package, `dism.exe` of §2.4 inside it (M02); `CallNtPowerInformation`, `PowerGetActiveScheme`, `PowerRegisterForEffectivePowerModeNotifications` (temporary, unregistered after the read), `GetLogicalProcessorInformationEx` (M05); `GetFirmwareEnvironmentVariableExW`, with `SeSystemEnvironmentPrivilege` enabled only on a thread token dropped after the read (M08); `D3DKMTQueryAdapterInfo` (M09); `DeviceIoControl` with `IOCTL_STORAGE_QUERY_PROPERTY` on `\\.\PhysicalDrive<n>` (M11); `QueryDisplayConfig` (M14); `wlanapi.dll` (M18) | Registry keys and values named in `src/Maus.Core/Catalog/*.json` and `src/Maus.Core/Modules/*`. WMI namespaces `root\cimv2`, `root\wmi`, `root\default`, `root\StandardCimv2`, `root\SecurityCenter2`, `root\Microsoft\Windows\{Defender, DeliveryOptimization, DeviceGuard, Storage, TaskScheduler}`, `root\cimv2\Security\{MicrosoftTpm, MicrosoftVolumeEncryption}`. Event logs `System` and `Microsoft-Windows-Diagnostics-Performance/Operational`. `module` runs one of the 20 modules on the engine's existing audit |
| `plan` | `FixEngine.Plan`: the `Plan` method of each fixable module, with the same readers | The engine's own audit results and its protected preferences. It returns a list of changes and writes nothing |
| `inventory` | `X86Base.CpuId`; WMI; NVML (`nvml.dll`, only from System32 or the NVIDIA `NVSMI` folder, if present); registry reads; SPD read through PawnIO when installed and elevated (§3.3) | WMI classes `Win32_Processor`, `Win32_BaseBoard`, `Win32_BIOS`, `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray`, `Win32_OperatingSystem`, `Win32_SoundDevice`, `Win32_Tpm`, `MSFT_PhysicalDisk`, `MSFT_StorageReliabilityCounter`, `MSFT_NetAdapter` and the battery classes of `root\wmi`; HKLM display-class key (video memory size) |
| `drivers` | WMI; `CM_Locate_DevNodeW`, `CM_Get_DevNode_PropertyW` (cfgmgr32) | `Win32_PnPSignedDriver`; driver date property of each device |
| `sensors` | PDH; `GlobalMemoryStatusEx`; `D3DKMTQueryAdapterInfo`; NVML; registry read of the PawnIO install state; built-in safety-limits catalog. When PawnIO is installed and the process is privileged: LibreHardwareMonitorLib (§3.3) | PDH counters `\Processor Information`, `\PhysicalDisk(_Total)`, `\Network Interface(*)`, `\Thermal Zone Information(*)`, `\GPU Engine(*)`. The library opens the processor and the motherboard only |
| `memory` | `X86Base.CpuId`; WMI; PawnIO modules (§3.3) | `Win32_PhysicalMemory`; memory SPD chips and memory-controller registers |
| `processes` | `Process.GetProcesses`; `OpenProcess` with `PROCESS_QUERY_LIMITED_INFORMATION`; `QueryFullProcessImageNameW`, `GetProcessTimes`, `GetProcessIoCounters`; PDH | All processes; PDH counter `\GPU Engine(*)\Utilization Percentage` |
| `drivers.backups` | `DirectoryInfo.EnumerateDirectories`, `EnumerateFiles` | `%ProgramData%\MAUS\pilotes`, if it exists, after re-applying that folder's protection (§3.2): this is the only change the operation makes. It returns folder names |
| `journal`, `session`, `prefs.load` | `Directory.GetFiles`, `AtomicFile.ReadText` with an owner check | The JSON files of §3.2 (`%ProgramData%\MAUS\journal\*.json`; `%ProgramData%\MAUS\settings\preferences.json`) |
| `frames.read` | In-memory data only | The log of the running PresentMon capture (§2.3) |
| `software` | `ReadOnlyCommandRunner` (§2.4), after locating winget through the package list | `winget upgrade --source winget --disable-interactivity` |
| `space` | `DirectoryInfo.EnumerateFileSystemInfos`, `FileInfo.Length` | The given folder and its subfolders (§4). Reparse points are skipped. It returns paths, names, sizes and dates |
| `restorepoint.list` | WMI `SELECT SequenceNumber, Description, CreationTime FROM SystemRestore` (`root\default`) and `SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage`; registry reads | HKLM `SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore\DisableSR` and `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients` (protection state of the system drive) |

`verify` is not read-only, but its only write is to the journal: it reads the session file, adds a display note to entries already applied, and rewrites the file atomically. `schedule.get`, `latency` and the `frames` start and stop operations are in §2.3.

---

## 3. Complete scope of system changes

### 3.1 Registry values that a fix can write or delete

Fixes are built only from code and embedded catalogs (`src/Maus.Core/Catalog/*.json`, `src/Maus.Core/Modules/*`), and only for a value that the audit found different from the target. "Delete" means the fix removes the value so that Windows returns to its own default. In M01 and M13, MAUS restores a security policy by deleting it; the only policy values written instead are the three UAC values of M01 (`EnableLUA` = 1, `ConsentPromptBehaviorAdmin` = 5, `PromptOnSecureDesktop` = 1). Non-policy values (service start types, Winlogon, AppInit, driver blocklist) are written to the Windows default shown below. M04, M06, M07 and M09 write the policy values listed under them, as their purpose. "(Pro+)" means the value is only written on Pro, Enterprise and Education editions. Values marked "optional" are never pre-checked.

Keys: a write creates the listed key, and any missing parent key, if it does not exist. On undo or rollback, a key that did not exist before the fix (recorded in the journal) is deleted if it then holds no value and no subkey; this applies only to that key, and its now-empty parents are left in place. MAUS's own code creates or deletes no other registry key.

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
| HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` | `Start_IrisRecommendations` = 0, `Start_TrackProgs` = 0 |
| HKCU `Software\Policies\Microsoft\Windows\Explorer` | `DisableSearchBoxSuggestions` = 1 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\SearchSettings` | `IsDynamicSearchBoxEnabled` = 0 |
| HKCU `Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager` | `RotatingLockScreenOverlayEnabled`, `SubscribedContent-338387Enabled`, `SubscribedContent-338389Enabled`, `SoftLandingEnabled`, `SilentInstalledAppsEnabled`, `SubscribedContent-338393Enabled`, `SubscribedContent-353694Enabled`, `SubscribedContent-353696Enabled` = 0 |
| HKCU `Control Panel\International\User Profile` | `HttpAcceptLanguageOptOut` = 1 |
| HKCU `Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy` | `HasAccepted` = 0 |
| HKCU `Software\Microsoft\InputPersonalization` | `RestrictImplicitTextCollection` = 1, `RestrictImplicitInkCollection` = 1 |

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
- HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency`, set to 0 during the call and then restored (§2.3; §9, item 7, gives the exception when the engine is interrupted).

If MAUS runs under a **different** account than the signed-in user (for example started with "Run as administrator" using another administrator account), every HKCU and per-user SPI change (apply and undo) is skipped and the user is told why (`FixContext.ElevatedAsAnotherUser`). This way, the wrong profile is never modified. A different case: if the interface runs under a standard account and UAC is approved with an administrator's credentials, the engine runs under that administrator and the pipe's access control list (§5) refuses the interface. The connection fails with an access error, the interface stays in limited mode, no engine operation can run and nothing is modified; that engine exits when the window closes or after 15 minutes without a request. The access error has no dedicated message: from the banner it appears as a generic error, and from the startup window it is only logged. This second case was traced in the code, not run.

### 3.2 Files and folders written

This table lists what MAUS itself writes. The console tools have their own scope (§3.3).

| Path | Content | Written by | Protection |
|---|---|---|---|
| `%ProgramData%\MAUS\journal\` | Journal of original values (one JSON file per session) | Engine | Folder reserved to SYSTEM + Administrators; files owned by Administrators; atomic writes; a file with another owner is ignored |
| `%ProgramData%\MAUS\settings\preferences.json` | Protected choices | Engine | Same |
| `%ProgramData%\MAUS\tasks\maus-task-<guid>.xml` | Temporary XML of the weekly task, deleted after use (if an antivirus holds it, it stays in that protected folder) | Engine | Same |
| `%ProgramData%\MAUS\pilotes\<folder>` | Driver backups (`pnputil /export-driver`) | Engine (console) | Same: the engine re-applies this protection (and refuses a link) before listing, backing up or restoring |
| `%LOCALAPPDATA%\MAUS\rapports\rapport-batterie.html` | Windows battery report | Interface (`powercfg.exe` without administrator rights) | User profile |
| `%LOCALAPPDATA%\MAUS\` (settings, history, logs, `memoire\`, `core-test.json`) | Display settings, score history, crash logs, memory sheets, core-test checkpoint | Interface; also the engine (its own crash log) and the weekly audit (score history, crash log), both with RedirectionGuard (§6) once it is applied at process start; the crash-log writer itself does not check for reparse points | User profile |
| `%LOCALAPPDATA%\MAUS\benchmark-history.json`, `benchmark\`, `bench-shaders\` | Benchmark results, result images (older ones deleted), compiled-shader cache | Benchmark process: with administrator rights when the engine started it (§2.3), then with the engine's process hardening of §6 (RedirectionGuard included) and **without** reading or writing the shader cache; without rights, it uses the cache | User profile |
| `%TEMP%`, or the root of the tested drive when %TEMP% is on another drive | Disk speed test file `MAUS-test-<guid>.tmp` (after a confirmation naming the drive); benchmark thumbnails in `MAUS-benchmark-<pid>` in %TEMP%; deleted after use | Interface; benchmark process for the thumbnails | The rights of the process that writes; a drive root has no MAUS protection |
| Path chosen by the user in a Save dialog | HTML report, CSV recordings | Interface | The rights of the interface; no MAUS protection |

### 3.3 Services, tasks, drivers and other configuration

- **Console tools (§2.2):** MAUS fixes the command, but the tool sets the scope. `DISM /RestoreHealth` repairs the Windows component store and `sfc /scannow` replaces damaged protected system files. `winget upgrade` and the PawnIO install or removal run third-party installers with administrator rights: the files, services, tasks and drivers they change are chosen by those installers, not by MAUS. `pnputil` changes the driver store for the selected display adapter. The statements below about services, tasks and drivers, and the "Never touched" list, describe MAUS's own actions, not what these tools and installers do.
- **Services:** MAUS itself starts, stops, creates or deletes no service. Only the `Start` value of the 10 services listed in §3.1 can be restored to the Windows default, and only when that service is disabled.
- **Scheduled tasks:** `\MAUS\WeeklyAudit` can be created, deleted or re-pointed (§2.3). Third-party logon tasks can be disabled and re-enabled (§2.1). Tasks under `\Microsoft\` are never changed.
- **Drivers:** apart from PawnIO below, MAUS's own driver actions concern only the display adapter, only through `pnputil` (§2.2). Every action is confirmed in the interface and, since 0.8.5, confirmed a second time by the engine itself (restart, remove and re-detect, back up, back up then delete, restore; §8). Before a driver is deleted, the interface requests a verified restore point first (`restorepoint.driver`; if it fails, the user decides whether to continue; the driver is backed up first in any case). MAUS ships no kernel driver.
- **PawnIO:** PawnIO, a signed third-party driver, is installed or removed only through winget, at the user's request, in a visible console; its installer adds its kernel driver. When PawnIO is installed and MAUS has administrator rights, MAUS uses it to read data, and MAUS's own code calls no fan or other control API (`src/Maus.Core/Workshop/Memory/PawnIo/SpdBusDriver.cs`, `PawnIoMemorySources.cs`). What it reads:
  - sensors, through LibreHardwareMonitorLib (processor and motherboard only);
  - memory SPD chips;
  - memory-controller registers (Intel `IntelMCHBAR` / `IntelMSR` modules, AMD SMN register reads);
  - the AMD power-management table (an SMU command that refreshes the table, then a read).

  A software guard in front of the SMBus module examines every transfer call (`ioctl_smbus_xfer`) and refuses writes, except the page-selection writes that reading an SPD chip requires (addresses 0x36/0x37 for DDR4; register MR11 at addresses 0x50–0x57 for DDR5). The module's other entry points (identification, port selection, write-protection query) are not filtered by MAUS; the SPD library calls them for detection only. MAUS therefore cannot modify an SPD chip or a DDR5 power-management IC through the transfer function. On AMD, refreshing the power-management table is a mailbox command sent to the SMU (a hardware write that only asks for the table to be updated), followed by reads. The software guard covers the SMBus module only. It does not cover LibreHardwareMonitorLib 0.9.6, whose metadata names hardware-write functions (`DisableIOSpaceLock`, `WriteIoPort`), which the library may use to reach motherboard sensors. MAUS does not call them itself, and it has not been traced which boards reach them, so "to read data" is established for MAUS's own calls and is not proven for that library's internals.
- **System protection:** WMI `SystemRestore.Enable("C:\")` is called only during `apply`, only if system protection is off **and** the user ticked the option to allow it (`ApplyOptions.EnableProtectionIfNeeded`).
- **Temporary ETW sessions:** `NT Kernel Logger` (latency, at most 5 minutes) and `MAUS_Compteur` / `MAUS_Releve` (PresentMon), stopped at the end and when the engine stops normally; if the engine process is killed or crashes, see §9, item 11.
- **Display:** the refresh-rate trial (`ChangeDisplaySettingsExW`) runs in the **non-elevated** interface, never in the engine. The trial mode is applied dynamically (not saved) and rolled back automatically after 15 seconds unless the user clicks "Keep"; "Keep" then saves the mode through the same API (`CDS_UPDATEREGISTRY`). This is the one setting that the interface itself changes outside the FixEngine journal (§9, item 12, lists the other changes outside the journal): it is undone by choosing the previous mode again (in MAUS or in Windows settings), not by the "Undo" button.
- **Never touched:** BIOS/UEFI, BCD and boot configuration, Defender exclusions, firewall rules, hosts file, proxy, page file, user accounts, credentials, CPU mitigations (MAUS only re-enables them).

---

## 4. Inputs, permitted values and validation per operation

General rules (`EngineServer.cs`, `EngineProtocol.cs`):
- A message is a 4-byte little-endian length followed by UTF-8 JSON. A length ≤ 0 or above **32 MiB** is refused before the body is read.
- Every request is signed (HMAC-SHA256) and carries a sequence number that can be used only once (§5). A request with a wrong signature closes the connection without an answer, before the request text inside the signed message is parsed; a request with a used number is refused the same way, after parsing and before anything is executed.
- One request per connection. A malformed message closes that connection; nothing is executed.
- JSON is deserialized into fixed record types. Unknown fields are ignored. A missing or wrongly typed field returns an error, and nothing is executed.
- Identifiers (`Checked`) must have 1–200 characters, with no control characters and no `\`, `/` or `:`. At most **500** identifiers in the `apply` list. This check applies to `apply`, `module`, the session id of `verify`, `revert`, `session` and the `frames` session name; the `console` fields have their own rules (§2.2). The free texts are bounded too (0.8.5): the restore-point description (1–200 characters, no control characters) and the verification notes (at most 500 entries of at most 2,000 characters); the list of acknowledged findings in `prefs.save` is bounded only by the message size.
- Enumerations travel by name. An integer outside an enumeration's definition is refused by the operation that would act on it (`schedule.set` since 0.8.5; `console` already refused an unknown kind).
- Language: if it is not one of fr, en, es, the engine uses the language of Windows. It only selects message texts.

| Operation | Inputs | Validation |
|---|---|---|
| `ping`, `audit`, `plan`, `journal`, `prefs.load`, `inventory`, `drivers`, `drivers.backups`, `sensors`, `processes`, `memory`, `restorepoint.list`, `restorepoint.create`, `restorepoint.driver`, `schedule.get`, `software` | None | — |
| `module` | Module id | Checked id; an id that is not one of the 20 built-in modules returns nothing |
| `apply` | List of fix ids; options `CreateRestorePoint` (bool, pre-checked in the interface), `EnableProtectionIfNeeded` (bool), `ProceedWithoutRestorePoint` (bool), `RestorePointDescription` (text) | The engine **recomputes the plan** from its own audit and keeps only the ids found in it. An unknown id is ignored. The interface cannot supply a registry path, a value or a task name. If the engine restarted since its last audit, it audits again before planning. The description is only passed as the restore point name to WMI; the interface always sends the default "MAUS : avant corrections" |
| `verify` | Session id; list of check results (fix id, effect, message) | Only results "no effect" and "pending restart" are kept, as a display note on entries that are already applied. A value to write or to restore can never be changed this way |
| `revert` | Session id, optional change id, `force` (bool, ignored) | Session and change must exist in the protected journal. The values written back are the ones the engine journaled itself. Since 0.8.5 the engine ignores any `force` sent through the channel: a value changed since the fix is always left as it is. Only the separate command-line tool offers `--force`: it is not part of the Store package, must be started by an administrator, and runs the FixEngine in-process |
| `session` | Session id | Checked id; must exist in the journal |
| `prefs.save` | Preferences object | Deserialized into the fixed `UserPreferences` record: enumerations, booleans, numbers, dates and texts (language code, last seen version, wanted findings with their recorded value, memory-voltage calibration). There is no field-by-field validation on save; the size is bounded only by the 32 MiB message limit. Values are stored in the protected file, never executed and never written to Windows settings. At use, they only select behaviour built into MAUS: a Game Bar profile outside 1-3 falls back to the detected one, and display values out of range fall back to a default or are clamped. A newly added wanted finding needs the engine's own confirmation (§8) |
| `process.terminate` | PID (int), optional start time | §2.3 |
| `latency` | Duration | 0 < duration ≤ 5 minutes |
| `frames.start` | Session name, optional buffer duration | Session ∈ {`MAUS_Compteur`, `MAUS_Releve`}; the duration only sizes the in-memory buffer. The duration is not range-checked; without one, the whole measurement stays in memory until stopped |
| `frames.read` | Session name, optional time windows, minimum frame count | Same session check; count clamped. The time windows are not range-checked; they only select which frames in memory are summarised |
| `frames.stop` | Session name | Same session check |
| `space` | Folder path | Fully qualified path that must exist. The check refuses only a path that starts with a literal double backslash (`\\server\share`, `\\?\`, `\\.\`). Other spellings of network and device paths are **not** filtered in this version: forward-slash forms (`//server/share`, `//?/C:/`, `//./C:/`), mixed separators (`\/server/share`) and `\??\C:\`. The interface only sends the roots of fixed drives (`WorkshopViewModel.Storage.cs`, `DriveType.Fixed`), so another path needs a request signed with the key (§5). In every case the operation is read-only: enumeration (depth ≤ 256, reparse points skipped) that returns paths, names, sizes and dates; nothing is written or executed |
| `console` | Kind + ids (§2.2) | Kind ∈ the six values of §2.2; each id validated as described there. Package ids have no count limit other than the message size: only ids also found in the engine's own list are used |
| `schedule.set` | Day of week, or none | Enumeration by name; an undefined value is refused |
| `benchmark` | api, resolution, scope, language, ray tracing (bool) | Closed sets (§2.3); anything else refused |

---

## 5. Named-pipe authentication, authorization, process verification and anti-replay

| Control | Implementation |
|---|---|
| Engine command line | The pipe name must match `^MAUS-[0-9a-f]{32}$`, the client PID must be a number, and the key fingerprint must be 64 lowercase hexadecimal digits; otherwise the engine exits (code 2) |
| Unpredictable name | `MAUS-<random GUID>` (`Guid.NewGuid`, 122 random bits), generated by the interface for each engine start |
| Pipe squatting | The first instance is created with `PipeOptions.FirstPipeInstance`: if another process already owns the name, the engine fails to start instead of serving it. Later instances do not use that flag, and the ACL grants `CreateNewInstance` to the user's account, so another process of the same account could create an instance of the same name once the engine runs: the interface then refuses that instance (the server process id does not match, see below) and sends nothing; at worst the engine stops after 3 refused connections. Denial of service, with the interruption effects of §9, item 7 |
| Access control list | Explicit `PipeSecurity` with two entries only: **Deny** `NETWORK` (S-1-5-2) full control, and **Allow** the account the engine runs under (`ReadWrite \| CreateNewInstance`). Remote clients are refused |
| Client verification (engine side) | For **every** connection, `GetNamedPipeClientProcessId` must equal the client PID (passed on the engine's command line; only its format is validated). In normal use, this is the interface that started the engine. Any other process is disconnected without an answer, and the attempt is recorded in the engine's in-memory breadcrumbs (§9, item 10) |
| Server verification (interface side) | For every connection, `GetNamedPipeServerProcessId` must equal the PID of the engine process that the interface started itself (`Process.Start` result). Otherwise the interface refuses to use the pipe and tells the user |
| What is not verified | The engine trusts the PID and key fingerprint on its own command line. It does not check its parent process, or the executable path, signature or integrity level of the client, and the interface does not check those of the engine beyond the PID. A program running in the user's session could therefore start `MAUS.exe --engine` itself with its own PID and fingerprint. UAC would still appear, showing MAUS, and if the user approves, that program is the engine's client: it can send only the closed list of §1 with the validation of §4, and the engine's own confirmations of §8 still appear. This was traced in the code, not run |
| Secret key | For each engine start, the interface draws a new 256-bit key from the Windows cryptographic random generator (`RandomNumberGenerator`). The key is **never** put on the command line, which other programs can read: only its SHA-256 fingerprint is. On its first connection, the interface presents the key over the pipe; the engine accepts it only if its SHA-256 matches the fingerprint it received at launch (constant-time comparison), and keeps it for its lifetime. Before that, every request is refused (`src/Maus.Core/Engine/Broker/EngineKey.cs`) |
| Request authentication | Every request is the exact JSON text of `{operation, language, arguments, sequence number}` with an HMAC-SHA256 signature computed with the key (domain-separated: `MAUS-REQ`). The engine checks the signature (constant time) **before** parsing the request; a missing or wrong signature closes the connection without an answer |
| Anti-replay | Each request carries a sequence number that the interface increments. The engine accepts each number **once only**; it also refuses a number older than a window of 4,096 (`ReplayWindow`, same principle as the IPsec anti-replay window, RFC 4303 §3.4.3). The window allows several requests in parallel to arrive out of order. A recorded request sent again, or a request modified after signing, is refused without an answer |
| Response authentication | Every response (progress messages included) is signed with the key and bound to the sequence number of its request (domain `MAUS-RES`). The interface refuses a response that is not signed for its own request, so an old response cannot be replayed to it either |
| Authorization | The engine runs only operations from the closed list (§1), with the validation of §4. There is no "generic" operation |
| Message bounds | 32 MiB maximum (the length is checked before the body is read; a body within the limit is then read into memory before its signature is checked), one request per connection (after the one-time key presentation), JSON into fixed types |
| Lifetime binding | The engine watches the interface process and exits when it exits |
| Security stop | After **3** refused connections (another process, a wrong key, a replayed or modified request), the engine stops by itself (`EngineServer.MaxRefusals`, exit code `SecurityStopExitCode`); the interface warns the user and returns to limited mode. The engine takes no action of its own on the PC, but it does not wait for a request in progress: a fix cut off this way stays `Pending` for Undo (§9, items 7 and 11) |

These controls are layered: a request is executed only if it arrives on a local connection, from the process named on this engine's command line, on a pipe that only that account can open, signed with the key of this engine start, with a sequence number never used before. Even then, the engine only runs operations from its closed list, recomputed from its own audit and catalogs. Tests: `tests/Maus.Core.Tests/Engine/EngineKeyTests.cs` (signature, fingerprint, replay window) and `EngineBrokerTests.cs` (a real pipe: a request before the key presentation, a wrong key, a replayed request and a modified request are all refused; a response is valid only for its own request).

---

## 6. No arbitrary commands, scripts, paths, arguments or executables

- The protocol has no field for a command, script, executable path, registry path, registry value to write or argument list, and every request must be signed with the key of this engine start (§5). The interface sends an operation name from §1, plus identifiers, values from closed sets and the three bounded free texts of §0 (§4). It can also send one folder path, for `space`: the interface only sends the roots of fixed drives, the engine refuses only a path that starts with `\\` (forward-slash spellings such as `//server/share` are not filtered in this version, §4), and the folder is only enumerated, read-only.
- Every executable that the engine starts itself is fixed in code (what winget then starts: see the limits below):
  - `cmd.exe`, `schtasks.exe` and `pnputil.exe` from `%SystemRoot%\System32` (the non-elevated interface also starts `powercfg.exe` for the battery report, and `%windir%\explorer.exe`, only to open a web page, a folder, a file or a Settings page with standard rights; the elevated weekly-audit process starts `explorer.exe` once, to open MAUS without rights when the user clicks "Open MAUS", §7);
  - `DISM.exe` and `sfc.exe` started by that `cmd.exe`, whose working directory is System32;
  - the read-only tools of §2.4 from System32 only, never from the `PATH`;
  - `winget.exe` only from `%ProgramFiles%\WindowsApps`;
  - bundled PresentMon only after its SHA-256 check;
  - `MAUS.exe` itself for the benchmark.
- Programs are started with `ArgumentList` (each argument passed separately), with three exceptions that use a single argument string: the `runas` start of the engine (validated pipe name, PID and fingerprint), `explorer.exe` (one quoted URL, folder or file, or a Settings page), and the visible consoles of §2.2. The consoles use a `cmd.exe` command line built by MAUS from constants, from identifiers that passed the validation of §2.2 and from display text. Displayed text is escaped, a driver path containing a `cmd` special character is refused, and the winget path (inside WindowsApps) must not contain a quote.
- No PowerShell, no script host and no `cmd /c` with user text. `ShellExecute` is used only once: to start the engine itself with `runas`.
- Registry writes come only from the plan computed by the engine (§3.1). There is no "write this value" operation.
- Three of these rules are enforced by an automated test that fails the CI run (`SecurityGuardTests`, §2): where programs are started, `UseShellExecute`, and `cmd.exe`. The others are not covered by that test. GitHub CodeQL (`security-extended`) analyses pushes to `main` and `claude/**` branches, pull requests and a weekly scheduled run.
- **Two limits, stated plainly.** These rules describe what reaches MAUS through the channel and what MAUS starts; they do not make these two things impossible:
  - After its task, each console remains an open elevated `cmd.exe` prompt (`/k`) so that the user can read the result. It outlives the engine, and MAUS sends it nothing more. The person who accepted UAC could type any command there, as in any administrator prompt they open themselves; MAUS does not restrict that prompt. Nothing from the channel reaches it except the validated identifiers of §2.2.
  - `winget` runs the publisher's installer with administrator rights. MAUS fixes or validates the package id; winget selects the installer, and MAUS does not inspect it.
- **Process hardening** (`src/Maus.Core/Platform/ProcessHardening.cs`, `SetProcessMitigationPolicy`, applied at start before any request): the interface, the engine and the weekly audit refuse to load DLLs from a remote share or from a file with a low mandatory label (`NoRemoteImages`, `NoLowMandatoryLabelImages`). The elevated engine and the scheduled audit also search Windows DLLs in System32 first (`PreferSystem32Images`), disable legacy extension point DLLs (`ProcessExtensionPointDisablePolicy`), and enable RedirectionGuard (`EnforceRedirectionTrust`): they do not follow a filesystem junction created by a non-administrator account, which blocks the classic trick of redirecting an elevated write. These policies only restrict MAUS's own processes; a policy Windows refuses is logged and skipped, and MAUS does not re-check afterwards that it was applied. Programs started by the engine (the `cmd.exe` consoles and the tools they run, `schtasks`, `pnputil`) do not inherit them: a mitigation policy is per process. Microsoft notes that such policies are most effective when applied at process initialization: they are applied at the very start of each process, but do not cover what Windows loaded before that point. The benchmark process (`MAUS.exe --benchmark`) applies the engine's policies when it has administrator rights (started by the engine), and the interface's policies otherwise.

---

## 7. Elevation timing and engine termination per workflow

General behaviour:
- **At start**, there is no UAC prompt. The interface runs as a standard user. MAUS may show, in its own window, the disclaimer (first start, and again if its text changes) and a window "How do you want to use MAUS?" that explains the two modes side by side: "Limited mode, without administrator rights" and "All features, with administrator rights". That window appears at each start until the user ticks "Don't ask me again" or turns it off in Settings > Administrator rights. UAC appears **only if** the user clicks "All features"; "Limited mode" keeps MAUS without rights. After an update, "What's new" is a card on the home page (since 0.8.4), not a window. The 0.8.4 home page changes (simple layout, Mica background through `DwmSetWindowAttribute` on MAUS's own window, keyboard shortcuts) add no engine operation, no network access, no program start and no file loaded from outside MAUS.
- **Limited mode:** while the engine is not running, every control that needs administrator rights is greyed out (audit, fixes and their restore-point options, undo, repair, software upgrades, PawnIO, driver actions and backups, restore points, weekly audit, protected choices, memory sheet, latency test and its duration, frame counter), and a banner at the top of the window says "Limited mode: greyed-out options need administrator rights" with an **"All features"** button (`src/Maus.App/ViewModels/ElevationState.cs`, `MainWindow.xaml`). No greyed-out control can be clicked. The only actions that can bring a UAC prompt, other than the controls made for it, are the three exceptions listed under "Switching to all features".
- **Switching to all features:** an explicit click on "All features" (banner or startup window) or on "Enable advanced sensors" starts `MAUS.exe --engine` with `runas`; UAC appears at that moment. If two clicks happen together, only one prompt is shown. When the engine is ready, the greyed-out controls become available and the banner disappears. These are the only controls made to start the engine: since 0.8.5, the memory sheet no longer does it either (if the engine stopped between the moment its button became available and the click, the sheet says so instead of prompting). There are three narrow exceptions, traced in the code and not run. Each follows a user action, and declining cancels only that action (§8):
  - if the engine stops on idle while a control is still available or its confirmation dialog is open (for example Apply fixes), that action starts the engine again, and UAC appears for it;
  - the language selector is never greyed: changing the language after a completed audit re-runs the audit, and if the engine has stopped, that audit starts it and UAC appears;
  - "Save report" is available as soon as an audit exists: after fixes were applied in this window, saving the report reads the session from the protected journal, and if the engine has stopped, that read starts it and UAC appears.
- **Location check before elevation:** before starting the engine, the interface checks that `MAUS.exe` is in a folder that only administrators can modify (`Program Files`, `Program Files (x86)`, `WindowsApps`; `ScheduledAudit.IsInProtectedFolder`). If it is not (for example a copy in Downloads or on a USB stick), another program could have replaced `MAUS.exe`, and Windows would give administrator rights to it. MAUS then warns the user and asks whether to continue (default: No); on No, no UAC prompt is shown and MAUS stays in limited mode. Installed under Program Files through the Microsoft Store, the installer or winget, MAUS is in a protected folder, so this warning does not appear. The check knows only those two Program Files folders: a Store install on another drive (`X:\WindowsApps`) is not recognised as protected, so the warning appears and the weekly audit is refused. This was read in the code, not run with this package.
- **Following actions** reuse the running engine, with no new prompt.
- **15 minutes without any request:** the engine exits (`EngineServer.IdleTimeout`), but never while a request is in progress. For example, a running latency measurement or benchmark counts as a request. The interface is notified when the engine process exits (`Process.Exited`): the controls are greyed out again and the banner comes back. If the engine restarted since the last audit, it audits again before any plan or apply, so it never acts on stale data. Automatic refreshes are requests. While the engine runs, the sensor refresh (Live page, monitor window, a running test, diagnosis or recording), the process list refresh (Processes page, diagnosis) and the frame counter refresh reach the engine every few seconds or faster, so the engine does not reach the idle timeout while one of them is active, and it keeps its rights until that view closes and 15 minutes pass, or until the interface exits. There is no control to return to limited mode without closing MAUS. If UAC is approved but the engine does not answer the first request, the interface reports an error and stays in limited mode (§3.1 gives the different-account case). It does not stop that engine, which exits with the interface or after 15 minutes without a request.
- **Security stop:** after 3 refused connections (another process, a wrong key, a replayed or modified request; §5), the engine stops by itself with a dedicated exit code; the interface goes back to limited mode and warns the user that a program tried to control the engine, and advises a scan with Windows Security. Nothing else is done (no action on the PC that a malicious program could abuse). An operation in progress is cut, not finished (§9, item 11).
- **Interface closed or crashed:** the engine detects that the interface process has exited and stops. If a fix was being written at that moment, the journal keeps it as `Pending` and "Undo" handles it (§9). Closing MAUS during an apply asks for confirmation first; closing it during an undo does not (§9, item 11).
- **MAUS started with "Run as administrator" by the user:** there is no separate engine; the same operations run in-process (`LocalMausEngine`). In that mode there is no second confirmation by the engine (§8), because there is no second process: the interface dialogs are the only confirmation, and everything the interface does, battery report included, runs with the rights the user gave it.
- **Weekly audit (only if the user enabled it):** the task `\MAUS\WeeklyAudit` (§2.3) starts `MAUS.exe --scheduled-audit` with highest rights and **without a UAC prompt**: the user gave that consent once, when creating the task, which itself requires the engine. That process applies the engine's process hardening (§6), changes no system setting and applies no fix, writes only its score history and crash log in the user profile (§3.2), and exits unless a red problem is found. In that case it shows a notification; if the user clicks "Open MAUS", MAUS is started again **as a normal process, without administrator rights** (through Explorer, like a double-click), and the elevated audit process exits. The window never runs with the task's rights; to fix something, the user chooses "All features" and the usual UAC prompt appears.

| Workflow | Elevation | Engine runs until |
|---|---|---|
| "All features" (banner or startup window), "Enable advanced sensors" | UAC at the click: these are the only controls made to start the engine (exceptions: "Switching to all features" above) | Idle timeout, security stop or interface exit |
| Audit, view and apply fixes, undo, history (journal), protected choices ("wanted" findings, Game Bar profile, laptop power) | Greyed out in limited mode; available with all features | Same |
| Repair Windows files, software upgrades, PawnIO install/remove, driver actions, driver backups and restore | Greyed out in limited mode; with all features, the console then runs on its own | The console window stays open until the user closes it, even after the engine stops |
| Restore points (list, create), weekly audit (enable/disable) | Greyed out in limited mode | Idle timeout, security stop or interface exit |
| Weekly audit run (if enabled) | Highest rights from the task, no UAC prompt (consent given when the task was created); "Open MAUS" starts a new window without rights | Exit after the audit, or when the notification is closed or "Open MAUS" is clicked |
| Memory sheet, latency test, frame counter | Greyed out in limited mode | Same; frame captures stop with the engine |
| Game session recording | Never asks. In limited mode it records without frames per second (they need the engine) | — |
| Benchmark | Never asks. With all features, it is started by the engine so that the CPU temperature can be watched through PawnIO; in limited mode it runs without that reading (the graphics card safety stop stays active) | Idle timeout (not while the benchmark request is in progress), security stop or interface exit. The benchmark process itself runs until it finishes, Esc is pressed or its own window is closed, its heat safety stop triggers or the graphics card is lost, even if MAUS's window closes or the engine stops: neither MAUS's window nor the engine terminates it. Started by the engine, it keeps administrator rights until then |
| Process list, process termination, disk-space analysis, drivers list, inventory, software list | Never asks. They run with administrator rights only if the engine is already running | — |
| Battery report | Never asks; run by the interface without administrator rights (not an engine operation) | — |
| Refresh-rate trial, Explorer restart, opening web pages, folders and Settings pages, CPU/RAM/VRAM/disk tests, ping test | Never elevated: they run in the interface process (web pages and folders are opened through `explorer.exe`) | — |

---

## 8. Confirmation before changes, and declined UAC

**First start:** the user must accept a disclaimer (what MAUS does, the risks, and that the user decides); otherwise MAUS closes.

**Before every modifying action**, except those listed in this paragraph, the interface shows a Yes/No dialog whose default button is **No**. It describes what will change and, where an undo exists, how to undo it. Actions with no dialog: removing a "wanted" mark and setting other MAUS preferences, such as the laptop power choice or the Game Bar profile, except profiles 1 and 2 on a Ryzen X3D (they only change a MAUS preference); disabling the weekly audit (it only removes MAUS's own task); starting the benchmark, the latency test or the frame counter (they change no setting); and the automatic re-pointing of the weekly task after a Store update (§2.3). One dialog has **Yes** as its default: if another MAUS is running with no window, a new launch asks whether to close that stuck copy, because it can block the memory bus (`App.TakeOverFromWindowlessInstance`). On Yes, `SingleInstance.CloseWindowlessOthers` calls `Process.Kill` on the other `MAUS.exe` processes that have no window, with the rights of the new launch. This is not an engine operation. The Yes/No dialog is the user's consent step; it lives in the non-elevated process, so it is not a barrier against code already running in the user's session: that barrier is the engine's own confirmation below, which covers the most sensitive operations.
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
- Marking a finding as "wanted" and choosing Game Bar profile 1 or 2 on a Ryzen X3D also ask for confirmation.

**Second confirmation shown by the elevated engine itself** (since 0.8.4, `src/Maus.Core/Engine/Broker/EngineConfirmation.cs`): the interface runs without administrator rights, so a malicious program running under the same account could in theory inject into it and send signed requests while the engine is running. For the most sensitive operations, the engine therefore asks again in a Windows message box of its own (`MessageBoxW`, topmost, default button **No**), before doing anything:
- terminating a process (`process.terminate`);
- every graphics-driver action: restart, remove and re-detect, back up, back up then delete, and reinstalling a driver backup (`console`, driver tasks; restart and back up since 0.8.5);
- installing or removing PawnIO (`console`);
- marking a new finding as "wanted" (`prefs.save`, only when a finding is added to the list).

The other modifying operations (apply, undo, restore points, creating the weekly-audit task, the repair and software-upgrade consoles) rely on the interface dialog and on the closed list; removing the weekly task, re-pointing it, the latency test, the frame counter and the benchmark have no dialog and rely on the closed list alone. All of them can only do what the engine's own plan, lists and catalogs allow. A program without administrator rights cannot click or type in a window of the elevated engine: Windows User Interface Privilege Isolation (UIPI, Microsoft "Windows Integrity Mechanism Design") blocks it. A "No" returns a message to the interface and nothing is done. The interface calls `AllowSetForegroundWindow` for the engine process before each request so that this box can come to the front. When MAUS itself was started with "Run as administrator", there is no separate engine and only the interface dialog is shown.

Admin actions that only **read** (audit, sensors through PawnIO, memory sheet, latency, frame counter, journal) have no MAUS dialog: they run only after the user accepted a UAC prompt (§7, including its exceptions), or, for the audit, from the weekly task the user enabled; that is the consent.

**UAC declined:** Windows returns error 1223 (`ERROR_CANCELLED`). MAUS throws `EngineDeclinedException` and shows: "Autorisation administrateur refusée : rien n'a été lancé. MAUS reste en mode limité, sans droits administrateur." (Administrator permission refused: nothing was started. MAUS stays in limited mode, without administrator rights.)
- Nothing is started and nothing is written.
- MAUS stays in limited mode: the controls that need administrator rights stay greyed out, and the "All features" button can be clicked again later.
- There is no retry loop, and no fallback that performs the action without elevation.

---

## 9. Restore point, partial failure, cancellation, logging and undo

Implemented in `src/Maus.Core/Fixes/FixEngine.cs`, `RestorePoints.cs`, `JournalStore.cs` and `AtomicFile.cs`. Items 1 to 10 describe fixes applied through `FixEngine`; item 12 says what they do not cover.

1. **Blocked cases.** No fix is written in these cases (a restore point, when requested, is created before the journal folder is checked, so it can exist when the apply then stops):
   - the PC is managed (domain or MDM enrolment);
   - the engine is not elevated;
   - the journal folder cannot be secured (`JournalUnsafeException`).
2. **Restore point first.** When the restore-point option is on (pre-checked), a restore point is created before any write and then **verified** by re-reading the list of restore points.
   - If system protection is off, it is enabled only if the user ticked that option; otherwise the point fails.
   - If the point fails, the apply stops with "Aucune modification n'a été faite" (no change was made). The user is then asked whether to continue without a restore point (explicit `ProceedWithoutRestorePoint`; default: no). No fix has been written at that point. If system protection was enabled just before (the ticked option), it stays enabled even though the message says no change was made: MAUS does not turn it off (item 12), and the user can do so in Windows settings.
   - If the user unchecks the option, the confirmation dialog says so beforehand, and the journal records "restore point not requested by the user".
3. **Journal before write.** For each value of a fix, the original value is read just before writing, together with whether its key existed. It is saved to the protected journal (state `Pending`) **before** anything is written.
   - If the original value cannot be read, that fix is skipped and nothing is written.
   - A value already changed by another fix of the same session is skipped.
   - A value already at the target is not written.
4. **Write, then read back.** Each write is read back. If any write of a fix fails, or reads back differently, **the whole fix is rolled back** in reverse order (all-or-nothing per fix), and each restored value is re-read. The result names the failed fix and the reason. The other fixes chosen in the same session are reported separately.
5. **Rollback failure.** If a rollback itself fails, the entry is marked `RollbackFailed` and shown to the user, and "Undo" stays available to retry.
6. **Journal failure during a session.** If the journal can no longer be secured (`JournalUnsafeException`), no further write is made, and the writes already made in the session are rolled back. Any other journal write error (disk full, file locked) also stops the session and no further write is made, but nothing is rolled back: the writes already made stay in the journal and can be undone from History. In the engine, such an I/O error closes the connection: the interface reports that the corrections were interrupted and points to History, without naming the journal as the cause, and this path does not refresh the History list (its refresh button or the next audit does). An access-denied error is returned with its own message instead. This was traced in the code, not run.
7. **Interruption.** If MAUS is closed, the engine makes a security stop (§5) or a crash occurs during a write, the next "Undo" handles the `Pending` entries:
   - if the original value is still in place, nothing had been written;
   - if the new value is in place, it is restored like an applied fix.
   - Exception, traced in the code and not run: if the engine stops while a restore point is being created (MAUS closed, a security stop, or a crash), `SystemRestorePointCreationFrequency` can stay at 0. It is not journaled, so Undo does not restore it; the effect is that Windows no longer skips a restore point made within 24 hours of another.
8. **Effect check.** After the apply, the interface runs a fresh audit and tells the user which fixes were written but have no visible effect (for example a policy ignored on the Home edition). This note is stored in the journal (`verify`). If that audit fails, the fixes are shown as "not verified", never as "no effect".
9. **Undo.** Undo works by session or by single change (History page; `--revert` in the separate command-line tool, which is not part of the Store package), and each restored value is re-read. If a value was changed by something else since MAUS wrote it, MAUS **leaves it as it is** and reports "changed since". Neither the interface nor the engine's channel can overwrite such a value (the engine ignores `force` from the channel, §4); only the separate command-line tool, not part of the Store package and started by an administrator, offers `--force`.
10. **Logging.** The journal is one JSON file per session in `%ProgramData%\MAUS\journal` (SYSTEM + Administrators), written atomically. A session file whose owner is not SYSTEM or Administrators, or that is a link, is ignored and never replayed. Crash logs are written to `%LOCALAPPDATA%\MAUS\logs`. Nothing is sent anywhere. There is no persistent log of engine requests. Only fixes applied through `FixEngine` are recorded, in the journal. The engine's diagnostic breadcrumbs, including refused connections and operations that ended in error, are an in-memory list of the last 60 lines, written to disk only in a crash log. They are lost when the engine stops normally or by the security stop; in that last case the user's warning (§7) is the only record.
11. **Cancellation.** Before writing, "No" in the confirmation (§8) or in the "continue without a restore point" question (item 2) ends the action with no fix written. Once writing has started there is no Cancel button: `FixEngine.Apply` takes no cancellation token and runs to the end. Closing MAUS during an apply asks first (default No); on Yes the engine stops, and a fix cut off mid-write stays `Pending` for "Undo" (item 7). MAUS has no cancel control for a console: the user closes its window. The benchmark stops with Esc. A latency measurement has no cancel control in the interface: it ends at its chosen duration or when the engine stops. Undo has no cancel control either: `FixEngine.Revert` takes no cancellation token, and closing MAUS during an undo does not ask. It saves the journal once, at the end, so if the engine stops during an undo, values already restored are written but the journal still shows them as applied; a later Undo finds a value different from the fix, leaves it as it is and reports "changed since". A security stop (§5) cuts a running apply, undo or restore point in the same way as closing MAUS, without asking: item 7 and this item then apply. The audit, the memory reading and restore-point creation have no cancel control. A memory bus read cannot be interrupted: the interface stops waiting, and the engine refuses another read and does not reach its idle timeout until the first ends. The "Stop" button of the disk-space analysis stops the interface only when the engine runs the analysis: the connection closes, the read-only enumeration continues in the engine until it ends, and the engine does not reach its idle timeout meanwhile. In limited mode it stops the enumeration itself. If the engine process is killed or crashes, no MAUS code runs to stop the `NT Kernel Logger` session or PresentMon. All of this was traced in the code, not run.
12. **What this section does not cover.** Console operations (DISM/SFC, winget upgrades, PawnIO, driver actions) and process termination create no journal entry and have no MAUS "Undo"; their record is the visible console or an on-screen message. The same is true of creating a restore point, enabling system protection (§3.3), the weekly task (§2.3) and the refresh-rate setting (§3.3). A driver delete is preceded by a backup, which is required, and by a request for a verified restore point; if the point fails, the user decides whether to continue (§3.3). PawnIO is removed with `PawnIoUninstall`; the weekly task is removed by turning it off. A failed manual restore point (`restorepoint.create`) only returns an on-screen message: nothing else is changed, and the frequency value is restored as in §2.3. Partial failure in a console: steps are chained, so `sfc` runs even if `DISM` fails, `/scan-devices` runs even if `/remove-device` fails, and each winget upgrade runs even if an earlier one failed; only Remove stops, when the export fails. MAUS produces no summary. The engine reports only that the window opened (`VisibleConsole.Run`), never whether the task succeeded; the result is what the console shows.

---

## 10. Separation of read-only audit and privileged modification

| Layer | Rights | What it can do |
|---|---|---|
| Audit modules (`IAuditModule.Detect`, `IFixableModule.Plan`) | Read-only by contract | They receive `AuditContext`: registry **reader**, WMI/CIM reader, event-log reader, package and file readers, and `ReadOnlyCommandRunner` (§2.4). It contains no writer. Some modules also make the direct read calls listed in §2.5. These are ordinary code in the same process: separation from writers is by design and review, not a technical barrier. Unit tests run every module with fakes, and an apply/undo round-trip test checks each fixable module |
| `FixEngine` (`Apply`, `RecordVerification`, `Revert`) | The only writer of fix settings | It receives `FixContext` (writers + journal), which is separate from `AuditContext`, and writes only `PlannedChange` objects produced by the engine's own `Plan`. Changes outside fixes, all listed in §3: `RestorePointCreator` sets the restore-point frequency value to 0 around the call, then restores it, and can enable system protection during `apply`; `schtasks` for the weekly task; the consoles of §2.2; and, in the interface, the refresh-rate mode |
| Engine Workshop operations (§2.2, §2.3) | Engine | Fixed executables and validated inputs; a confirmation in the interface, except for the actions listed in §8 that have none |
| Interface | Standard user | Displays results and sends operation names and ids. It never accesses the protected journal, the protected preferences or the privileged tools directly; everything that needs the engine goes through `EngineHost.Current`. It does act on its own, without the engine and without rights, for the Explorer restart, the refresh-rate trial (§3.3), the CPU/RAM/VRAM stability tests, the disk speed test (§3.2), the ping test, the battery report, reports saved where the user chooses, and its own files in the user profile (display settings, score history, benchmark history, including the path of the benchmark result image that it decodes and can open) |

The audit modules do read, in the elevated process, inputs that a standard user can influence: the `Run` values of HKCU (M01 checks the signature of the program they point to with `WinVerifyTrust`), the shortcuts of the Startup folders (M12 parses them with MAUS's own bounded parser; a file above a fixed size is skipped), and the outputs of the read-only commands of §2.4. These inputs are parsed into typed values; a parser failure turns the module grey (no result) and never into an action.

The audit can be run as many times as the user wants without changing any setting. No fix is ever applied automatically, including by the weekly scheduled audit, which changes no system setting and applies no fix: it notifies, and writes only its score history and crash log in the user profile (§3.2). Two read operations of §1 do make one small change: `schedule.get` can re-point the MAUS weekly task after a Store update (§2.3), and `drivers.backups` re-applies the protection of MAUS's backup folder (§2.5). Reading through PawnIO also involves the hardware writes stated in §3.3, within the limits stated there; `latency` and `frames.start` open temporary ETW sessions.

---

*Contact: https://sites.google.com/view/maus-help/accueil · Source code: https://github.com/TERMINATORmdel101/MAUS (GPL-3.0). This application and this document were written with the help of an AI (Claude, Anthropic), as stated in the application's "About" window.*
