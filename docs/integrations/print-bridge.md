# Wasla Print Bridge

## Purpose and scope

This document describes the Windows Print Bridge client and how it talks to Wasla for receipt jobs.

It owns:

- Process boundary (separate Windows app)
- Desktop client structure (Core engine, desktop host, WebView2 desktop app)
- Setup fields (`ServerUrl`, device token)
- ProgramData paths and polling defaults
- Auth header and job API surface (high level)

It does not own receipt template HTML, order lifecycle, or full tenant UI for devices.

## Process boundary

Wasla Print Bridge is a **separate Windows process** (`Wasla.PrintBridge`).

- Web and Worker create `PrintJob` rows (or call existing print services). They do **not** print to Windows printers directly.
- Print Bridge polls the server, claims jobs, and submits them to local Windows printing.
- Physical paper output is not guaranteed by spooler acceptance alone.

## Desktop client structure

Print Bridge is **Windows-only**. It has no macOS or Linux build.

```text
WebView2 desktop app (packaged HTML/CSS/JS): the only Print Bridge window
        │ narrow, versioned web-message contract
        ▼
Wasla.PrintBridge (WinExe): tray, app window and its native dialogs, WebView2 Runtime notice,
                            localization resources, composition root, protocol registration,
                            single-instance setup IPC
        │ the app reads state through IPrintBridgeStatusSource and changes it only
        │ through ShellOperations → IPrintBridgeEngine (allowlisted, single-flight);
        │ setup links use the engine through PrintBridgeAutoSetupCoordinator
        ▼
Wasla.PrintBridge.Core (library): PrintBridgeRuntime (poll, claim, print, retry, mark-*),
                            WaslaPrintBridgeClient (X-PrintBridge-Token), settings and token
                            persistence, local print history, receipt formatting,
                            setup-URI handling, Windows printer integration (winspool)
```

| Project | Owns |
|---------|------|
| `Wasla.PrintBridge.Core` | Engine and integrations. Must not reference WinForms, WPF or WebView2 (checked by `PrintBridgeCoreBoundaryTests`). Namespaces stay `Wasla.PrintBridge.*`. |
| `Wasla.PrintBridge` | Desktop host: `TrayApplicationContext`, `WebShell/` (the WebView2 app window `PrintBridgeShellForm`, its native dialogs and `WebView2RuntimeNotice`), `Localization/` and `Resources/` (TR, EN, AR, RU), `Program` |

There is exactly one engine per process. The app window observes `PrintBridgeRuntime`; it never creates a second polling loop, and the engine keeps running while no window is open.

## Setup (canonical fields)

Canonical connection property: **`ServerUrl`** (not `BaseUrl`).

Also required: device agent token (entered in the desktop Settings UI; generated from tenant Web / CLI).

Rules enforced locally:

- Empty `ServerUrl` or empty token blocked before request
- Values trimmed before save
- Save reconnects/pings with the newly saved values
- Manual setup remains available even when protocol-based auto setup exists

### Settings sections

The settings file (`appsettings.json`, see ProgramData) has exactly three top-level sections:

| Section | Options class | Holds |
|---------|---------------|-------|
| `Wasla` | `WaslaOptions` | `ServerUrl` and the device token (`AgentToken`) |
| `PrintBridge` | `PrintBridgeOptions` | Printer, polling, test mode and device identity |
| `Ui` | `UiOptions` | Language, start with Windows, minimize to tray |

- The connection is read only from `Wasla`. No other section is read, copied or written.
- The property name is **`ServerUrl`**. Do not introduce new `BaseUrl` aliases in docs or code.

**Re-pairing after WAS-135.** A settings file written before WAS-135 has no `Wasla` section, so the device starts unconnected and must be paired again (setup link or manual connection); printer and polling settings are kept. There is no migration. A settings file written by this version is not read by older versions: rolling back means reinstalling the previous package and pairing again.

## ProgramData

Canonical root (Windows CommonApplicationData):

`C:\ProgramData\Wasla\PrintBridge`

| Path | Role |
|------|------|
| `...\appsettings.json` | Runtime config (preferred over Program Files / repo) |
| `...\logs\` | Client logs, one file per day: `wasla-print-bridge-<yyyyMMdd>.log`. Log files with another name were written by earlier versions and can be deleted manually |
| `...\print-history.json` | Local print history store |

Source: `PrintBridgePaths`.

The WebView2 shell keeps its browser profile (cache only, never settings or tokens) per user in `%LOCALAPPDATA%\Wasla\PrintBridge\WebView2`.

**Isolated development instance.** Debug builds only: set `WASLA_PRINTBRIDGE_DATA_ROOT` to an absolute folder to run with its own settings, token, history and logs instead of ProgramData. Such an instance does not register the `wasla-printbridge://` handler and does not listen on the setup pipe, so it cannot take over the installed client. Release builds ignore the variable. Never point a development instance at a real printer or a real device token.

