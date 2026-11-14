# Wasla Print Bridge Windows Service (WAS-61)

> **Installation.** Since WAS-62 Print Bridge is installed with `Wasla.PrintBridge-Setup-x64.exe`, which registers this service exactly as documented below ([print-bridge-installer.md](print-bridge-installer.md)). The app alone is not a printing installation: it shows "The Print Bridge service is not running" until the service is registered. The support ZIP (development and support recovery only) needs the manual commands in [Installer handoff](#installer-handoff-was-62). Builds from this repository are unsigned development builds until the release pipeline signs them; do not give them to restaurants.

## Purpose and scope

This document owns the Print Bridge process split introduced by WAS-61: the Windows Service that runs the printing engine, the tray/WebView2 app that is now its client, the local IPC contract between them, the service identity and data folder permissions, lifecycle and crash recovery, and the handoff to the installer (WAS-62) and update delivery (WAS-63).

It does not repeat the job flow, setup links, moves between restaurants or the app window; those stay in [print-bridge.md](print-bridge.md). The installer, the WebView2 Runtime and the app's start at sign-in are in [print-bridge-installer.md](print-bridge-installer.md) (WAS-62); automatic updates are WAS-63.

## Process boundary

```text
Wasla.PrintBridge.exe (per Windows user, per session; WinExe)
  tray, WebView2 app window, native dialogs (connection, confirmations), runtime notice,
  wasla-printbridge:// handler, per-user single instance and setup-link forwarding (WAS-92),
  localization (TR, EN, AR, RU)
        │  named pipe \\.\pipe\WaslaPrintBridge.Service, protocol version 1
        │  (PrintBridgeServiceClient → PrintBridgeIpcServer)
        ▼
Wasla.PrintBridge.Service.exe (Windows Service "WaslaPrintBridge", NT SERVICE\WaslaPrintBridge)
  the only engine: PrintBridgeEngineHost → PrintBridgeRuntime (poll, claim, print, report),
  WaslaPrintBridgeClient, settings and device token, print history, job journal,
  setup links and moves (PrintBridgeAutoSetupCoordinator), pending-move recovery,
  printer discovery and printing (winspool), machine-wide engine lock
```

| Executable | Project | Owns | Never does |
|------------|---------|------|------------|
| `Wasla.PrintBridge.Service.exe` | `src/Wasla.PrintBridge.Service` | Polling, claiming, printing, acknowledgement, settings, device token, history, journal, setup and move logic, printer discovery | Show a window, take a credential from anything but an authorized IPC request, accept remote clients |
| `Wasla.PrintBridge.exe` | `src/Wasla.PrintBridge` | Everything a user sees and every confirmation a user gives | Poll, claim, print, read or write the settings file, hold the device token, take the engine lock |
| (library) | `src/Wasla.PrintBridge.Core` | The engine types, `PrintBridgeEngineHost`, the IPC contract (`Ipc/`) shared by both executables | Reference WinForms or WebView2 |

The app's composition (`PrintBridgeUiServices`) registers no engine, HTTP client, printer or settings store; `PrintBridgeUiCompositionTests` checks it. The service's composition is `PrintBridgeAppServices` in the service project.

## Service identity and privileges

| Item | Value |
|------|-------|
| Service name | `WaslaPrintBridge` (`PrintBridgePaths.ServiceName`) |
| Display name | `Wasla Print Bridge` |
| Account | `NT SERVICE\WaslaPrintBridge` (virtual account; no password, its own SID `S-1-5-80-…`) |
| Start type | Automatic, depends on `Spooler` |
| Required privileges | `SeChangeNotifyPrivilege` only (`sc privs`) |
| Recovery | Restart after 5 s, 10 s, 60 s; failure count reset after one day; also on a non-zero exit (`sc failureflag 1`) |

Why a virtual account: it is not an administrator, has no interactive logon, has a SID of its own for the data folder and the engine lock, and needs no password. LocalSystem would work but gives the engine far more than it needs. LocalService would work too, but its SID is shared by many services, so it cannot own the data folder exclusively.

What the account needs, and why it has it:

- **Network:** outbound HTTPS to the Wasla server. Virtual accounts make outbound connections like any process; nothing authenticates as the account (the device token is the credential).
- **Spooler and printers:** see [Supported printer installation](#supported-printer-installation).
- **Session 0:** the service has no desktop. Printing is RAW through winspool (`OpenPrinter`/`StartDocPrinter`/`WritePrinter`), which needs no desktop and no GDI device context, so it works in session 0. Nothing in the service shows UI; every confirmation is shown by the app in the user's session.
- **Data folder:** Modify on `C:\ProgramData\Wasla\PrintBridge` (below).
- **Registry:** none. The `wasla-printbridge://` handler is the installer's, for every user (`HKLM\Software\Classes`, WAS-62); a development or support copy of the app writes it per user.

## Supported printer installation

The service prints as `NT SERVICE\WaslaPrintBridge` in session 0. `Everyone` has *Print* on a printer by default, so it can enumerate and print to every printer installed **for the computer**: a USB or local TCP/IP-port printer added by an administrator, a driver's "all users" install, or a shared network printer added for all users (`printui /ga /n\\server\queue`, then restart the Spooler). Printing is RAW through winspool, which needs no desktop.

It does **not** see a printer connection a user added in their own profile (for example a shared `\\server\queue` added from Windows Settings by that user): per-user connections live in that user's registry hive, which the service never loads. Before WAS-61 the app printed as the signed-in user, so such a printer worked; after it, it does not. What the user sees:

- The Printer tab lists only the printers the service can use and says, permanently, "Only printers installed for all users of this computer are listed…" (`Shell.Printer.ServiceScope`). Saving any other name is refused (`Shell.Printer.NotInstalled`).
- When the saved printer is missing for the service but this user's Windows lists it (an installation that used a per-user printer before WAS-61), the printer state reads "This printer is installed only for your Windows account, so the Print Bridge service cannot use it. An administrator must install it for all users." (`Shell.Printer.UserOnly`) instead of "not found". Printing does not start until a printer the service sees is chosen.

Verified on the GitHub-hosted runner by `PrintBridgeWindowsServiceTests.TheRealService_PrintsToAMachinePrinterInSession0_AndNeverToAPrinterItCannotSee`, with test mode off:

- A saved printer the service cannot see: printing does not start (`Error.PrinterUnavailable`), the status is "not found", the service does not list it, saving it is refused, and a waiting job is not claimed. The app's `Shell.Printer.UserOnly` text is built from that real status with the user's printer list substituted.
- A printer installed for the computer while the service runs (Windows' "Generic / Text Only" driver on a standard TCP/IP port sending RAW to a listener on 127.0.0.1, as a network receipt printer on port 9100 receives it): the service lists it, it can be saved, and the service prints a claimed job to it as its virtual account in session 0 through the Windows spooler, once.

Not verified, because it cannot be built on one computer: a real per-user printer connection. Windows refuses a printer connection to a printer the same computer shares, under any of its names: error 123 by the computer's name, its address or an alias for that address, error 1801 by `localhost`, `127.0.0.1` or an alias for it, also with `DisableLoopbackCheck`, the File and Printer Sharing firewall rules or RPC over named pipes for printing. "Microsoft Print to PDF" cannot be shared at all (error 3022, its driver). A per-user connection needs a second computer that shares the printer; with a physical USB or network receipt printer under the service account, that remains for WAS-28.

## Data folder and permissions

The folder and its files are unchanged: `appsettings.json`, `print-history.json`, `pending-connection-change.json`, `logs\` — plus the new `print-journal.json` (below). Settings, the device token, history and a pending move keep their formats, so an existing installation keeps its pairing, printer, installation id and history when the service takes over.

Before WAS-61 the folder inherited the ProgramData defaults: every local user could read the settings file (and so the device token) and create files in the folder. The service needs a folder that only it and administrators control, because it trusts what is in it:

| Path | Owner | Access |
|------|-------|--------|
| `C:\ProgramData\Wasla\PrintBridge` (and everything below, inherited) | `BUILTIN\Administrators` | `SYSTEM` Full, `BUILTIN\Administrators` Full, `NT SERVICE\WaslaPrintBridge` Modify; inheritance from ProgramData removed |
| `...\logs` | `BUILTIN\Administrators` | as above, plus `BUILTIN\Users` Read & execute (Settings → Diagnostics → **Open log folder**) |

`Wasla.PrintBridge.Service.exe secure-data` applies exactly this (as an administrator): it creates the folders, sets the owner, replaces the folder's access rules, and resets every existing file and subfolder to inherit them, so access a user had through `CREATOR OWNER` is removed. It is idempotent.

At every start the service checks the folder before it reads anything (`PrintBridgeDataFolderSecurity.Verify`): the owner must be `SYSTEM`, `Administrators` or the service, inheritance must be off, and no other account may have any access to the folder itself. If the check fails, the engine does not start; the app shows "The data folder of Print Bridge is not protected" and the log says which rule failed. Debug builds running against `WASLA_PRINTBRIDGE_DATA_ROOT` skip the check (that folder belongs to the developer).

Secrets: the device token was never encrypted at rest. No version of Print Bridge used DPAPI or any other per-user protection (`git log -S ProtectedData` and `-S DataProtectionScope` over the Print Bridge projects find nothing); `appsettings.json` and `pending-connection-change.json` hold their secrets as plain JSON values, protected only by the folder's permissions, which were too wide. It stays a plain value in `appsettings.json`, now readable only by `SYSTEM`, administrators and the service. Machine-scope DPAPI would add nothing against another local account that can read the file, and user-scope DPAPI under the virtual account would bind the token to that account's profile and break a rollback. The token never leaves the service except to the Wasla server in `X-PrintBridge-Token`: no IPC response, event, log line or error carries it.

**Migrating an existing installation.** `secure-data` changes permissions only, never content: the existing token, installation id, printer, history and a pending move are read by the service as they are, so a paired installation keeps printing without pairing again. Its effects and failure behavior, each checked by `PrintBridgeWindowsServiceTests` on a folder with ProgramData's inherited rules and a paired settings file:

| Situation | Result |
|-----------|--------|
| Service started before `secure-data` | The service runs, refuses to start its engine, logs the rule that failed, and the app shows "The data folder of Print Bridge is not protected" (`DataFolderNotSecured`). No request is sent with the token. |
| `secure-data` by a standard user | Refused (access denied); the folder's rules are unchanged. |
| `secure-data` by an administrator | Owner Administrators, inheritance removed, SYSTEM/Administrators full, service Modify, `logs` also readable by Users; existing files reset to inherit. |
| `secure-data` again | No change (idempotent). |
| The service account cannot read the folder's rules | Treated as unsafe (fail closed), never as safe. |
| A standard user afterwards | Cannot read `appsettings.json` (the token); can read the service log. |
| An administrator afterwards | Full access, for support and recovery. |
| Rollback to a build without the service | `icacls "<folder>" /reset /T /C` restores ProgramData's inherited rules. |

Logs: the service writes `logs\wasla-print-bridge-<yyyyMMdd>.log`; the app writes its own log per user to `%LOCALAPPDATA%\Wasla\PrintBridge\logs\wasla-print-bridge-app-<yyyyMMdd>.log`. Test-mode receipts (`logs\test-receipts`) contain receipt text, as before, and are readable by local users through the `logs` rule.

## Command line and endpoints

```text
Wasla.PrintBridge.Service.exe [secure-data] [--service-name <name>] [--data-root <absolute path>]
```

| Data folder | Pipe | Engine lock |
|-------------|------|-------------|
| `C:\ProgramData\Wasla\PrintBridge` (no `--data-root`) | `WaslaPrintBridge.Service` | `Global\WaslaPrintBridge` |
| any other folder | `WaslaPrintBridge.Service.<16 hex of SHA-256 of the folder>` | `Global\WaslaPrintBridge.<same 16 hex>` |

The names follow the folder (`PrintBridgeServiceEndpoint`), so two engines never use one folder and a test instance never takes the production names. `--service-name` is the Service Control Manager name the app verifies (default `WaslaPrintBridge`). Only the service's registered command line, which administrators own, or a developer's console passes these options. Without the Service Control Manager the executable runs only with a folder of its own (`--data-root`, or a Debug build's `WASLA_PRINTBRIDGE_DATA_ROOT`) and then skips the data folder check; it refuses to run as a console program on the installed folder.

## Engine exclusivity

Exactly one engine may poll and print per computer:

1. **Machine-wide engine lock** `Global\WaslaPrintBridge`, the lock WAS-92 introduced. The service creates it with access for `SYSTEM`, administrators and itself only. It counts as held only when the service created it; a lock that exists already (an older Print Bridge app that still runs its own engine, or any process holding the name) means another engine may be running. The service then does not build its engine, reports "Another Print Bridge is running on this computer" to the app, logs it, and tries again every 5 seconds. When that process exits, the service takes over. An older Print Bridge app started after the service finds the lock held (or not openable) and starts no engine, as WAS-92 defines.
2. **The app has no engine.** It never takes the engine lock and has no runtime, HTTP client or printer in its composition. Opening several app windows (several users or sessions) gives several clients of the one engine.
3. **Inside the service** the engine is one `PrintBridgeRuntime`; `Start` is idempotent and a new polling loop waits for the previous one to end (WAS-56).

A process that holds `Global\WaslaPrintBridge` without being Print Bridge keeps the service from printing (denial of service), never from being safe: no second engine runs. That needs a local logon, and the service log names the condition.

## IPC contract (protocol version 1)

Transport: one named pipe, `\\.\pipe\WaslaPrintBridge.Service`, byte mode, overlapped, up to 8 simultaneous clients. Each message is a frame: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.

| Limit | Value |
|-------|-------|
| Frame from the app | 1 B – 16 KiB |
| Frame from the service | 1 B – 1 MiB |
| Hello | must arrive within 5 s of connecting |
| Requests in flight per client | 8 (a ninth is answered `busy`) |
| Request id | `^[A-Za-z0-9][A-Za-z0-9-]{7,63}$` |

### Who may connect

- **Pipe access rules** (set by the service when it creates the pipe): `SYSTEM`, administrators and the service SID full control; `NT AUTHORITY\INTERACTIVE` read and write (no right to create pipe instances, so no other process can add an instance of this pipe); `NT AUTHORITY\NETWORK` denied. The service creates the first instance with `FirstPipeInstance`, so it never joins a pipe someone else created first; it logs and retries.
- **The service checks each client** right after it connects, before reading anything, by identifying (not impersonating) the client's token: it must belong to an interactive logon (`INTERACTIVE`) or to an enabled member of `Administrators`, and must not be a network logon. Anything else is disconnected. The user SID and session id are kept for the connection and logged (no user name).
- **The app checks the service** before it sends anything: the pipe's server process id (`GetNamedPipeServerProcessId`) must equal the process id the Service Control Manager reports for `WaslaPrintBridge` (`QueryServiceStatusEx`), and the service must be running. Only administrators can register that service, so a process that created the pipe name first is never mistaken for the service and never receives a setup link or a typed token. The app connects with identification-level impersonation only. A Debug app running against `WASLA_PRINTBRIDGE_DATA_ROOT` talks to that folder's pipe (below) and checks that its owner is the current user instead (`PipeOptions.CurrentUserOnly`).

**Security boundary.** May use the app's commands: any account signed in interactively on the computer (console, fast user switching or Remote Desktop: the logon carries `INTERACTIVE`), in any session, and enabled administrators. May not: network logons (refused by the pipe's rules before anything is read, and again by the token check), anonymous connections, and service or batch logons of non-administrators (no `INTERACTIVE`). Remote machines cannot connect. Verified on the GitHub-hosted runner: a second, standard account with an interactive logon gets `welcome`; the same kind of account with a network logon cannot open the pipe. Each app gets its own consent and verification ids, so one user's app cannot answer another's move confirmation or apply another's checked connection.

This replaces WAS-92's "not prevented" case for the engine: a squatter can no longer receive setup links meant for Print Bridge, because the app hands links only to the verified service. Between app instances of the same user and session, setup links are still forwarded over WAS-92's per-user pipe, unchanged.

**Authorization across users.** Every authorized client may read the state and use every command: before WAS-61 any local user could start Print Bridge and change its connection, printer and settings, and the files were readable by all users; now they reach the engine only through these validated commands. A setup link's confirmation (a move to another restaurant) is asked only of the client that submitted the link, in that user's session: its id lives in that connection, so another client's answer is refused (`consent_not_found`), and a disconnect is a refusal. A verified connection (`connection.check`) can likewise be applied only by the client that checked it.

### Handshake and version

The first frame from the app is `{"type":"hello","protocol":1,"client":"<app version>"}`. The service answers `{"type":"welcome","protocol":1,"service":"<service version>"}` and then a `state` event, or `{"type":"error","code":"incompatible_protocol","min":1,"max":1}` and closes the pipe. The app then shows "Print Bridge app and service versions do not match" (`RuntimeIssue.ServiceIncompatible`) and asks again every 30 seconds, so an update of either side is picked up. A future protocol version may change any message; a service that supports several versions announces its range in `min` and `max`.

### Messages

Request: `{"type":"request","id":"<id>","command":"<command>","payload":{…}}`. Response: `{"type":"response","id":"<id>","ok":true,"payload":{…}}`, or `"ok":false` with `"error":{"code","key","args","issue","status"}`. Event: `{"type":"event","event":"<event>","payload":{…}}`. Unknown or duplicate properties, wrong types, out-of-range values, unknown commands and malformed JSON are rejected (`malformed`, `unknown_command`); a frame of the wrong size or that is not UTF-8 ends the connection. Nothing from a payload is logged; only the command name, the outcome and the length.

| Command | Payload | Answer payload |
|---------|---------|----------------|
| `state.get` | `{}` | the state (below) |
| `engine.start` | `{}` | `{}`; clears "stopped by the user" |
| `engine.stop` | `{}` | `{}`; stops printing and remembers it (below). The app sends it only while printing runs |
| `connection.test` | `{}` | `{"tenantName","deviceName","serverTimeUtc"}` |
| `connection.check` | `{"serverUrl","token","keepSavedToken"}` | `{"verificationId", health}`; nothing saved |
| `connection.apply` | `{"verificationId","startListening"}` | `{"change"}` (`PrintBridgeConnectionChange`) |
| `connection.reset` | `{}` | `{}` |
| `printer.test` | `{}` | `{}` |
| `printers.list` | `{}` | `{"printers":[…]}` as the service sees them |
| `printer.save` | `{"name"}` | `{"errorKey"}` (null when saved) |
| `settings.save` | `{"testMode","idlePollSeconds","busyPollSeconds","errorPollSeconds"}` | `{"errorKey"}` |
| `language.save` | `{"culture"}` | `{}` |
| `history.query` | `{"range","search","page","pageSize"}` | `{"total","page","rows":[…]}` |
| `history.reprint` | `{"jobId"}` | `{"messageKey","newPrintJobId"}` |
| `history.resolve` | `{"jobId","printed"}` | `{}`; the user's check of a receipt whose paper outcome is unknown (below) |
| `setup.apply` | `{"link"}` | `{"outcome"}` (`PrintBridgeAutoSetupOutcome`) |
| `setup.consentAnswer` | `{"consentId","approved"}` | `{}` |

| Event | Payload |
|-------|---------|
| `state` | Sent after the handshake, after every state change (coalesced to one per 250 ms) and before the answer of every command that changes something |
| `setup.consent` | `{"consentId","currentTenantName","currentServer","targetTenantName","targetServer"}`, only to the client whose `setup.apply` asked |
| `setup.background` | `{"outcome"}` when a pending move finished without a link being open |

The state: `availability` (`active`, `starting`, `engineHeldElsewhere`, `dataFolderNotSecured`), the engine status (running, connection, last issue as code + resource key + numeric arguments, printer health, today's counts, the 50 most recent jobs without error text, the server-assigned device name, the service version) and the saved settings without secrets (`serverUrl`, `hasSavedToken`, printer, test mode, poll intervals, language, `printerUsable`, `stoppedByUser`). History rows carry a failure category (`printer`, `server`, `other`) instead of error text.

`connection.check` is the only message that carries a credential, from the app to the service, once; the service keeps the verified candidate for 5 minutes under a random `verificationId` bound to that client, and `connection.apply` refers to it. `keepSavedToken` checks the saved token without the app ever knowing it.

### Reconnect

The app connects at start and again after any disconnect: after 0.5 s, then doubling to 5 s, until the service answers. While disconnected the tray shows the connection-lost icon, the app window shows "The Print Bridge service is not running" with no actions, and a command fails with "The Print Bridge service could not be reached" (`RuntimeIssue.ServiceUnavailable`). A command whose answer was lost with the connection is reported as failed; its effect, if any, is visible in the next state. Nothing is repeated automatically.

## Lifecycle

| Event | What happens |
|-------|--------------|
| Service starts | Checks the data folder → starts the pipe → takes the engine lock (or waits) → loads settings, history and the journal → recovers the journal (below) → finishes a pending move → starts printing when a connection and a printer are saved and printing was not stopped by the user |
| App starts | Takes its per-user instance lock (WAS-92), registers the protocol handler, connects to the service. Without a saved connection it opens the app window on Overview, as before |
| App window closed | Hidden to the tray, as before |
| Tray **Close app** (was Exit) | Closes the app only. Printing and acknowledgements continue in the service. A move confirmation still open is closed as a refusal; a command already sent finishes in the service |
| App crashes or is killed | Same as closing; the service notices the closed pipe |
| **Stop** in the app (action bar) | `engine.stop`: stops printing (WAS-56: a claimed job is finished, Stop waits up to 10 s) and saves `PrintBridge.PrintingStoppedByUser = true`, so a service restart or a reboot does not start printing again |
| **Start** in the app | `engine.start`, clears the flag. A setup link or a connection change that starts printing clears it too |
| Service stops (`sc stop`, shutdown) | Stops the engine as Stop does (10 s grace), records what a claimed job still lacked in the journal, closes the pipe, releases the lock. The flag is not changed, so the next start prints again |
| Service crashes | The Service Control Manager restarts it (5 s, 10 s, 60 s); the journal decides what happens to a job that was in progress |

## Print journal and crash recovery

A job claimed by the engine but not yet reported is in `print-journal.json` (atomic writes, in the protected folder). Each entry has the job id, the stage, the server URL and a SHA-256 fingerprint of the token that claimed it (never the token).

| Stage | Written | Means |
|-------|---------|-------|
| `Claimed` | after `mark-printing` succeeded | The receipt has not been handed to Windows yet |
| `Submitting` | right before the spooler call (or the test-mode file write) | Windows may or may not have printed it |
| `Uncertain` | when a later start finds `Submitting` | Waiting for the user's check (never removed on its own) |
| `PrintedUnreported` | after the spooler accepted the receipt | Printed; `mark-printed` not yet confirmed |
| (removed) | after `mark-printed` or `mark-failed` was answered | Finished |

At the next engine start (after a crash, a kill, a stop that outlasted its 10-second grace, or a power loss after the write), before the first poll:

| Stage found | Action | Why |
|-------------|--------|-----|
| `Claimed` | `mark-failed` ("Print Bridge stopped before printing this receipt") to the server that claimed it | Nothing was printed; the job becomes Failed and can be printed again from Wasla |
| `Submitting` | Becomes `Uncertain`: never printed again, never reported on its own. The app counts it on Overview ("Receipts to check") and shows it in History as "Needs checking" with **It printed** / **It did not print** (each confirmed inline). The answer is reported with the claiming connection: printed → `mark-printed`; not printed → `mark-failed`, so Wasla can print it again | Whether paper came out is unknown: printing it again could print it twice, and either report would be a guess. Only the person at the printer knows |
| `PrintedUnreported` | `mark-printed` repeated, as the WAS-56 retries do (a repeat is answered `skipped`) | Printed once; only the report was missing |

The journal is settled before new jobs are claimed. The first cycle after a start (service start, Start, a new connection) settles every entry at once; later cycles repeat an entry whose report failed only after one minute (`JournalRetryInterval`), so a report that failed while the service kept running is still finished without a restart. An entry is reported only when the saved server URL and token still match its fingerprint; after a connection change it is dropped with a warning (the old token is gone, and WAS-58 forbids reporting with another one). An uncertain job of a replaced connection stays visible in History, without the two actions, saying to check it in the Wasla panel. A report that fails for a network or server reason (unreachable, 5xx, a redirect) stays in the journal and is tried again on the next cycle; the cycle still polls, so a failing report endpoint never stops printing. Physical printing is never claimed to be exactly-once: spooler acceptance is not paper, and a crash inside the spooler call is reported as unknown.

## Installer handoff (WAS-62)

The setup (WAS-62, [print-bridge-installer.md](print-bridge-installer.md)) applies exactly these settings and the rules below. The commands remain for support (the support ZIP) and development: an elevated PowerShell or command prompt, with the binaries in `C:\Program Files\Wasla\Print Bridge\` (adjust the path):

```cmd
sc.exe create WaslaPrintBridge binPath= "\"C:\Program Files\Wasla\Print Bridge\Wasla.PrintBridge.Service.exe\"" obj= "NT SERVICE\WaslaPrintBridge" start= auto depend= Spooler DisplayName= "Wasla Print Bridge"
sc.exe description WaslaPrintBridge "Prints Wasla receipts on this computer."
sc.exe privs WaslaPrintBridge SeChangeNotifyPrivilege
sc.exe failure WaslaPrintBridge reset= 86400 actions= restart/5000/restart/10000/restart/60000
sc.exe failureflag WaslaPrintBridge 1
"C:\Program Files\Wasla\Print Bridge\Wasla.PrintBridge.Service.exe" secure-data
sc.exe start WaslaPrintBridge
```

| Task | Command |
|------|---------|
| Status and process id | `sc.exe queryex WaslaPrintBridge` |
| Stop (printing stops; the app keeps running and shows the service as stopped) | `sc.exe stop WaslaPrintBridge` |
| Start | `sc.exe start WaslaPrintBridge` |
| Remove | `sc.exe stop WaslaPrintBridge`, then `sc.exe delete WaslaPrintBridge` |
| Give the data folder back to ProgramData's defaults (rollback to a build without the service) | `icacls "C:\ProgramData\Wasla\PrintBridge" /reset /T /C` |

Rules for the installer:

- Install both executables and `shell-ui\` in one folder that only administrators can write (Program Files). The service account needs Read & execute there, which Program Files grants to `Users`.
- Stop and remove an older Print Bridge app that runs its own engine (any build before WAS-61) before starting the service; until it exits, the service waits (engine lock). Close the app (`Wasla.PrintBridge.exe`) before replacing it.
- Run `secure-data` after `sc create` (the account must exist) and before `sc start`; on an update run it again.
- Start the tray app at sign-in for every user: the setup's `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` entry passes `--startup`, so a second start in the same session ends quietly. The app does not read `Ui.StartWithWindows`; startup is the installer's.
- Uninstall: stop and delete the service, close the app, remove the files. Keep `C:\ProgramData\Wasla\PrintBridge` unless the user asks to remove the pairing (it holds the token, the installation id and history).
- Updates (WAS-63) replace both executables together: the app and the service must speak the same protocol version, and a mismatch is shown, never guessed around.

## Developer validation

Two levels, reported separately:

1. **Two processes, one user (any developer machine, not elevated).** A Debug build of the service runs as a console program with its own data root, and the app (or the test client) connects to it over the real pipe:

   ```powershell
   $env:WASLA_PRINTBRIDGE_DATA_ROOT = "$env:TEMP\wasla-pb-dev"
   dotnet run --project src/Wasla.PrintBridge.Service -c Debug
   # second terminal, same variable:
   dotnet run --project src/Wasla.PrintBridge -c Debug
   ```

   Seed the root with `DryRun: true` and a fake API (see [print-bridge.md](print-bridge.md)); never a real device token or a physical printer. `PrintBridgeServiceProcessTests` run this boundary automatically (any build): they start the built service executable as a separate process with `--data-root`, drive it through the production client, kill it between a print and its report, start a second process on the same folder, and hold its engine lock as an older app would.

2. **The real Windows Service (elevated, disposable machine).** `PrintBridgeWindowsServiceTests` register a uniquely named instance of the built service executable with the production `sc.exe` settings above (its own name, virtual account, pipe, engine lock and data folder, given by `--service-name` and `--data-root` in the service's command line), run `secure-data` and start it, then check through the production client. Two tests, so that a printer problem cannot hide the other results:
   - `TheRealService_MigratesAnExistingInstallation_RunsAsItsVirtualAccount_ServesOnlyLocalUsers_AndRecovers` (test mode): the migration table above on a folder with ProgramData's rules and a paired settings file, the account the service runs as, printing and acknowledgement with the existing token, that closing the client does not stop printing, a second standard account with an interactive logon (`welcome`) and with a network logon (refused), what each can read, a kill followed by the recovery restart that reports a printed job once, graceful stop, a held engine lock, administrator access and the `icacls` rollback.
   - `TheRealService_PrintsToAMachinePrinterInSession0_AndNeverToAPrinterItCannotSee` (test mode off): the printer checks in [Supported printer installation](#supported-printer-installation), with a disposable "Generic / Text Only" printer on a loopback TCP/IP port.

   They run on GitHub-hosted runners (elevated, disposable) in every CI scope and skip elsewhere (CI fails on a skipped test); each step that passed is written to the test output in the TRX file. To run them on your own disposable VM (they create and delete local accounts, a service and a printer), run the Print Bridge tests elevated with `GITHUB_ACTIONS=true` and `RUNNER_ENVIRONMENT=github-hosted` set:

   ```powershell
   $env:GITHUB_ACTIONS = "true"; $env:RUNNER_ENVIRONMENT = "github-hosted"
   dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj -c Release --filter "FullyQualifiedName~PrintBridgeWindowsServiceTests|FullyQualifiedName~SetupInstanceCrossAccountTests"
   ```

Not covered by either: a physical receipt printer, a real per-user printer connection (needs a second computer, above), Windows shutdown with a job in progress, a reboot and a domain-joined machine (WAS-28). The installer has its own tests ([print-bridge-installer.md](print-bridge-installer.md#verification)).
