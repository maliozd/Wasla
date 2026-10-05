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
WebView2 desktop app (packaged HTML/CSS/JS, opt-in)    Classic WinForms window (default)
        │ narrow, versioned web-message contract                │
        ▼                                                       ▼
Wasla.PrintBridge (WinExe): tray, windows, localization resources, composition root,
                            protocol registration, single-instance setup IPC
        │ the app reads state through IPrintBridgeStatusSource and changes it only
        │ through ShellOperations → IPrintBridgeEngine (allowlisted, single-flight);
        │ the classic window and setup keep using the engine directly
        ▼
Wasla.PrintBridge.Core (library): PrintBridgeRuntime (poll, claim, print, retry, mark-*),
                            WaslaPrintBridgeClient (X-PrintBridge-Token), settings and token
                            persistence, local print history, receipt formatting,
                            setup-URI handling, Windows printer integration (winspool)
```

| Project | Owns |
|---------|------|
| `Wasla.PrintBridge.Core` | Engine and integrations. Must not reference WinForms, WPF or WebView2 (checked by `PrintBridgeCoreBoundaryTests`). Namespaces stay `Wasla.PrintBridge.*`. |
| `Wasla.PrintBridge` | Desktop host: `TrayApplicationContext`, classic `MainForm`, `WebShell/` (WebView2 shell), `Localization/` and `Resources/` (TR, EN, AR, RU), `Program` |

There is exactly one engine per process. Both windows observe the same `PrintBridgeRuntime`; neither creates a second polling loop.

## Setup (canonical fields)

Canonical connection property: **`ServerUrl`** (not `BaseUrl`).

Also required: device agent token (entered in the desktop Settings UI; generated from tenant Web / CLI).

Rules enforced locally:

- Empty `ServerUrl` or empty token blocked before request
- Values trimmed before save
- Save reconnects/pings with the newly saved values
- Manual setup remains available even when protocol-based auto setup exists

### Legacy config-section debt

The options class that holds `ServerUrl` / agent token still uses config section name **`OrderHub`** (`WaslaOptions.SectionName = "OrderHub"`).

- Treat `OrderHub` as **current legacy config-section debt**.
- The property name is **`ServerUrl`**. Do not introduce new `BaseUrl` aliases in docs or code.

Polling and printer options use section `PrintBridge` (`PrintBridgeOptions`).

## ProgramData

Canonical root (Windows CommonApplicationData):

`C:\ProgramData\Wasla\PrintBridge`

| Path | Role |
|------|------|
| `...\appsettings.json` | Runtime config (preferred over Program Files / repo) |
| `...\logs\` | Client logs |
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

Typical client routes (relative to `ServerUrl`):

- `GET api/print-bridge/health`
- `GET api/print-bridge/jobs/pending?max=…`
- `POST api/print-bridge/jobs/{id}/mark-printing`
- `POST api/print-bridge/jobs/{id}/mark-printed`
- `POST api/print-bridge/jobs/{id}/mark-failed`
- Setup: `api/print-bridge/setup/exchange`, `api/print-bridge/setup/complete`

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

## New device tokens in the Web panel

- A token issued on the setup page (manual setup) or by Regenerate token on the device details page is shown once, in a read-only field that starts masked. Show token / Hide token changes only the field's type; the field's value is the token's single copy on the page, and Copy works while it stays masked. Dismissing the details page's token box clears the value. The token is never written into an HTML attribute.
- The same rules are explained in the shared device topics (`_PrintBridgeDeviceTopics`), shown in the guided device guide and on Help.

## WebView2 desktop app (WAS-53, WAS-54, WAS-57)

The modern desktop UI. WAS-53 added the secure shell and a status view, WAS-54 the daily-use features, and WAS-57 made it the complete normal UI: connection setup, operational settings and tray navigation no longer open the classic window. The page renders host state and sends allowlisted commands; polling, claiming, printing, retries, history, settings and the device token stay in `Wasla.PrintBridge.Core`, and privileged desktop actions stay in the host.

| Area | Contents |
|------|----------|
| Header | Product name, compact connection status (text and dot), language picker |
| Overview | Connection state with a localized explanation and the next step for every problem (connect, reconnect, choose printer, check connection, start, show diagnostics); test-mode notice; printer and its state; listening state and device name; last contact; latest print job; today's jobs, failures and last print time |
| Printer | Installed Windows printers (listed by the host, refresh), save, printer state, test print, test-mode notice |
| History | Today / last 7 days / last 30 days, order search, pages of 20 rows, status labels, failure category (printer, server or other), reprint of printed rows after an inline confirmation |
| Settings | Wasla connection (state; **Connect**, **Reconnect** or **Change connection** opens the native connection dialog; check; reset with a native confirmation), printing behavior (test mode and the three poll intervals), language, theme note, diagnostics (app version, WebView2 Runtime version, listening state, last contact, last error category, printer, test mode, open log folder) and the classic window as an emergency fallback |
| Action bar | Start or Stop, Test print, Check connection; always visible below the scrolling content |

Test print is single-flight and refused in test mode with the engine's localized message. Saving a printer accepts only a name the host listed from Windows, uses the classic window's validator and settings store, and replaces the options object, so a job that is already printing keeps the printer it was claimed for.

### Connection dialog

The server URL and device token are entered in a host-owned native dialog (`ShellConnectionDialog`) drawn in the app's palette (light or dark from the Windows app theme, the same orange accent, rounded border-first controls, the brand mark) and owned by the app window, so it reads as part of the app and not as the classic window. The logic is in `ShellConnectionSetup`; it reuses `PrintBridgeSettingsValidator`, the setup-link paste guard, the API client and the settings store.

- The title and introduction follow why it opens: **first setup** (no token), **reconnect** (the token was rejected or reset) or **change** (a token is saved). The address field is prefilled with the saved address (not the built-in `localhost` default); the token field always starts empty and masked.
- **Show** reveals only what was typed in this dialog. The saved token is never loaded into it and cannot be shown again. In change mode an empty token keeps the saved one, which the dialog says without showing it.
- **Connect and save** validates locally first (empty or malformed URL, query or fragment, empty token, a pasted setup link). It then checks the candidate with `IPrintBridgeEngine.CheckConnectionAsync`, which calls the health endpoint with the candidate URL and token and changes neither the settings nor the engine state, so a rejected candidate never clears the saved token. Only after the check succeeds does `ApplyVerifiedConnectionAsync` stop listening, write the settings file (before the in-memory settings change, so a failed write changes nothing), clear the server-assigned device name when the token changed, record the verified contact and resume listening (always for first setup and reconnect; for a change only if it was listening). "Connected" is shown only after this.
- While a print job is in progress (from its claim, `mark-printing`, until its last report, `mark-printed` or `mark-failed`), the verified connection is not applied. Stopping then would cancel the job mid-report, which is the Stop race tracked in WAS-56. `ApplyVerifiedConnectionAsync` returns `PrintingInProgress` before anything is stopped or written, so the saved connection and printer stay as they were and the job finishes once on the saved connection. The dialog says that nothing was saved and keeps the input for another try. From that check until listening has stopped, the engine leaves new jobs pending; they are claimed after the change.
- Failures keep the typed input while the dialog stays open and show a localized, actionable message in an alert region, mark the field, describe it to assistive technology and move focus there: token rejected (token field), server unreachable, certificate error or wrong address (address field), timeout after 30 seconds, disabled device or installation conflict.
- **Cancel**, Escape and the close button leave the saved settings byte-for-byte unchanged and cancel a check in progress. Once saving has begun the dialog stays open until it finishes. Repeated clicks do nothing while it works. Exiting the app from the tray abandons a change that has not been written yet.
- The page receives only the localized result (`operation.result` for `connection.openSetup`) and the refreshed snapshot, without a restart or reload. When the bridge is connected but no usable printer is chosen, the page is sent to the Printer tab.
- Keyboard: Tab follows the visible order (address, token, Show, Connect, Cancel), Enter connects, Escape cancels. Arabic mirrors the dialog; the address and token fields stay left-to-right.

### Operational settings

The Settings tab edits exactly the classic Advanced section: test mode and the idle, busy and error poll intervals. The page sends one typed command (`settings.save`); there is no generic "write a setting" command. The host validates with `PrintBridgeSettingsValidator` (idle 1–300 s, busy 1–60 s, error 1–300 s, also sent to the page as ranges), writes the settings file and replaces the in-memory options. The engine reads these values for every poll and job, so they apply from its next check without a restart; a wait already in progress finishes first. The form shows the saved values unless the user is editing, marks unsaved changes and invalid fields, and keeps a draft only until the host reports those values as saved.

Turning test mode **on** needs a native confirmation, because in test mode new receipt jobs are not printed on paper but are still reported to Wasla as printed. The page alone cannot turn it on. Turning it off needs no confirmation. Max jobs per poll has no UI in either window and stays a settings-file value.

### Navigation and tray

| Entry point | `Ui.Shell = WebView2` | Classic default |
|-------------|----------------------|-----------------|
| Tray Open, double-click | The app window (one instance), brought forward | Classic window |
| Tray Print history / Settings | The app window on the History / Settings tab | Classic window on that tab |
| Tray Printer test | Native message box with the result (no window) | Same |
| Tray **Open classic window (fallback)** | Classic window; shown only while the app window is in use | Not shown |
| Setup link (`wasla-printbridge://setup`) | Applied in the app window; result shown there, Printer tab when a printer is missing | Classic window with message boxes, as before |
| WebView2 Runtime missing, or the app fails to start | Classic window on the requested tab, with one balloon notice per session | — |

