# Wasla Print Bridge installer (WAS-62)

> **Distribution.** Builds from this repository are **unsigned development builds** unless the release pipeline signs them
> (below). Their product name says "(unsigned development build)" in the setup window and in Installed Apps, and their
> metadata says `"signed": false`. Do not give an unsigned build to a restaurant. Nothing in this repository publishes,
> deploys or distributes an installer.

## Purpose and scope

This document owns how Print Bridge is installed on a restaurant's Windows computer: the setup EXE and the MSI, what they
install and register, the order of a repair, an upgrade and an uninstall, the WebView2 Runtime, how they are built and
signed, and how the Web setup page offers them.

The service the installer registers, its account, the data folder's permissions and the app/service IPC are WAS-61's
([print-bridge-service.md](print-bridge-service.md)). The app itself is in [print-bridge.md](print-bridge.md). Update
discovery, controlled updates and rollback are WAS-63; physical receipt printers and clean-machine certification are WAS-28.

## Artifacts

`scripts/release/package-print-bridge.ps1` writes, under `artifacts/print-bridge/` (gitignored; never committed):

| File | For | Contents |
|------|-----|----------|
| `Wasla.PrintBridge-Setup-x64.exe` | Restaurants (the Web download) | WiX Burn setup: the WebView2 Runtime bootstrapper when needed, then the MSI |
| `Wasla.PrintBridge-x64.msi` | Administrators who deploy MSIs | The app, the service, their registration. It does not install the WebView2 Runtime |
| `Wasla.PrintBridge-win-x64-support.zip` | Development and support recovery only | The same program files, unpacked, and a README. Not an installation: the service has to be registered by hand ([print-bridge-service.md](print-bridge-service.md#installer-handoff-was-62)) |
| `Wasla.PrintBridge-package.json` | Release records | Product, version, signed or not, commit (and whether it had local changes), build time, size and SHA-256 of each file, the WebView2 bootstrapper's SHA-256 and signer |
| `SHA256SUMS.txt` | Verification | SHA-256 of each file (`sha256sum` format) |

## Technology and version decisions

- **WiX Toolset v7 (7.0.1)**: an MSI for installation identity, repair and major upgrades, wrapped in a Burn setup EXE that
  bootstraps the WebView2 Runtime. Projects: `installer/print-bridge/Package` (MSI) and `installer/print-bridge/Bundle`
  (setup EXE), built by the script, not part of `Wasla.sln`. WiX v3 to v5 no longer receive security fixes (since
  2026-02-05); v6 and v7 are distributed under FireGiant's Open Source Maintenance Fee EULA. Wasla's owner accepted the
  EULA for this repository on 2026-11-13 (`AcceptEula=wix7` in `installer/print-bridge/Directory.Build.props`); WiX v7
  does not build without it. Sponsorship under that EULA is the owner's matter.
- **One version** for the app, the service, the MSI and the setup EXE: `PrintBridgeVersion` in
  `src/Wasla.PrintBridge.Version.props` (major.minor.build within 255.255.65535; Windows Installer compares these three
  parts). The script's `-Version` overrides it for one build.
- **Upgrade codes** (never change): MSI `2DAEE4A2-EF79-45BD-B403-602FFC1E737E`, setup EXE
  `0CAF4165-E129-4DB9-A1DF-C719CFE65AF5` (`PrintBridgeInstalledSetup.SetupUpgradeCode`).
- **Self-contained Release `win-x64`** publish of both executables into one folder: no .NET runtime is needed on the
  computer. The service references the app's shared framework (`Microsoft.WindowsDesktop.App.WindowsForms`) so both run
  on the same runtime files; the script stops if the two publishes ever carry different files under one name.

## What the setup installs

| Item | Value |
|------|-------|
| Program folder | `C:\Program Files\Wasla\Print Bridge` (per machine; writable by administrators only): both executables, `shell-ui\`, the runtime |
| Service | `WaslaPrintBridge`, "Wasla Print Bridge", `NT SERVICE\WaslaPrintBridge`, automatic start, depends on `Spooler`, `SeChangeNotifyPrivilege` only, restart after 5 s, 10 s and 60 s (failure count reset after one day), also after an error exit. The privileges and recovery settings are the WAS-61 `sc.exe` commands, run by the setup as LocalSystem (WiX advises against MSI's own service configuration tables) |
| Data folder | `Wasla.PrintBridge.Service.exe secure-data`, as LocalSystem, after the service exists and before it starts, at every install, repair and upgrade. Permissions only; content is never changed |
| App at sign-in | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`, "Wasla Print Bridge" = `"…\Wasla.PrintBridge.exe" --startup`, for every user (feature `StartAtSignIn`) |
| Setup links | `wasla-printbridge://` for every user under `HKLM\Software\Classes` (`"…\Wasla.PrintBridge.exe" "%1"`) |
| Start menu | "Wasla Print Bridge" in the all-users Start menu. No desktop shortcut |
| Installed Apps | One entry, the setup EXE's (version, Modify → Repair, Uninstall). The MSI is hidden behind it |
| WebView2 Runtime | Installed by the setup EXE when missing (below) |

**Startup ownership.** The service never starts the app. Explorer starts the app at each user's interactive sign-in from
the Run entry, in that user's session and without elevation. `--startup` makes a start that finds this user's app already
running in this session end quietly instead of saying "already running" (`PrintBridgeSignInStart`); the per-user,
per-session instance lock (WAS-92) decides. So there is one engine per computer (the service) and at most one app per user
and session. A user can turn the start off in Settings → Apps → Startup; an administrator can install without it
(`StartAtSignIn=0` for the setup EXE, `START_AT_SIGN_IN=0` for the MSI). On the last page of an interactive install,
**Launch** starts the app as the signed-in user, not elevated.

**Setup links.** The installed app writes no per-user handler: one under `HKCU\Software\Classes` would hide the computer's
handler from that user and outlive an uninstall. At its first start it removes a per-user handler an earlier (portable)
Print Bridge wrote; another program's per-user handler is left alone and logged (`PrintBridgeProtocolRegistrar`). A
development or support copy, without the setup's registration, still registers itself per user as before.

## Order of operations

Install, repair and upgrade (MSI `InstallExecuteSequence`):

1. A major upgrade removes the installed version first, inside the same transaction (`RemoveExistingProducts` right after
   `InstallInitialize`): its app processes are ended and its service is stopped and deleted before a new file is written.
   A failure rolls back to the installed version.
2. Every `Wasla.PrintBridge.exe` (every user and session, also a pre-WAS-61 portable copy that still runs its own engine)
   is ended (`taskkill /F /T`, as LocalSystem). Ending the app is safe: printing is the service's (WAS-61). A pre-WAS-61
   portable copy in the middle of a receipt can leave that job in Printing (it had no journal); it is never printed twice.
3. The service is stopped (Windows waits for its graceful stop: a claimed job is finished, WAS-56).
4. Files, registry entries and the shortcut; the service is registered; its privileges and recovery settings; `secure-data`.
5. The service is started.

So an older app and a newer service (or the reverse) never run together. Users get the app back at their next sign-in, or
from Launch at the end of the setup.

| Action | Result |
|--------|--------|
| Repair (Installed Apps → Modify → Repair, `setup.exe /repair`, or the runtime notice's Repair) | Missing or damaged program files restored, service registration and data folder permissions applied again, the WebView2 Runtime installed when missing or too old. Data unchanged |
| Upgrade (a newer setup) | As above, new version. Data unchanged |
| The same version again | Replaces the installed one (a rebuilt or re-signed build), never installs beside it |
| Downgrade (an older setup or MSI) | Refused before anything changes ("A newer version of Wasla Print Bridge is already installed.") |
| Uninstall (Installed Apps, `setup.exe /uninstall`) | Apps ended; service stopped and deleted; program folder, shortcut, Run entry and the `wasla-printbridge` handler removed; Installed Apps entry removed |

**`C:\ProgramData\Wasla\PrintBridge` is never the installer's.** Repair, upgrade and uninstall keep everything in it:
pairing (server URL, device token, installation id), settings, history, the print journal (including receipts waiting for
the user's check), a pending move and logs. Its permissions stay as `secure-data` set them. Removing the pairing after an
uninstall is a manual step for an administrator (delete the folder; it holds the device token). To give it ProgramData's
default permissions back: `icacls "C:\ProgramData\Wasla\PrintBridge" /reset /T /C`.

## WebView2 Runtime

The app window needs the Microsoft Edge WebView2 Runtime (Evergreen) **120.0.2210.55** or newer
(`WebView2RuntimeProbe.MinimumRuntimeVersion`; the setup EXE uses the same value, checked by a test).

- **Detection:** the per-machine runtime's version, `pv` under
  `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`, as Microsoft documents.
  Windows 11 includes it; most Windows 10 computers with Edge have it.
- **Install:** when it is missing or older, the setup EXE runs Microsoft's Evergreen bootstrapper
  (`MicrosoftEdgeWebview2Setup.exe /silent /install`, elevated), which downloads the runtime: this needs the internet. The
  bootstrapper is embedded in the setup EXE; the build downloads it from
  `https://go.microsoft.com/fwlink/p/?LinkId=2124703` and stops unless its Authenticode signature is valid and Microsoft's.
- **Not vital:** if the runtime cannot be installed (offline, a proxy), Print Bridge is still installed and prints. Its app
  window then shows the runtime notice. The runtime is never removed by the setup (other programs share it).
- **Recovery path:** on a computer the setup installed, the notice's **Repair** button starts the installed setup's cached
  copy (Installed Apps registration of the setup's upgrade code, `BundleCachePath`) with `/repair`; Windows asks for
  administrator permission; the repair installs a missing or too old runtime and repairs Print Bridge, then **Launch**
  opens the app again. Printing continues in the service throughout. Without the setup (a development or support copy)
  the notice says to run Print Bridge's setup. A runtime that is present and new enough but damaged is not reinstalled
  by Repair; Windows' own Installed Apps entry for "Microsoft Edge WebView2 Runtime" repairs it.

## Building and signing

```powershell
.\scripts\release\package-print-bridge.ps1                       # unsigned development build of PrintBridgeVersion
.\scripts\release\package-print-bridge.ps1 -Version 1.0.1         # another version (for example an upgrade test)
.\scripts\release\package-print-bridge.ps1 -CopyToWebDownload     # also copy the setup to the Web download folder (local testing)
```

Needs the .NET SDK, network access for NuGet (WiX packages) and Microsoft's bootstrapper (or `-WebView2BootstrapperPath`).

**Signing inputs stay outside the repository.** A signed release build needs a code-signing certificate in the build
machine's certificate store, named by its SHA-1 thumbprint, and a timestamp server: `-SigningCertificateThumbprint` and
`-TimestampUrl`, or `WASLA_SIGNING_CERT_THUMBPRINT` and `WASLA_SIGNING_TIMESTAMP_URL`. The script then signs Wasla's own
executables and libraries (Microsoft's runtime files are already signed), the MSI, the setup EXE's engine (detached and
reattached by the WiX SDK) and the setup EXE, with SHA-256 and an RFC 3161 timestamp, using `signtool` from the Windows SDK.
No certificate, key, password or PFX is part of this repository or its CI. Until a release pipeline holds such a
certificate, every build is an unsigned development build, and Windows SmartScreen warns about it.

## Web download

`GET /print-bridge/download/package` is unchanged (same origin, no query values, `CanManageDeviceSecurity`). It serves
`Wasla:PrintBridgeDownload:PackageFileName` (default `Wasla.PrintBridge-Setup-x64.exe`) from
`Wasla:PrintBridgeDownload:PackagePath` or the Web root's `downloads/wasla-print-bridge/` folder, with the media type of
its extension (`application/vnd.microsoft.portable-executable`; a ZIP for development or support stays `application/zip`).
Without the file the setup page shows the "coming soon" state with no download link, and the route answers 404 (WAS-128).
Guided setup's first install and Help say: download the setup → run it (administrator permission; it installs the WebView2
Runtime if missing) → open Print Bridge (Launch or the Start menu) → connect automatically or manually → choose and test the
printer.

## Verification

| Level | What |
|-------|------|
| Unit | `PrintBridgeProtocolRegistrarTests` (who owns the handler), `PrintBridgeInstalledSetupTests` (finding and starting the installed setup, a missing runtime folder, the sign-in argument), `TrayRuntimeNoticeTests` (Repair offered, started, its failure; printing continues; TR/EN/AR/RU, Arabic mirrored) |
| Source contract | `PrintBridgeInstallerSourceTests`: upgrade code, minimum runtime version, service registration and the WAS-61 `sc.exe` settings, the order of the custom actions, the Run entry and handler, no desktop shortcut or data folder, one version, one WiX release, the accepted EULA |
| Web | `PrintBridgePackageWebRootTests`, `PrintBridgeFirstInstallTests` (the route, its media type, 404, the installer copy in five resource files) |
| Real installer (GitHub-hosted runner, elevated, disposable; CI only) | `PrintBridgeInstallerTests` install this commit's setup EXE over a pre-installer data folder, repair, upgrade to a build with the next build number, refuse the older setup and MSI, and uninstall; then the sign-in start, one app per session, one engine and the runtime notice of the installed app while the service prints. CI builds both setups (`Build Print Bridge installers`) and uploads only their metadata; each step's result is in the TRX output |

Not verified: a signed build (no certificate exists yet); Windows 10; a computer without the WebView2 Runtime downloading
it through the bootstrapper (the runners have the runtime, so the CI install finds it present; the missing case is checked
in the app and by the setup's detection condition); a real interactive sign-in starting the app through Explorer (the test
runs the Run entry's command line as the runner's user, elevated); several users signed in at once; a physical receipt
printer (WAS-28); an upgrade from a pre-WAS-61 portable app that is printing at that moment.

## Known limitations

- The setup's own window (WiX's standard bootstrapper application) is English only: Wasla's guidance names its "Launch"
  button in English. Localized setup pages (TR, AR, RU) need translated theme files: a follow-up.
- No update discovery or rollback (WAS-63): an upgrade is a newer setup run by someone with administrator rights.
- The MSI alone does not install the WebView2 Runtime; administrators who deploy the MSI deploy the runtime themselves.
