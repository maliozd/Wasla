# Wasla Print Bridge updates and rollback (WAS-63)

How an installed Print Bridge ([print-bridge-installer.md](print-bridge-installer.md), WAS-62) moves to a newer release:
the user asks for it and confirms it in the app, the service downloads and verifies the setup and pauses printing at a
safe point, the user runs the verified setup, and Windows Installer either commits the new version after a health check
or rolls back to the installed one. Nothing is installed in the background, and nothing is installed without the user's
confirmation and an administrator's permission.

Related: the service and its IPC ([print-bridge-service.md](print-bridge-service.md), WAS-61), the app
([print-bridge.md](print-bridge.md)). Physical printers and client Windows certification are WAS-28.

## Roles

| Part | Owns |
|------|------|
| Service (`NT SERVICE\WaslaPrintBridge`) | Asking the Wasla server for the current release, downloading it, verifying it, pausing printing (the update gate), the durable update state, reconciliation after a restart, and the health answer the installer checks. It holds the device token and the data folder, so the app never needs either |
| App (the signed-in user) | Showing the installed and available version and progress, the confirmation, verifying the staged setup again and starting it, and cancelling when the setup ends without installing. It cannot elevate anything itself |
| Setup (WAS-62: Burn + MSI) | Administrator permission (UAC), closing the apps, stopping and replacing the service, the post-start health check, and rollback. It is the only installation mechanism; the standalone setup and Repair work as before |
| Wasla server (`Wasla.Web`) | Describing and serving the setup it already offers for download, to an authenticated device |

## Trust model

An update is installed only when all of these hold. Each is checked by the service after the download, and the last two
again by the app immediately before it starts the setup:

1. **Transport.** The release description and the setup come from the saved `ServerUrl` with the device token
   (`X-PrintBridge-Token`), through the same client as every device request: redirects are never followed (WAS-191).
   The token never appears in a URL, a command line or a log.
2. **Integrity.** The file has exactly the size and SHA-256 the release description gives. This proves the file is the
   one described, not who made it: the description comes from the server, which is not the authority on code.
3. **Authenticity.** The setup has a valid Authenticode signature (Windows' `WinVerifyTrust`, Authenticode policy:
   a code-signing certificate that chains to a trusted root, checked for revocation except the root), and its signer
   certificate's subject is exactly the subject of the certificate that signed the running service executable. The
   trust anchor is therefore the signed Print Bridge already installed: a release is accepted only from the publisher
   that signed this installation.
4. **Version.** The setup's own version (its version resource) equals the described version and is newer than the
   installed one. Older or equal versions are never offered.

Consequences:

- An unsigned installation (every development build) cannot update itself: the app says updates come with signed
  releases. Its setup, Repair and a newer setup run by hand work as before.
- No setting, file, environment variable or command-line option turns a check off. Tests use real signatures: CI signs
  its builds with a throwaway self-signed code-signing certificate that only the disposable runner trusts.
- The staged setup is in `C:\ProgramData\Wasla\PrintBridge\updates`, which local users can read and run but not change
  (below), so the file the app starts is the file the service verified. Windows' UAC prompt names the verified publisher
  as well.
- Changing the signing publisher's subject (a new legal name, for example) breaks the chain on purpose: installations
  signed by the old subject must be updated with the setup by hand.

## Release description (schema 1)

The packaging script's `Wasla.PrintBridge-package.json` already records the version, the signing state and each file's
size and SHA-256. The server reads it from the folder of the configured setup (`Wasla:PrintBridgeDownload`), so
publishing a release stays "put the setup and its metadata where the Web download serves them".

| Request (device token) | Answer |
|------------------------|--------|
| `GET api/print-bridge/releases/latest` | `200 {"schemaVersion":1,"version":"1.0.1","setup":{"fileName","bytes","sha256"}}`; `204` when there is no signed release to offer (no file, no metadata, an unsigned or unreadable package) |
| `GET api/print-bridge/releases/{version}/setup` | The setup's bytes; `404` unless `{version}` is the release currently described |

`schemaVersion` changes only with an incompatible change; a client refuses an unknown schema. Releases are offered only
when the metadata says `signed: true`; the client still verifies everything itself.

## Sequence

1. **Check.** The app asks the service (`update.check`). The service asks the server and compares versions.
2. **Confirm.** The app shows the installed and the available version and asks in a native dialog. It says that printing
   pauses during the installation, that Windows asks for administrator permission, that Print Bridge closes and that
   the setup opens.