The classic window reloads every setting before it is shown, so a later save there cannot write back values the app changed while it was hidden.

### Parity with the classic window

| Capability | Classic window | WebView2 app | Shared host or engine service |
|------------|----------------|--------------|-------------------------------|
| Connection state, last contact, printer state, today's counts, last print | Status tab | Overview | `IPrintBridgeStatusSource`, `ShellSnapshotFactory` |
| Recent jobs of the session | Status tab grid | Overview latest job; History | Engine recent jobs, `LocalPrintJobHistoryStore` |
| Start or stop listening | Status tab | Action bar, Overview | `ShellOperations` → `IPrintBridgeEngine` |
| Check the saved connection | Status tab, Settings | Action bar, Settings, Overview | `IPrintBridgeEngine.TestConnectionAsync` |
| Enter or replace server URL and token, show or hide, paste guard, save and verify | Settings → Wasla connection | Native connection dialog | `ShellConnectionSetup`, `PrintBridgeSettingsValidator`, `CheckConnectionAsync`, `ApplyVerifiedConnectionAsync`, `PrintBridgeSettingsStore` |
| Check unsaved connection values | Settings → Test connection | Part of Connect and save | Same |
| Reset the connection | Settings → troubleshooting, confirmation | Settings, native confirmation | `ResetConnectionForReconnectAsync` |
| Printer list, refresh, save | Settings → Printer | Printer tab | `IPrinterCatalog`, `ShellPrinterSettings` |
| Test print | Status tab, Settings, tray | Action bar, Printer tab, tray | `IPrintBridgeEngine.TestPrinterAsync` (refused in test mode; the classic Settings button prints even in test mode) |
| Language | Settings → Language | Header and Settings | `PrintBridgeLanguageService` |
| Test mode | Settings → Advanced | Settings → Printing behavior, native confirmation to turn on | `ShellOperationalSettings` |
| Idle, busy and error poll intervals | Settings → Advanced | Settings → Printing behavior | `ShellOperationalSettings` |
| Print history, search, reprint | History tab | History tab | `ShellHistory`, `IPrintBridgeEngine` |
| Open the log folder | Status tab, Logs tab | Settings → Diagnostics | `ShellLogFolder` |
| Live log viewer, copy, clear | Logs tab | **Not in the app on purpose**: log lines contain the server URL, machine name, printer names and file paths. Open the log folder, or the classic fallback | `UiLogBuffer` |
| App version | Footer | Diagnostics | — |
| Machine name, settings file path | Footer, Settings hint | **Not shown** (secret boundary) | — |
| Setup link | Classic window | App window | `PrintBridgeAutoSetupCoordinator`, `PrintBridgeRuntime.VerifyAndResumeAsync` |
| Max jobs per poll, `Ui.Shell`, start with Windows, minimize to tray | No UI | No UI (settings file only; the last two have no effect today) | — |