## Authentication

Header: **`X-PrintBridge-Token`**

Also sent by the client (where applicable): version, machine name, installation id, printer name.

Server middleware: `PrintBridgeAuthMiddleware` on Web and API for `/api/print-bridge` (setup exchange paths are exempt from token auth as designed).

Do not log full tokens. Mask tokens in UI by default.

## Polling defaults

Source: `PrintBridgeOptions` defaults.

| Setting | Default |
|---------|---------|
| Idle poll | 5 seconds |
| Busy poll | 1 second |
| Error poll | 15 seconds |
| Max jobs per poll | 3 |

Client clamps `MaxJobsPerPoll` to 1–10 when building the pending-jobs request.

## Job flow

Basic statuses: **Pending → Printing → Printed / Failed** (Cancelled only per existing business rules).

Do not create a second active receipt job while one is Pending or Printing for the same print semantics. First prints and reprints use the shared job pipeline.

Server contract (`PrintBridgeJobService`): a poll offers only Pending jobs, and each report is a conditional transition (`mark-printing` Pending → Printing, `mark-printed` and `mark-failed` Printing → Printed or Failed). A report for a job in another state is answered `skipped` and changes nothing, so the first final report wins and repeating `mark-printed` is safe. There is no claim lease or requeue: a job left in Printing is never offered again and blocks a manual print of that order until it is resolved.

Stop and the engine (`PrintBridgeRuntime`, WAS-56):

- **Before a claim:** Stop (and a terminal connection issue or a connection change) ends polls and waits at once, and no new job is claimed afterwards. A job not yet claimed stays Pending and is printed after the next start.
- **After a claim:** the job is not cancelled. It is printed and reported to its end, bounded by its own limits (the 30-second HTTP timeout, the spooler call, three `mark-printed` attempts). Stop waits for it for up to 10 seconds. If the job takes longer, Stop returns and the job finishes in the background; the next start polls only after it has finished, so two loops never process jobs at once.
- **Failures:** `mark-failed` is sent only when the receipt was not printed (printer, driver or receipt error). Once printed, the job stays Printed locally and is never reported failed. After a server or network failure, `mark-printed` is repeated (after 1 and 3 seconds). If every attempt fails, the loop records the connection problem and the server keeps the job in Printing, which needs attention in Wasla but never leads to a second automatic print.
- Cancelling a job between its claim and its last report used to report printed receipts as failed (`mark-printing → print → mark-printed → Stop → mark-failed`), which invited a duplicate reprint.

Connection changes and active jobs (WAS-58):

- **One decision.** `IPrintBridgeConnectionGuard.TryBeginConnectionChange` (implemented by `PrintBridgeRuntime`) decides whether the server URL and device token may change now. A job is active from just before its claim until its last report, including `mark-printed` retries and a job that still finishes after Stop returned at its 10-second limit. While one or more jobs are active the change is refused. Otherwise the caller gets a lease, and until it is released no new job is claimed. Both are decided under one lock, so a job and a change never both proceed.
- **Who asks.** The setup link (`PrintBridgeAutoSetupCoordinator`) and the app's connection dialog (`ApplyVerifiedConnectionAsync`). A refused setup link returns `PrintingInProgress` before the one-time code is exchanged, so the code is not used up. URL, token, device name, installation ID and the settings file stay byte-for-byte unchanged, and nothing is kept for later. The app window shows `Auto.PrintingInProgress` as a failed setup without reloading or re-verifying anything; without a usable WebView2 Runtime the same text is shown in a message box. Opening the same link again after printing has finished applies it normally.
- **One connection per job.** Each polling cycle takes one copy of the URL and token. Its health check, its poll, and every request of the jobs it claims (`mark-printing`, `mark-printed` and its retries, `mark-failed`) use that copy, never the current settings. A job offered by a poll whose connection has changed since is not claimed and stays pending on the server that offered it. A failure of a replaced connection is logged but not recorded as a problem of the new one. This also covers **Reset connection**, which is not refused (it does not move to another server): a job already claimed still finishes with the token that claimed it.
- **Not covered.** Editing `appsettings.json` by hand while Print Bridge runs is outside this guard. The running engine does not reload that file.

