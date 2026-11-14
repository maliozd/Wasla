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
   the service downloads to `updates\<version>.partial` while hashing, refuses anything larger than described, renames
   the finished file, and verifies it (trust model above).
4. **Gate.** The service then takes the engine's connection-change lease (`IPrintBridgeConnectionGuard`): it is granted
   only when no job is between its claim and its last report, and while it is held no job is claimed, the journal is not
   settled and a pending move is not finished. It waits for that up to 2 minutes; a job is never cancelled, failed or
   cut short to make room, and if it does not finish in time the attempt fails with "printing is in progress".
   The attempt is written as `Ready`.
5. **Install.** The app verifies the staged setup again and starts it (unelevated; the setup asks for administrator
   permission). The setup closes every Print Bridge app, stops the service, removes the installed version and installs
   the new one in one Windows Installer transaction, starts the new service and runs the health check (below).
6. **Commit.** The new service finds the attempt `Ready` at start and keeps the gate. Once no Windows Installer
   transaction is running (`Global\_MSIExecute` is free) and Windows Installer reports its own version as installed, it
   records `Succeeded`, releases the gate and prints again, unless the user had stopped printing.
7. **Result.** The app (started by the setup's **Launch**, the Start menu or the next sign-in) shows "Updated to X", or
   "The update was not installed; X is still installed" with **Try again**.

If the setup ends without installing (the user cancelled it or the UAC prompt), the app that started it cancels the
attempt and printing resumes at once. If no app does (it was closed), the gate expires 15 minutes after `Ready` while the
installed version is still running. A setup started after that stops the service as any installation does, which WAS-61
already makes safe.

## Printing safety

- The gate and the check for a job in progress are one decision under the engine's lock, so a job cannot start between
  "idle" and "paused".
- A job in progress finishes normally before the gate is granted. Entries the journal keeps across a restart stay as they
  are: `Printed` (the report is still owed) is reported by the new version after the commit; `Uncertain` waits for the
  user's answer and is never printed again. Nothing is marked failed and nothing is discarded to allow an update.
- The user's **Stop** (`PrintBridge.PrintingStoppedByUser`) is a setting the update never writes. After a successful,
  failed or interrupted update the service prints again exactly when it would have printed before it.
- Physical printing stays at-least-once-or-uncertain, as WAS-61 defines it. An update adds no new way to print a receipt
  twice or to lose one.

## Health check

The MSI runs `Wasla.PrintBridge.Service.exe health-check` as LocalSystem right after it started the service, inside the
installation transaction (deferred, `Return="check"`, not on uninstall). It passes only when the service:

- answers on its pipe within 60 seconds with IPC protocol 2;
- reports the version of the executable that was just installed;
- has its data folder protected, and its settings and journal loaded (not set aside as unreadable).

Any other answer, or none, fails the installation and Windows Installer rolls everything back. The check needs neither
the internet, a connection nor a printer, so a first installation passes it too.

## Rollback and the downgrade rule

- **Before the commit, rollback is Windows Installer's own.** The major upgrade removes the installed version inside the
  same transaction (`RemoveExistingProducts` after `InstallInitialize`, WAS-62). Any failure up to the end of that
  transaction restores the previous files, service registration and start state and starts the previous service: a file
  that cannot be written, a service that does not start, a failed health check. The downgrade refusal is untouched
  because a rollback is not an installation of an older version.
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
| `Succeeded`, `Failed`, `NotApplied`, `Cancelled` | any | Kept to show the result once; no gate |

Failure behavior at each point:

| Interruption | Outcome |
|--------------|---------|
| Cancel before the setup starts | Download stopped or staged file deleted, gate released, `Cancelled` |
| Download interrupted (network, service stop, crash, reboot) | Partial file deleted at the next start; try again from the beginning |
| Verification fails | File deleted, never run, `Failed` with the check that failed (support detail in the log) |
| Printing does not finish within 2 minutes | No gate, `Failed` (`printingInProgress`); try again later |
| Setup cancelled, UAC refused | The app cancels; or the gate expires after 15 minutes |
| Installation fails, health check fails | Windows Installer rolls back; the previous service starts and records `NotApplied` |
| Interrupted installation or rollback (power loss) | Windows Installer completes the rollback when it next runs; the service then reconciles as above |

What is tested where is listed under [Verification](#verification). An interruption or reboot in tests is a service stop
or process kill and restart; a real machine reboot or power loss is not tested.

## IPC protocol 2

The update adds commands and a state field, and the WAS-61 parser rejects unknown fields, so the protocol becomes 2 and
the service accepts only 2. An app of protocol 1 (an older app still open, or a portable copy) gets
`incompatible_protocol` and shows "Print Bridge app and service versions do not match"; closing it and opening Print
Bridge again from the Start menu fixes it. The setup closes every Print Bridge app before it replaces anything, so a
normal update never shows this.

| Command | Payload | Answer |
|---------|---------|--------|
| `update.check` | `{}` | `{"installed","available"}` (`available` null when there is nothing newer) or a refusal |
| `update.prepare` | `{"version"}` | `{}` at once; progress and the result arrive in `state` |
| `update.cancel` | `{}` | `{}` |
| `service.health` | `{}` | `{"version","dataFolderProtected","settingsLoaded","journalLoaded"}`; used by `health-check` |

The `state` gains `update`: the stage, the from and to version, download progress, a failure reason code and resource
key, and, only while `Ready`, the staged setup's path, size and SHA-256 for the app's own check.

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

Filled in as the work lands; see the PR for exact runs.