### Enabling (explicit switch)

The classic WinForms window remains the default. Set `Ui.Shell` in `C:\ProgramData\Wasla\PrintBridge\appsettings.json`:

| `Ui.Shell` | Tray entries open |
|------------|-------------------|
| missing, empty or `WinForms` | Classic window (WebView2 is never loaded) |
| `WebView2` | WebView2 desktop app, if the runtime is usable (see the navigation table) |
| anything else | Classic window, with a warning in the log |

The key is written back only when it was set.

### Default-switch decision (after WAS-55 and WAS-56)

The classic window stays the default. Switch it (missing `Ui.Shell` means `WebView2`, `WinForms` stays an explicit opt-out) only when all of these hold:

1. WAS-55 delivers and updates the WebView2 Runtime with the installer and Windows Service, so the fallback is rare.
2. WAS-56 fixes the job acknowledgement race when listening stops. The app's connection dialog already refuses a change while a job is in progress. Stop, Reset and the classic window's connection save still stop listening mid-job.
3. A test print and a real order receipt have been verified on a physical receipt printer with the app (WAS-54 and WAS-57 verified with test mode and fakes only).
4. The classic window remains reachable as the labelled emergency fallback for at least one release.

The tray's Print history and Settings entries open the matching app tabs since WAS-57.

### Runtime dependency and fallback