Exiting the app (`TrayApplicationContext`, WAS-59):

- **One shutdown.** The tray's **Exit** enters a shutting-down state at once. From then on no window, tray update, printer test or setup link starts; a setup link forwarded during shutdown is dropped and can be opened after a restart. A second or concurrent Exit joins the shutdown already running. The app window has no exit command of its own; closing it only hides it to the tray, and closing the runtime notice only closes the notice.
- **Order:**
  1. A connection change or setup link still in progress is abandoned before it writes anything. The tray stops following the engine, and the tray icon and windows disappear. A tray update queued just before Exit does nothing when it arrives.
  2. The engine stops. This is awaited, so the UI thread is never blocked.
  3. The app window (WebView2), the runtime notice and the tray icon are disposed. Exit writes no settings.
  4. The services are disposed, the message loop ends and the process exits.
- **With an active print job:** the job is not cancelled (WAS-56). Exit waits for it for up to 10 seconds. A job that finishes in that time is printed and reported once, and Exit then completes. Only the print itself decides `mark-failed`: a status update after the receipt was printed can no longer turn it into a failure. Before WAS-59, the tray's status handler threw on the already disposed tray icon at exactly that point. Measured with test mode and a fake API: without a job the process ended about 0.5–0.7 seconds after Exit; with a job, when the job finished.
- **After the 10-second limit:** the process exits anyway. The log line "stopped listening while a print job is still finishing; it completes in the background" still appears, but on Exit the process ends, so the job does **not** complete. Its remaining requests are not sent and the server keeps it in Printing (see Known gaps). WAS-59 does not change this limit and does not add a durable acknowledgement.
- **Windows sign-out or shutdown** is not routed through this sequence; Windows ends the process, as before.
- **Confirming the exit:** the tray icon is gone, and the log has "Print Bridge exit requested." followed by "Print Bridge shutdown complete; the application exits.". Task Manager (Details) shows no `Wasla.PrintBridge.exe`.

Typical client routes (relative to `ServerUrl`):

- `GET api/print-bridge/health`
- `GET api/print-bridge/jobs/pending?max=…`
- `POST api/print-bridge/jobs/{id}/mark-printing`
- `POST api/print-bridge/jobs/{id}/mark-printed`
- `POST api/print-bridge/jobs/{id}/mark-failed`
- Setup: `api/print-bridge/setup/exchange`, `api/print-bridge/setup/complete`

