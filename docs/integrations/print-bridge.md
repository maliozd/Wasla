# Wasla Print Bridge

## Purpose and scope

This document describes the Windows Print Bridge client and how it talks to Wasla for receipt jobs.

It owns:

- Process boundary (separate Windows app)
- Desktop client structure (Core engine, desktop host, WebView2 status shell)
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

## WebView2 desktop app (WAS-53, WAS-54)

The modern desktop UI. WAS-53 added the secure shell and a status view; WAS-54 adds the daily-use features. The page renders host state and sends allowlisted commands; polling, claiming, printing, retries, history, settings and the device token stay in `Wasla.PrintBridge.Core`, and privileged desktop actions stay in the host.

| Area | Contents |
|------|----------|
| Header | Product name, compact connection status (text and dot), language picker |
| Overview | Connection state with a localized explanation and the next step for every problem (start, reconnect, choose printer, check connection, show diagnostics); test-mode notice; printer and its state; listening state and device name; last contact; latest print job; today's jobs, failures and last print time |
| Printer | Installed Windows printers (listed by the host, refresh), save, printer state, test print, test-mode notice |
| History | Today / last 7 days / last 30 days, order search, pages of 20 rows, status labels, failure category (printer, server or other), reprint of printed rows after an inline confirmation |
| Settings | Wasla connection (check, open the native connection setup, reset with a native confirmation), language, theme note, test-mode state, diagnostics (app version, WebView2 Runtime version, listening state, last contact, last error category, printer, test mode, open log folder) and the classic window |
| Action bar | Start or Stop, Test print, Check connection; always visible below the scrolling content |

Still only in the classic window: entering the server URL and device token, the Advanced section (test mode, poll intervals), the Logs viewer and automatic setup from a setup link. The tray menu's Print history and Settings entries still open the classic window.

Test print is single-flight and refused in test mode with the engine's localized message. Saving a printer accepts only a name the host listed from Windows, uses the classic window's validator and settings store, and replaces the options object, so a job that is already printing keeps the printer it was claimed for.

### Enabling (explicit switch)

The classic WinForms window remains the default. Set `Ui.Shell` in `C:\ProgramData\Wasla\PrintBridge\appsettings.json`:

| `Ui.Shell` | Tray **Open** / double-click opens |
|------------|-------------------------------------|
| missing, empty or `WinForms` | Classic window (WebView2 is never loaded) |
| `WebView2` | WebView2 desktop app, if the runtime is usable |
| anything else | Classic window, with a warning in the log |

The key is written back only when it was set. The classic window stays one click away in Settings, and the tray falls back to it whenever the app cannot start.

### Default-switch decision (after WAS-55)

WAS-54 keeps the classic window as the default. Switch the default (missing `Ui.Shell` means `WebView2`, `WinForms` stays an explicit opt-out) only when all of these hold:

1. WAS-55 delivers and updates the WebView2 Runtime with the installer and Windows Service, so the fallback is rare.
2. A test print and a real order receipt have been verified on a physical receipt printer with the app (WAS-54 verified with test mode and a recording printer only).
3. The tray's Print history and Settings entries open the matching app tabs.
4. The classic window remains reachable from Settings for at least one release.

### Runtime dependency and fallback

The shell needs the Microsoft Edge WebView2 Runtime (Evergreen), version 120.0.2210.55 or newer (`WebView2RuntimeProbe`). WAS-53 only detects it. When it is missing or too old, or the shell fails to start (runtime error, page failed to load, browser process exit), the tray opens the classic window and shows one balloon notice per session. Shipping or installing the runtime belongs to WAS-55.

### Security model

- The page is served only from the packaged `shell-ui` folder through a synthetic origin, `https://shell.printbridge.invalid` (`.invalid` never resolves), mapped with `CoreWebView2HostResourceAccessKind.Deny`. No other folder is mapped.
- `index.html` sets a restrictive CSP: `default-src 'none'`, scripts and styles from `'self'` only, `connect-src 'none'`, no frames, objects, workers or forms, `base-uri 'none'`, and Trusted Types (`require-trusted-types-for 'script'`), so `eval` and string-to-HTML sinks fail. No remote scripts, fonts, styles or analytics.
- Every sub-resource request outside the shell origin gets 403 from the host (`WebResourceRequested`), in addition to the CSP.
- Top-level navigation is allowed only to `/index.html` on the shell origin. Frame navigation, new windows and popups, downloads, external URI schemes, basic authentication and every permission request (camera, location, notifications and so on) are denied. Certificate errors are cancelled.
- `ShellSecurityProfile`: host objects, default context menus, autofill, password saving, script dialogs, status bar, swipe navigation, external drops and SmartScreen lookups are off in every build. DevTools and browser accelerator keys are enabled only in Debug builds; Release builds disable them.
- The page has no access to the device token, server URL, installation id, Windows machine name or tenant identifiers: no host message has such fields, and nothing is stored in `localStorage`, `sessionStorage` or cookies. The device name is the one assigned in the Wasla Web panel, or "unknown device".
- The page has no credential fields. Reconnect opens the classic window's connection section (`MainForm.FocusConnectionSettingsSection`), the only place the server URL and token are entered. Resetting the connection asks in a native dialog before the engine clears the token.
- Privileged actions take nothing from the page (`IShellNativeActions` methods have no parameters). Open log folder opens only `PrintBridgePaths.ProgramDataLogDirectory` in File Explorer; logs are never rendered in the page.
- Every state-changing command carries a request id. The host executes an id once (it remembers the last 256), runs at most one operation per group (engine, connection test, reset, printer refresh, printer save, test print, reprint) and answers a second request with `busy`. Busy state shown in the page comes from the host. Engine calls time out after 30 seconds.
- Results are localized resource text. Exception text, server URLs, file paths and raw print errors never reach the page; an unexpected failure shows a generic message.
- History rows carry opaque per-session handles (`h` plus 16 hex digits). Only the host maps a handle to a job, so the page never sees job or order ids, receipt content or error text.