The shell needs the Microsoft Edge WebView2 Runtime (Evergreen), version 120.0.2210.55 or newer (`WebView2RuntimeProbe`). WAS-53 only detects it. When it is missing or too old, or the shell fails to start (runtime error, page failed to load, browser process exit), the tray opens the classic window on the requested tab and shows one balloon notice per session. Shipping or installing the runtime belongs to WAS-55.

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

Messages are JSON strings `{ "version": 3, "type": "…", "payload": { … } }`, at most 1024 characters from the page (`ShellMessageContract`, `ShellMessageParser`, `shell-model.js`). WAS-57 raised the version from 2: `connection.openSetup` became a tracked command, `settings.save` and `ui.navigate` were added, and the snapshot gained the operational settings. The page and the host ship together.

| Direction | Type | Payload |
|-----------|------|---------|
| page → host | `ui.ready` | `{}`; the host replies with a full snapshot, then any queued tab request or setup-link result, and lists printers once. Repeats are harmless. |
| page → host | `snapshot.request` | `{}` |
| page → host | `language.change` | `{ "culture": "tr-TR" }`; exactly one of `tr-TR`, `en-US`, `ar-SA`, `ru-RU` |
| page → host | `classicWindow.open` | `{}` |
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
- The page renders what the host sends and never derives connection or print state. All host messages share one `sequence`; the page drops older or repeated ones. A command is shown as done only when its `operation.result` arrives; the page stops waiting after 45 seconds and then shows host state again (the busy flag of an open connection dialog keeps its button busy). Language changes are applied by the host (same persistence as the classic Settings tab) and confirmed by the next snapshot.
- The app reads the engine through `IPrintBridgeStatusSource` and changes it only through `ShellOperations`, which uses `IPrintBridgeEngine` (start, stop, connection test, check and apply, reset, test print, history, reprint). It has no timer, thread or HTTP client of its own, so it cannot create a second poll loop. Engine notifications from background threads are coalesced into one UI-thread update, and unchanged snapshots are not resent. Closing the window hides it to the tray, like the classic window.

### Dates, theme and accessibility

- Operational times (orders, print jobs, contact, test print, receipts) use the Gregorian calendar in every language, including Arabic, whose culture default is Umm al-Qura. `GregorianCulture.For` (`Wasla.Application.Printing`) keeps the language's names, separators and era marker and changes only the calendar. It is used by `PrintBridgeDateTimeFormatter` (both windows, the app snapshot and history), `ReceiptLabelLocalizer.GetCulture` (receipt times) and the engine's test print.
- Light or dark follows the Windows app theme: the page uses `prefers-color-scheme`, and the host updates the window background and title bar when the Windows setting changes. Native dialogs pick the same palette when they open.
- Tabs follow the ARIA tab pattern: one tab stop, arrow keys that follow the reading direction, Home and End. Unavailable actions use `aria-disabled` so focus is not lost; busy buttons are described as working. The reprint confirmation takes focus and returns it. Results are announced through polite (`status`) and assertive (`alert`) live regions. Invalid poll intervals are marked with `aria-invalid` and described by the error text only while it applies; test mode is a labelled switch. Styles use logical directions for right-to-left, honor reduced motion and forced colors, and every coloured state also has a text label. The layout needs no horizontal scrolling at the 420 × 420 minimum window, at 200 % zoom or at 320 CSS pixels.

### Future work

- **WAS-55:** Windows Service, installer, WebView2 Runtime delivery and automatic update, then the default-switch decision above.
- **WAS-56:** the job acknowledgement race when listening stops.

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- Config section still named `OrderHub` while product is Wasla.
- The WebView2 app is opt-in and has not yet been verified with a physical receipt printer; WAS-54 and WAS-57 verification used test mode, a fake API and a recording or missing printer. The live log viewer stays only in the classic window on purpose (see the parity table).
- Engine and client log lines include the server URL, the Windows machine name and printer names (for example the startup "Effective config" line and request warnings). Tokens are never logged.
- A setup link (`PrintBridgeAutoSetupCoordinator`, both windows) replaces the saved connection in place without stopping listening. A job in progress at that moment sends its remaining reports with the new connection. The connection dialog does not have this gap; Stop and Reset are covered by WAS-56.
- Every WebView2 host honors the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable (for example a remote-debugging port). This is platform behavior the shell does not override; anyone who can set the user's environment can already inspect the process.
- Sample / download packaging may still mention older folder names in places; prefer `ServerUrl` and Wasla ProgramData paths above.
- The device details page still formats times on the server with `ToLocalTime()` (the server's zone, not `Europe/Istanbul`) and does not refresh itself. The token regeneration response still returns device times without a UTC marker.
- Live SignalR push for print-job status is future planning only: [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). It is not current implementation. An older copy remains at `docs/orders-printjob-status-signalr.md` until that file is retired.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