The health response (`PrintBridgeHealthResponse`) has the properties `success`, `tenantName` (the restaurant's name), `deviceName`, `serverTimeUtc`, `machineName` and `installationId`. A client older than WAS-135 reads no restaurant name (it is only logged) until it is updated.

Controllers exist on both **Web** (`PrintBridgeApiController`, setup APIs) and **API** (`PrintBridgeController`). Point `ServerUrl` at the host that actually serves those routes in your environment (often the tenant Web URL in local/dev samples).

## Device identity

- Device display name is managed from the Wasla Web panel / token identity.
- Local Windows machine name may be shown separately.
- `InstallationId` is a stable local installation identity synced to the server for device binding.

## Devices page presence (Web panel)

- Connection status comes from `PrintBridgeConnectionStatusCalculator`: **Connected** while the last heartbeat is at most 60 seconds old, **Recently seen** up to 5 minutes, then **Disconnected**; an inactive device is **Inactive** and a device that never connected is **Never connected**. Connection status is separate from registration (active/passive), setup sessions and printer readiness.
- The `/print-bridge/devices` list shows Connected as **Online**. Recently seen, Disconnected and Never connected all show as **Offline** there, and a passive device's connection shows as **Unknown**; the device details page shows the finer status label.
- The list's initial state and `GET /print-bridge/devices/list` return the same read-only snapshot (`PrintBridgeDeviceSnapshot`, tenant-scoped, `CanManagePrintBridgeDevices`, not cached). The page reads it again every 15 seconds while the tab is visible, as soon as a hidden tab becomes visible, and after an active/passive toggle. Only one request runs at a time, an older snapshot never replaces a newer one, and a failed background refresh keeps the last state without a message.
- The snapshot carries `serverTimeUtc` and the calculator's thresholds. When a Connected device's heartbeat ages past the threshold by the server's clock, the page shows it as Offline without a reload and immediately asks the server to confirm.
- Every snapshot time is UTC with `Z`. The page shows times with `Intl.DateTimeFormat` in the current UI culture and the Türkiye time zone (`Europe/Istanbul`), as the Live Screen does; a missing or zone-less time shows the "never" placeholder.
- The refresh is a fallback; there is no push channel yet. A future SignalR consumer should call `window.WaslaPrintBridge.refreshDevices()` instead of rendering devices itself.
- The recent print-job history below the list reads `GET /print-bridge/print-jobs` every 3 seconds. Both regions keep the user's place (WAS-132): a region is rebuilt only when its markup changed. While text is selected in it, or the control with keyboard focus would not exist in the new markup, the update waits, so a refresh never moves focus into or out of the region. A waiting update is explained by the region's status line (`PrintBridge.UpdatesPaused`, a polite `role="status"` written once) and is applied as soon as focus moves or the selection ends, without waiting for the next refresh. Otherwise the same control (the same device's switch or details link, the same job's reprint button) gets focus back and the table keeps its scroll position. Polling intervals are unchanged.

## New device tokens in the Web panel

- A token issued on the setup page (manual setup) or by Regenerate token on the device details page is shown once, in a read-only field that starts masked. Show token / Hide token changes only the field's type; the field's value is the token's single copy on the page, and Copy works while it stays masked. Dismissing the details page's token box clears the value. The token is never written into an HTML attribute.
- The same rules are explained in the shared device topics (`_PrintBridgeDeviceTopics`), shown in the guided device guide and on Help.

## WebView2 desktop app (WAS-53, WAS-54, WAS-57, WAS-65)

The only Print Bridge UI. WAS-53 added the secure shell and a status view, WAS-54 the daily-use features, WAS-57 connection setup, operational settings and tray navigation, and WAS-65 removed the earlier WinForms window and every route to it. The page renders host state and sends allowlisted commands; polling, claiming, printing, retries, history, settings and the device token stay in `Wasla.PrintBridge.Core`, and privileged desktop actions stay in the host.

| Area | Contents |
|------|----------|
| Header | Wasla logo and "Print Bridge" (the heading is named "Wasla Print Bridge"; below 36rem only the logo shows), compact connection status (text and dot), language picker |
| Overview | Connection state with a localized explanation and the next step for every problem (connect, reconnect, choose printer, check connection, start, show diagnostics); test-mode notice; printer and its state; listening state and device name; last contact; latest print job; today's jobs, failures and last print time |
| Printer | Installed Windows printers (listed by the host, refresh), save, printer state, test print, test-mode notice |
| History | Today / last 7 days / last 30 days, order search, pages of 20 rows, status labels, failure category (printer, server or other), reprint of printed rows after an inline confirmation |
| Settings | Wasla connection (state; **Connect**, **Reconnect** or **Change connection** opens the native connection dialog; check; reset with a native confirmation), printing behavior (test mode and the three poll intervals), language, theme note, diagnostics (app version, WebView2 Runtime version, listening state, last contact, last error category, printer, test mode, open log folder) |
| Action bar | Start or Stop, Test print, Check connection; always visible below the scrolling content |

Test print is single-flight and refused in test mode with the engine's localized message. Saving a printer accepts only a name the host listed from Windows, uses `PrintBridgeSettingsValidator` and the settings store, and replaces the options object, so a job that is already printing keeps the printer it was claimed for.

### Logo

Print Bridge shows the canonical Wasla logo owned by Wasla.Web (`src/Wasla.Web/wwwroot/images/brand/wasla-logo.svg`; see [../frontend/design-system.md](../frontend/design-system.md#logo)). The project links that file instead of keeping a copy, so both apps always ship the same bytes:

- `shell-ui/wasla-logo.svg` (copied on build and publish) for the WebView2 page header. It is served from the shell origin, which the page's `img-src 'self'` allows.
- An embedded resource for the native windows: `WaslaLogo` (`UI/WaslaLogo.cs`) fills the SVG's vector paths with GDI+, so it stays sharp at every DPI and keeps its aspect ratio. It is the brand mark of the connection and confirmation dialogs and of the WebView2 Runtime notice. If the drawing surface is mirrored by a right-to-left layout, it flips back, so the wordmark is never shown back to front.

`WaslaLogoTests` check that both shipped copies equal the Web asset, that the geometry and colour are read exactly, and the mirrored case. The tray icons (status dots drawn at runtime) and the executable have no Wasla logo; there is no dedicated icon artwork yet.

### Connection dialog

The server URL and device token are entered in a host-owned native dialog (`ShellConnectionDialog`) drawn in the app's palette (light or dark from the Windows app theme, the same orange accent, rounded border-first controls, the Wasla logo) and owned by the app window, so it reads as part of the app. The logic is in `ShellConnectionSetup`; it reuses `PrintBridgeSettingsValidator`, the setup-link paste guard, the API client and the settings store.

- The title and introduction follow why it opens: **first setup** (no token), **reconnect** (the token was rejected or reset) or **change** (a token is saved). The address field is prefilled with the saved address (not the built-in `localhost` default); the token field always starts empty and masked.
- **Show** reveals only what was typed in this dialog. The saved token is never loaded into it and cannot be shown again. In change mode an empty token keeps the saved one, which the dialog says without showing it.
- **Connect and save** validates locally first (empty or malformed URL, query or fragment, empty token, a pasted setup link). It then checks the candidate with `IPrintBridgeEngine.CheckConnectionAsync`, which calls the health endpoint with the candidate URL and token and changes neither the settings nor the engine state, so a rejected candidate never clears the saved token. Only after the check succeeds does `ApplyVerifiedConnectionAsync` stop listening, write the settings file (before the in-memory settings change, so a failed write changes nothing), clear the server-assigned device name when the token changed, record the verified contact and resume listening (always for first setup and reconnect; for a change only if it was listening). "Connected" is shown only after this.
- While a print job is in progress (from its claim, `mark-printing`, until its last report, `mark-printed` or `mark-failed`), the verified connection is not applied, so the job's remaining reports never go to the new connection. `ApplyVerifiedConnectionAsync` returns `PrintingInProgress` before anything is stopped or written, so the saved connection and printer stay as they were and the job finishes once on the saved connection. The dialog says that nothing was saved and keeps the input for another try. From that check until listening has stopped, the engine leaves new jobs pending; they are claimed after the change.
- Failures keep the typed input while the dialog stays open and show a localized, actionable message in an alert region, mark the field, describe it to assistive technology and move focus there: token rejected (token field), server unreachable, certificate error or wrong address (address field), timeout after 30 seconds, disabled device or installation conflict.
- **Cancel**, Escape and the close button leave the saved settings byte-for-byte unchanged and cancel a check in progress. Once saving has begun the dialog stays open until it finishes. Repeated clicks do nothing while it works. Exiting the app from the tray abandons a change that has not been written yet.
- The page receives only the localized result (`operation.result` for `connection.openSetup`) and the refreshed snapshot, without a restart or reload. When the bridge is connected but no usable printer is chosen, the page is sent to the Printer tab.
- Keyboard: Tab follows the visible order (address, token, Show, Connect, Cancel), Enter connects, Escape cancels. Arabic mirrors the dialog; the address and token fields stay left-to-right.

### Operational settings

The Settings tab edits exactly test mode and the idle, busy and error poll intervals. The page sends one typed command (`settings.save`); there is no generic "write a setting" command. The host validates with `PrintBridgeSettingsValidator` (idle 1–300 s, busy 1–60 s, error 1–300 s, also sent to the page as ranges), writes the settings file and replaces the in-memory options. The engine reads these values for every poll and job, so they apply from its next check without a restart; a wait already in progress finishes first. The form shows the saved values unless the user is editing, marks unsaved changes and invalid fields, and keeps a draft only until the host reports those values as saved.

Turning test mode **on** needs a native confirmation, because in test mode new receipt jobs are not printed on paper but are still reported to Wasla as printed. The page alone cannot turn it on. Turning it off needs no confirmation.

### Navigation and tray

The WebView2 app is the only Print Bridge window (WAS-65). There is no other window, no setting that selects one and no fallback to one.

| Entry point | Opens |
|-------------|-------|
| Start without a usable connection (no server URL or device token saved: a new installation, or after **Reset connection** or a rejected token) | The app window on Overview, whose next step is **Connect**; it opens the native connection dialog in first-setup or reconnect mode |
| Start of a configured installation (server URL and device token saved) | Nothing: the tray icon only. Listening starts on its own when a printer is chosen as well |
| Tray Open, double-click | The app window (one instance), brought forward |
| Tray Print history / Settings | The app window on the History / Settings tab |
| Tray Printer test | Native message box with the result (no window) |
| Setup link (`wasla-printbridge://setup`) | Applied in the app window; result shown there, Printer tab when a printer is missing |
| Any of the above when the WebView2 Runtime is missing, too old or the app fails to start | The runtime notice (below); the tray and the engine keep running. A setup link is still applied and its result shown in a message box |

The tray menu has exactly Open, Print history, Printer test, Settings and Exit, below a disabled line with the connection state.

The app window keeps no settings of its own: it reads the saved settings each time it renders. The settings file's `Ui` section holds only `Language`, `StartWithWindows` and `MinimizeToTray`; the last two have no effect today. Any other key in that section is ignored when the file is read and dropped the next time the settings are saved. Max jobs per poll has no UI and stays a settings-file value.

Not in the app on purpose: a live log viewer (log lines contain the server URL, machine name, printer names and file paths; **Open log folder** in Settings → Diagnostics opens them in File Explorer), the Windows machine name and the settings file path (secret boundary).

### Runtime dependency and recovery notice

The app window needs the Microsoft Edge WebView2 Runtime (Evergreen), version 120.0.2210.55 or newer (`WebView2RuntimeProbe`). It is checked each time a window is requested. When it is missing or too old, or the app window fails to start (runtime error, page failed to load, browser process exit), the tray shows `WebView2RuntimeNotice` instead:

- A small native window in the app's palette (light or dark from the Windows app theme, the Wasla logo) with a title, one message and a **Close** button: the window needs the Microsoft Edge WebView2 Runtime, which is missing or cannot be used; receipt printing continues in the background; install or repair the runtime, then restart Print Bridge. Resource keys `RuntimeNotice.Title`, `RuntimeNotice.Message` and `RuntimeNotice.Close`, in TR, EN, AR and RU.
- Arabic mirrors it right to left. It scales with the monitor's DPI, has a taskbar button (there is no owner window), and Enter, Escape and the button close it; the message is also its accessible description.
- One notice at a time: asking again (any tray entry, double-click, a setup link, a start without a connection) brings the open notice forward. After the app window failed to start, the notice is shown for the rest of the session.
- The tray, the engine, polling, printing, history and setup links keep working. A setup link is applied, and refused while a print job is being completed (WAS-58), exactly as with the app window; its result is shown in a message box. Manual connection setup needs the app window, so without the runtime a new device can be connected only through a setup link.
- Shipping or installing the runtime belongs to WAS-55 and WAS-62. Rollback is the previous signed package, never a second window in this one.

### Security model

- The page is served only from the packaged `shell-ui` folder through a synthetic origin, `https://shell.printbridge.invalid` (`.invalid` never resolves), mapped with `CoreWebView2HostResourceAccessKind.Deny`. No other folder is mapped.
- `index.html` sets a restrictive CSP: `default-src 'none'`, scripts and styles from `'self'` only, `connect-src 'none'`, no frames, objects, workers or forms, `base-uri 'none'`, and Trusted Types (`require-trusted-types-for 'script'`), so `eval` and string-to-HTML sinks fail. No remote scripts, fonts, styles or analytics.
- Every sub-resource request outside the shell origin gets 403 from the host (`WebResourceRequested`), in addition to the CSP.
- Top-level navigation is allowed only to `/index.html` on the shell origin. Frame navigation, new windows and popups, downloads, external URI schemes, basic authentication and every permission request (camera, location, notifications and so on) are denied. Certificate errors are cancelled.
- `ShellSecurityProfile`: host objects, default context menus, autofill, password saving, script dialogs, status bar, swipe navigation, external drops and SmartScreen lookups are off in every build. DevTools and browser accelerator keys are enabled only in Debug builds; Release builds disable them.
- The page has no access to the device token, server URL, installation id, Windows machine name or tenant identifiers: no host message has such fields, and nothing is stored in `localStorage`, `sessionStorage` or cookies. The device name is the one assigned in the Wasla Web panel, or "unknown device".
- The page has no credential fields. Credentials are entered only in the native connection dialog, which the page can open (`connection.openSetup` carries only a request id) but never read. Resetting the connection and turning test mode on ask in a native dialog the page cannot answer.
- Privileged actions take nothing from the page (`IShellNativeActions` methods have no parameters). Open log folder opens only `PrintBridgePaths.ProgramDataLogDirectory` in File Explorer; logs are never rendered in the page. Native dialogs are shown from a posted callback, never inside the WebView2 message handler.
- Every state-changing command carries a request id. The host executes an id once (it remembers the last 256), runs at most one operation per group (engine, connection test, connection setup, reset, printer refresh, printer save, test print, reprint, settings save) and answers a second request with `busy`. While the connection dialog is open, start, stop, connection checks and reset wait, and the reverse. Busy state shown in the page comes from the host. Engine calls time out after 30 seconds; the connection dialog has no time limit, its server check does.
- Results are localized resource text. Exception text, server URLs, file paths and raw print errors never reach the page; an unexpected failure shows a generic message.
- History rows carry opaque per-session handles (`h` plus 16 hex digits). Only the host maps a handle to a job, so the page never sees job or order ids, receipt content or error text.

### Message contract (version 3)

Messages are JSON strings `{ "version": 3, "type": "…", "payload": { … } }`, at most 1024 characters from the page (`ShellMessageContract`, `ShellMessageParser`, `shell-model.js`). WAS-57 raised the version from 2: `connection.openSetup` became a tracked command, `settings.save` and `ui.navigate` were added, and the snapshot gained the operational settings. WAS-65 removed the page's command that opened the earlier WinForms window without raising the version: the page and the host ship together in one package, no remaining message changed its meaning, and the host rejects the removed type like any unknown type.

| Direction | Type | Payload |
|-----------|------|---------|
| page → host | `ui.ready` | `{}`; the host replies with a full snapshot, then any queued tab request or setup-link result, and lists printers once. Repeats are harmless. |
| page → host | `snapshot.request` | `{}` |
| page → host | `language.change` | `{ "culture": "tr-TR" }`; exactly one of `tr-TR`, `en-US`, `ar-SA`, `ru-RU` |
| page → host | `engine.start`, `engine.stop` | `{ "requestId" }`; start is refused while a reconnect is required |
| page → host | `connection.openSetup` | `{ "requestId" }`; opens the native connection dialog and is answered when it closes |
| page → host | `connection.test`, `connection.reset`, `printers.refresh`, `printer.testPrint`, `logs.openFolder` | `{ "requestId" }` |
| page → host | `printer.save` | `{ "requestId", "name" }`; 1–256 characters, no surrounding spaces or control characters, must be an installed printer |
| page → host | `history.query` | `{ "requestId", "range", "page", "search"? }`; `range` is `today`, `last7Days` or `last30Days`, `page` 0–1000, `search` up to 64 characters |
| page → host | `history.reprint` | `{ "requestId", "itemRef" }`; a handle from `history.result` |
| page → host | `settings.save` | `{ "requestId", "testMode", "idlePollSeconds", "busyPollSeconds", "errorPollSeconds" }`; a boolean and three whole numbers in 0–86400 (the host applies the real ranges and explains a refusal) |
| host → page | `snapshot.updated` | Connection, engine, device, printer (with the installed list), activity, latest job, allowed actions, busy flags, diagnostics, test mode, operational settings (saved values and ranges), languages and localized strings |
| host → page | `operation.result` | `{ "requestId", "operation", "outcome", "message" }`; outcome `succeeded`, `failed`, `busy`, `rejected` or `cancelled` (a repeated request id gets no second answer). A setup-link result arrives as `connection.openSetup` with `requestId: null` |
| host → page | `history.result` | `{ "requestId", "history" }`; one page of rows |
| host → page | `ui.navigate` | `{ "tab" }`; `overview`, `printer`, `history` or `settings` (tray entries, or the Printer tab after connecting without a printer) |

Request ids match `^[A-Za-z0-9][A-Za-z0-9-]{7,63}$`; the page uses `crypto.randomUUID()`.

Rules:

- The host accepts messages only from the shell document. It rejects unknown types, other versions, unknown or duplicate properties, non-object payloads, unexpected or invalid payload fields, malformed JSON and oversized messages, and logs only the rejection reason and length.
- Commands map to a fixed enum; no page string ever selects a host method, and no host object is exposed.
- The page renders what the host sends and never derives connection or print state. All host messages share one `sequence`; the page drops older or repeated ones. A command is shown as done only when its `operation.result` arrives; the page stops waiting after 45 seconds and then shows host state again (the busy flag of an open connection dialog keeps its button busy). Language changes are applied by the host (saved as `Ui.Language`) and confirmed by the next snapshot.
- The app reads the engine through `IPrintBridgeStatusSource` and changes it only through `ShellOperations`, which uses `IPrintBridgeEngine` (start, stop, connection test, check and apply, reset, test print, history, reprint). It has no timer, thread or HTTP client of its own, so it cannot create a second poll loop. Engine notifications from background threads are coalesced into one UI-thread update, and unchanged snapshots are not resent. Closing the window hides it to the tray.

### Dates, theme and accessibility

- Operational times (orders, print jobs, contact, test print, receipts) use the Gregorian calendar in every language, including Arabic, whose culture default is Umm al-Qura. `GregorianCulture.For` (`Wasla.Application.Printing`) keeps the language's names, separators and era marker and changes only the calendar. It is used by `PrintBridgeDateTimeFormatter` (the app snapshot and history), `ReceiptLabelLocalizer.GetCulture` (receipt times) and the engine's test print.
- Receipt amounts (lines, option surcharges, subtotal, delivery, total) are printed in one format whatever the PC's culture: two decimals with a point, no currency sign, matching the receipt preview in Web. Option surcharges used the PC's culture until WAS-44 (`12,50` next to `12.50` on a Turkish Windows PC). Items and options come in the order of the server's payload, which is the order every screen shows ([../frontend/design-system.md](../frontend/design-system.md#tables-and-sorting)).
- Light or dark follows the Windows app theme: the page uses `prefers-color-scheme`, and the host updates the window background and title bar when the Windows setting changes. Native dialogs pick the same palette when they open.
- The title bar and the app's top band (the Wasla logo, connection status, language and tabs) share one surface (WAS-174). On Windows 11 the host sets the title bar's caption, text and border colours through DWM (`ShellWindowTheme.TryApplyTitleBar`), which also replaces the accent colour Windows paints when "Show accent colour on title bars and window borders" is on; the text dims while the window is inactive. Windows 10 gets only the dark title bar and keeps the accent colour if that setting is on. Under high contrast Windows' own colours are kept. The window keeps the standard Windows frame, so dragging, snapping, resizing and the caption buttons are Windows' own. Its icon is the wordmark's first letter, rendered from the canonical logo (`WaslaAppIcon`).
- Tabs follow the ARIA tab pattern: one tab stop, arrow keys that follow the reading direction, Home and End. Unavailable actions use `aria-disabled` so focus is not lost; busy buttons are described as working. The reprint confirmation takes focus and returns it. Results are announced through polite (`status`) and assertive (`alert`) live regions. Invalid poll intervals are marked with `aria-invalid` and described by the error text only while it applies; test mode is a labelled switch. Styles use logical directions for right-to-left, honor reduced motion and forced colors, and every coloured state also has a text label. The layout needs no horizontal scrolling at the 420 × 420 minimum window, at 200 % zoom or at 320 CSS pixels.

### Future work

- **WAS-55:** Windows Service, installer, WebView2 Runtime delivery and automatic update.

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- The WebView2 app has not yet been verified with a physical receipt printer, and the runtime notice not on a physical installation without the WebView2 Runtime; WAS-54, WAS-57 and WAS-65 verification used test mode, a fake API, a simulated missing runtime and a recording or missing printer.
- Without the WebView2 Runtime a device can be connected only through a setup link; manual setup needs the app window.
- Engine and client log lines include the server URL, the Windows machine name and printer names (for example the startup "Effective config" line and request warnings). Tokens are never logged.
- A setup link opened while a print job is being completed is refused, not queued (WAS-58, see Job flow). The user has to open it again after printing has finished. Hand edits to `appsettings.json` while Print Bridge runs are not guarded.
- The server has no claim lease. A printed job whose `mark-printed` never arrives stays in Printing, for example after every retry failed, after Exit (or Stop followed by Exit) gave up waiting at its 10-second limit, or after a crash or power loss between the print and the report. It is never printed again automatically, but it blocks a manual print of that order until it is resolved. A `mark-printed` answered `not_found` still marks the local record failed, as before WAS-56.
- Every WebView2 host honors the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable (for example a remote-debugging port). This is platform behavior the shell does not override; anyone who can set the user's environment can already inspect the process.
- The device details page still formats times on the server with `ToLocalTime()` (the server's zone, not `Europe/Istanbul`) and does not refresh itself. The token regeneration response still returns device times without a UTC marker.
- Each job creation and each accepted `mark-printing`, `mark-printed` and `mark-failed` is announced to the tenant's open order detail pages after it is recorded ([../architecture/realtime.md](../architecture/realtime.md)). A report sent to Wasla.Api is announced only when Api has `Realtime:Internal` configured. The Bridge's own polling, claiming and retries are unchanged, and a skipped report announces nothing. The historical planning note `docs/orders-printjob-status-signalr.md` is superseded.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