3. **Prepare** (`update.prepare`, one attempt at a time for the whole computer; a second one is refused):
   the service first asks the server again (the release must still be the version the user confirmed), downloads to
   `updates\Wasla.PrintBridge-Setup-x64-<version>.exe.partial`, refuses anything larger or smaller than described,
   renames the finished file, and verifies it (trust model above) while holding it open against writers.
4. **Gate.** The service then takes the engine's connection-change lease (`IPrintBridgeConnectionGuard`): it is granted
   only when no job is between its claim and its last report and no journal entry is being reported again, and while it
   is held no job is claimed, no journal entry is settled, and no setup link or pending move is applied. It waits for
   that up to 2 minutes; a job is never cancelled, failed or cut short to make room, and if it does not finish in time
   the attempt fails with "printing is in progress". The attempt is written as `Ready`.
5. **Install.** The app verifies the staged setup again and starts it (unelevated; the setup asks for administrator
   permission). The setup closes every Print Bridge app, stops the service, removes the installed version and installs
   the new one in one Windows Installer transaction, starts the new service and runs the health check (below).
6. **Commit.** The new service finds the attempt `Ready` at start and does not start its engine. Once no Windows
   Installer transaction is running (`Global\_MSIExecute` is free) and Windows Installer reports its own version as
   installed (the related product of the MSI's upgrade code), it records `Succeeded`, deletes the staged setup and starts
   printing, unless the user had stopped printing. If no installation runs and Windows Installer reports another
   version for 30 seconds, it records `Failed` (`unexpectedVersion`) and prints anyway, so a damaged registration never
   keeps a restaurant from printing.
7. **Result.** The app (started by the setup's **Launch**, the Start menu or the next sign-in) shows "Updated to X", or
   "The update was not installed; X is still installed" with **Try again**.

If the setup ends without installing (the user cancelled it or the UAC prompt), the app that started it cancels the
attempt and printing resumes at once. If no app does (it was closed), the gate expires 15 minutes after `Ready` while the
installed version is still running. A setup started after that stops the service as any installation does, which WAS-61
already makes safe. The app disconnecting before `Ready` cancels the attempt; after `Ready` it does not (the setup
closes the app on purpose).

The staged setup is deleted after the commit, a rollback, a cancel or an expiry. The setup that ran from it keeps the
file in use until it exits, so the service tries again every 5 seconds for up to 10 minutes, and otherwise at its next
start.

## Printing safety

- The gate and the check for a job in progress are one decision under the engine's lock, so a job cannot start between
  "idle" and "paused".
- A job in progress finishes normally before the gate is granted. Entries the journal keeps across a restart stay as they
  are: `Printed` (the report is still owed) is reported by the new version after the commit; `Uncertain` waits for the
  user's answer and is never printed again. Nothing is marked failed and nothing is discarded to allow an update. A
  receipt still in the Windows print queue (WAS-216, `Submitting` with its Windows jobs) stays there: the new version
  keeps watching it, and a version without WAS-216 (a rolled-back update) reads it as interrupted and shows it as needing
  a check, never printing it again.
- The user's **Stop** (`PrintBridge.PrintingStoppedByUser`) is a setting the update never writes. After a successful,
  failed or interrupted update the service prints again exactly when it would have printed before it.
- Physical printing stays at-least-once-or-uncertain, as WAS-61 defines it. An update adds no new way to print a receipt
  twice or to lose one.

## Health check

The MSI runs `Wasla.PrintBridge.Service.exe health-check` as LocalSystem right after it started the service, inside the
installation transaction (deferred, `Return="check"`, not on uninstall). It passes only when the service:

- is the process the Service Control Manager reports for `WaslaPrintBridge` (checked on the pipe before anything is
  sent);
- answers within 60 seconds with this build's IPC protocol (`PrintBridgeIpcProtocol.Version`);
- reports the version of the executable that was just installed;
- serves its engine: its data folder is protected (`DataFolderNotSecured` fails at once); an engine held by an older
  portable app still counts as healthy, as WAS-61 reports it.

Any other answer, or none, fails the installation and Windows Installer rolls everything back. The check needs neither
the internet, a connection nor a printer, so a first installation passes it too.

## Rollback and the downgrade rule

- **Before the commit, rollback is Windows Installer's own.** The major upgrade removes the installed version inside the
  same transaction (`RemoveExistingProducts` after `InstallInitialize`, WAS-62). Any failure up to the end of that
  transaction restores the previous files, service registration and start state and starts the previous service: a file
  that cannot be written, a service that does not start, a failed health check. The downgrade refusal is untouched
  because a rollback is not an installation of an older version.
- **The restored service keeps its least privilege and recovery.** Windows Installer creates the deleted service again
  from its basic registration only (account, start type, dependencies, description, permissions). The required
  privilege (`SeChangeNotifyPrivilege` only) and the recovery actions are set with `sc.exe` (WAS-61, WAS-62), so until
  WAS-63's CI found it the restored service started with every privilege of its account and no restarts. Each version's
  MSI has rollback actions on its uninstallation, between `StopServices` and `DeleteServices`, that run the same
  `sc.exe` commands again; in the rollback they run after the service exists again and before it starts. A version
  restores its own settings, so this holds for an update from any version that has them, which every released setup does
  (none was released before WAS-63).
- **No newer data before the commit.** Until the commit the new version holds the gate: it claims nothing, settles no
  journal entry, finishes no pending move and writes no settings. It writes only its log and the update state. So a
  rolled-back update leaves the data folder as the previous version left it, and that version never reads data a newer
  version wrote. A future data-format change must migrate only after the commit.
- **After the commit there is no automatic downgrade.** The installer still refuses older versions. Recovery from a
  committed release that misbehaves is the setup's Repair or a newer release.

## Durable state and reconciliation

`updates\update-state.json` (written atomically by the service, schema 1): attempt id, from and to version, the setup's
size and SHA-256, the stage, timestamps and, on failure, a reason code. No token, URL or path is stored.

At every service start, before printing starts:

| Stage found | This executable's version | Result |
|-------------|---------------------------|--------|
| `Downloading`, `Verifying`, `WaitingForPrinting` | any | Interrupted: the partial or staged file is deleted, no gate, `Failed` (`interrupted`) |
| `Ready` | the target version | Keep the gate until the commit (above), then `Succeeded` |
| `Ready` | the previous version | The setup did not run, was cancelled or rolled back: gate released, `NotApplied` |
| `Ready` | another version | Gate released, `Failed` (`unexpectedVersion`); logged |
| `Succeeded`, `Failed`, `NotApplied`, `Cancelled` | any | Kept and shown until the next attempt; no gate |
| No record, or an unreadable one (set aside as `update-state.json.corrupt-*`) | any | No attempt; no gate |

In every row but the second, a staged or partial setup left in `updates\` is deleted (retried while in use, above).

Failure behavior at each point:

| Interruption | Outcome |
|--------------|---------|
| Cancel before the setup starts | Download stopped or staged file deleted, gate released, `Cancelled` |
| Download interrupted (network, service stop, crash, reboot) | Partial file deleted at the next start; try again from the beginning |
| Verification fails | File deleted, never run, `Failed` with the check that failed (support detail in the log) |
| Printing does not finish within 2 minutes | No gate, `Failed` (`printingInProgress`); try again later |
| Setup cancelled, UAC refused | The app cancels; or the gate expires after 15 minutes |
| Installation fails, health check fails | Windows Installer rolls back; the previous service starts and records `NotApplied` |
| Interrupted installation or rollback (power loss) | Windows Installer rolls back the unfinished transaction when it next runs (the setup again, or Repair); the service then reconciles as above. If the new version starts on an uncommitted installation with no installer running, it prints after 30 seconds and records `unexpectedVersion`. Not tested: no real power loss or reboot is simulated |

What is tested where is listed under [Verification](#verification). An interruption or reboot in tests is a service stop
or process kill and restart; a real machine reboot or power loss is not tested.

## IPC protocol

The update adds commands and a state field, and the WAS-61 parser rejects unknown fields, so WAS-63 raised the protocol
to 2; removing the device-limit outcome raised it to 3 ([print-bridge-service.md](print-bridge-service.md#handshake-and-version)).
The service accepts only its own version. An app of another version (an older app still open, or a portable copy) gets
`incompatible_protocol` and shows "Print Bridge app and service versions do not match"; closing it and opening Print
Bridge again from the Start menu fixes it. The setup closes every Print Bridge app before it replaces anything, replaces
the app and the service in the same installation, and its rollback restores both, so a normal update or a rolled-back
one never shows this.

| Command | Payload | Answer |
|---------|---------|--------|
| `update.check` | `{}` | `{"installed","available"}` (`available` null when there is nothing newer) or a refusal |
| `update.prepare` | `{"version"}` | `{"attemptId"}` at once; progress and the result arrive in `state` |
| `update.cancel` | `{"attemptId"}` | `{}`; nothing changes when that attempt is finished or awaits its commit |
| `service.health` | `{}` | `{"version","availability"}`; used by `health-check` |

Refusals are localized keys: `Update.Error.NotSigned` (unsigned installation), `NotReady`, `NotNewer`,
`InvalidRelease` and `Storage`, and `busy` for a second attempt. While printing is paused for an update (from
`WaitingForPrinting` until the commit), commands that change the connection, settings or printing (Start, Stop, a
setup link, a printer or settings save, the language, resolving an uncertain receipt) are refused with
`Update.Error.InProgress`; reading state, history and printers works.

The `state` gains `update`: the installed version, whether updates are supported (a signed installation), and the
current or last attempt: its id, stage, from and to version, download percent, failure code and, only while `Ready`
(not while the new version awaits its commit), the staged setup's path, size and SHA-256 for the app's own check.

## Permissions

- Updating needs administrator permission (UAC), like installing and repairing. A user without it gets the UAC
  credential prompt; printing resumes when the setup is cancelled.
- `updates\` is created by `secure-data` (as an administrator): owner Administrators; `SYSTEM` and Administrators Full,
  the service Modify, `BUILTIN\Users` Read & execute. Users can start the staged setup but cannot replace it.

## External inputs for a signed release

- A code-signing certificate (OV or EV) for the publisher, in the build machine's certificate store, with its SHA-1
  thumbprint, and an RFC 3161 timestamp URL: `package-print-bridge.ps1 -SigningCertificateThumbprint … -TimestampUrl …`
  (WAS-62). signtool from the Windows SDK. Nothing secret is in the repository.
- Every release must be signed by the same certificate subject as the installation it updates.
- Publishing: put the signed setup and its `Wasla.PrintBridge-package.json` in the configured download folder.

## Verification

Exact runs, totals and evidence lines are in the WAS-63 pull request.

| What | Where | How |
|------|-------|-----|
| Install, update, health check and commit, and a failed update rolled back, with the real setups | `PrintBridgeInstallerTests.AConfirmedUpdate_…` (CI, disposable elevated runner only) | This commit's test-signed setup and the next build: discovery from a fake server, a tampered download refused, the pause waiting for a claimed receipt, the staged file's access rules, `FailWhenDeferred=1` rolled back to the installed version (data and the receipt held by the pause intact; the new version never contacted the server; the service's whole configuration as `sc.exe` reports it, privileges and recovery included, identical to the installed one's), then a committed update keeping the data, the user's Stop, `StartAtSignIn=0` and that same service configuration. The failure is induced; no machine reboot |
| Reconciliation table, gate, expiry, cancel, disconnect, concurrent prepares, interrupted download, every refusal | `PrintBridgeUpdateCoordinatorTests` | Production engine, pipe and client in one process; fake server and fake Windows Installer; real Authenticode on a Microsoft-signed file of the .NET runtime (the installation is given Microsoft's publisher). A "restart" builds a new service on the same folder |
| Health check | `PrintBridgeServiceProcessTests`, `PrintBridgeInstallerSourceTests` | The built executable against a console service process; the custom action's place in the MSI |
| `updates\` rules | `PrintBridgeDataFolderSecurityTests` | Readable by users is safe; writable by users is not |
| Release versions, description and record | `PrintBridgeReleaseTests` in both test projects | Contract rules; the Web catalog and endpoints |
| App flow and languages | WebShell tests | Confirmation, progress, recovery texts in TR, EN, AR and RU, Arabic right to left. In the real WebView2 window at 320 CSS px, in all four languages, Settings → Updates in every state it shows (each attempt step, each result and failure text, and the app's own setup opening, open, closed and not opened): no sideways scrolling, nothing outside the card, cut or overlapping (`ShellWebViewRuntimeTests.MinimumWindow_…`) |

Not tested: a real machine reboot or power loss during an installation or rollback, physical printers and client
Windows editions (WAS-28), and a production-signed release (no certificate exists yet).