### Message contract (version 2)

Messages are JSON strings `{ "version": 2, "type": "…", "payload": { … } }`, at most 1024 characters from the page (`ShellMessageContract`, `ShellMessageParser`, `shell-model.js`). WAS-54 raised the version from 1; the page and the host ship together.

| Direction | Type | Payload |
|-----------|------|---------|
| page → host | `ui.ready` | `{}`; the host replies with a full snapshot and lists printers once. Repeats are harmless. |
| page → host | `snapshot.request` | `{}` |
| page → host | `language.change` | `{ "culture": "tr-TR" }`; exactly one of `tr-TR`, `en-US`, `ar-SA`, `ru-RU` |
| page → host | `classicWindow.open` | `{}` |
| page → host | `connection.openSetup` | `{}`; opens the native connection setup |
| page → host | `engine.start`, `engine.stop` | `{ "requestId" }`; start is refused while a reconnect is required |
| page → host | `connection.test`, `connection.reset`, `printers.refresh`, `printer.testPrint`, `logs.openFolder` | `{ "requestId" }` |
| page → host | `printer.save` | `{ "requestId", "name" }`; 1–256 characters, no surrounding spaces or control characters, must be an installed printer |
| page → host | `history.query` | `{ "requestId", "range", "page", "search"? }`; `range` is `today`, `last7Days` or `last30Days`, `page` 0–1000, `search` up to 64 characters |
| page → host | `history.reprint` | `{ "requestId", "itemRef" }`; a handle from `history.result` |
| host → page | `snapshot.updated` | Connection, engine, device, printer (with the installed list), activity, latest job, allowed actions, busy flags, diagnostics, test mode, languages and localized strings |
| host → page | `operation.result` | `{ "requestId", "operation", "outcome", "message" }`; outcome `succeeded`, `failed`, `busy`, `rejected` or `cancelled` (a repeated request id gets no second answer) |
| host → page | `history.result` | `{ "requestId", "history" }`; one page of rows |

Request ids match `^[A-Za-z0-9][A-Za-z0-9-]{7,63}$`; the page uses `crypto.randomUUID()`.

Rules:

- The host accepts messages only from the shell document. It rejects unknown types, other versions, unknown or duplicate properties, non-object payloads, unexpected or invalid payload fields, malformed JSON and oversized messages, and logs only the rejection reason and length.
- Commands map to a fixed enum; no page string ever selects a host method, and no host object is exposed.
- The page renders what the host sends and never derives connection or print state. All host messages share one `sequence`; the page drops older or repeated ones. A command is shown as done only when its `operation.result` arrives; the page stops waiting after 45 seconds and then shows host state again. Language changes are applied by the host (same persistence as the classic Settings tab) and confirmed by the next snapshot.
- The app reads the engine through `IPrintBridgeStatusSource` and changes it only through `ShellOperations`, which uses `IPrintBridgeEngine` (start, stop, connection test and reset, test print, history, reprint). It has no timer, thread or HTTP client of its own, so it cannot create a second poll loop. Engine notifications from background threads are coalesced into one UI-thread update, and unchanged snapshots are not resent. Closing the window hides it to the tray, like the classic window.

### Dates, theme and accessibility

- Operational times (orders, print jobs, contact, test print, receipts) use the Gregorian calendar in every language, including Arabic, whose culture default is Umm al-Qura. `GregorianCulture.For` (`Wasla.Application.Printing`) keeps the language's names, separators and era marker and changes only the calendar. It is used by `PrintBridgeDateTimeFormatter` (both windows, the app snapshot and history), `ReceiptLabelLocalizer.GetCulture` (receipt times) and the engine's test print.
- Light or dark follows the Windows app theme: the page uses `prefers-color-scheme`, and the host updates the window background and title bar when the Windows setting changes.
- Tabs follow the ARIA tab pattern: one tab stop, arrow keys that follow the reading direction, Home and End. Unavailable actions use `aria-disabled` so focus is not lost; busy buttons are described as working. The reprint confirmation takes focus and returns it. Results are announced through polite (`status`) and assertive (`alert`) live regions. Styles use logical directions for right-to-left, honor reduced motion and forced colors, and every coloured state also has a text label. The layout needs no horizontal scrolling at the 420 × 420 minimum window, at 200 % zoom or at 320 CSS pixels.

### Future work

- **WAS-55:** Windows Service, installer, WebView2 Runtime delivery and automatic update, then the default-switch decision above.

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- Config section still named `OrderHub` while product is Wasla.
- The WebView2 app is opt-in and has not yet been verified with a physical receipt printer; WAS-54 verification used test mode and a recording printer. Advanced settings, the Logs viewer and setup links still use the classic window.
- Every WebView2 host honors the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable (for example a remote-debugging port). This is platform behavior the shell does not override; anyone who can set the user's environment can already inspect the process.
- Sample / download packaging may still mention older folder names in places; prefer `ServerUrl` and Wasla ProgramData paths above.
- The device details page still formats times on the server with `ToLocalTime()` (the server's zone, not `Europe/Istanbul`) and does not refresh itself. The token regeneration response still returns device times without a UTC marker.
- Live SignalR push for print-job status is future planning only: [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). It is not current implementation. An older copy remains at `docs/orders-printjob-status-signalr.md` until that file is retired.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
