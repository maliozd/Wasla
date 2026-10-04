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
WebView2 status shell (packaged HTML/CSS/JS, opt-in)   Classic WinForms window (default)
        │ narrow, versioned web-message contract                │
        ▼                                                       ▼
Wasla.PrintBridge (WinExe): tray, windows, localization resources, composition root,
                            protocol registration, single-instance setup IPC
        │ the shell reads state through IPrintBridgeStatusSource only;
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

## WebView2 status shell (preview, WAS-53)

A first vertical slice of the modern desktop UI. It shows host-owned state only: connection state (online, connecting, offline, error, not configured, stopped) with a localized explanation, device name, selected printer and its state, last successful contact, today's job and failure counts, last print time, the most recent job of this session, and the dry-run warning. It can change the UI language and open the classic window. Settings, print history, logs, test print and start/stop stay in the classic window until WAS-54.

### Enabling (explicit switch)

The classic WinForms window remains the default. Set `Ui.Shell` in `C:\ProgramData\Wasla\PrintBridge\appsettings.json`:

| `Ui.Shell` | Tray **Open** / double-click opens |
|------------|-------------------------------------|
| missing, empty or `WinForms` | Classic window (WebView2 is never loaded) |
| `WebView2` | WebView2 status shell, if the runtime is usable |
| anything else | Classic window, with a warning in the log |

The key is written back only when it was set. Print history, Settings and automatic setup always open the classic window. Do not make `WebView2` the default before WAS-54 proves feature parity and real-printer behavior.

### Runtime dependency and fallback

The shell needs the Microsoft Edge WebView2 Runtime (Evergreen), version 120.0.2210.55 or newer (`WebView2RuntimeProbe`). WAS-53 only detects it. When it is missing or too old, or the shell fails to start (runtime error, page failed to load, browser process exit), the tray opens the classic window and shows one balloon notice per session. Shipping or installing the runtime belongs to WAS-55.

### Security model

- The page is served only from the packaged `shell-ui` folder through a synthetic origin, `https://shell.printbridge.invalid` (`.invalid` never resolves), mapped with `CoreWebView2HostResourceAccessKind.Deny`. No other folder is mapped.
- `index.html` sets a restrictive CSP: `default-src 'none'`, scripts and styles from `'self'` only, `connect-src 'none'`, no frames, objects, workers or forms, `base-uri 'none'`, and Trusted Types (`require-trusted-types-for 'script'`), so `eval` and string-to-HTML sinks fail. No remote scripts, fonts, styles or analytics.
- Every sub-resource request outside the shell origin gets 403 from the host (`WebResourceRequested`), in addition to the CSP.
- Top-level navigation is allowed only to `/index.html` on the shell origin. Frame navigation, new windows and popups, downloads, external URI schemes, basic authentication and every permission request (camera, location, notifications and so on) are denied. Certificate errors are cancelled.
- `ShellSecurityProfile`: host objects, default context menus, autofill, password saving, script dialogs, status bar, swipe navigation, external drops and SmartScreen lookups are off in every build. DevTools and browser accelerator keys are enabled only in Debug builds; Release builds disable them.
- The page has no access to the device token, server URL, installation id or tenant identifiers: the snapshot model has no such fields, and nothing is stored in `localStorage`, `sessionStorage` or cookies.

### Message contract (version 1)

Messages are JSON strings `{ "version": 1, "type": "…", "payload": { … } }`, at most 1024 characters from the page (`ShellMessageContract`, `ShellMessageParser`, `shell-model.js`).

| Direction | Type | Payload |
|-----------|------|---------|
| page → host | `ui.ready` | `{}`; the host replies with a full snapshot. Repeats are harmless. |
| page → host | `snapshot.request` | `{}` |
| page → host | `language.change` | `{ "culture": "tr-TR" }`; exactly one of `tr-TR`, `en-US`, `ar-SA`, `ru-RU` |
| page → host | `classicWindow.open` | `{}` |
| host → page | `snapshot.updated` | `sequence` plus the localized, formatted `ShellSnapshot` |

Rules:

- The host accepts messages only from the shell document. It rejects unknown types, other versions, unknown or duplicate properties, non-object payloads, unexpected payload fields, malformed JSON and oversized messages, and logs only the rejection reason and length.
- Commands map to a fixed enum; no page string ever selects a host method, and no host object is exposed.
- The page renders what the host sends and never derives connection or print state. It drops snapshots with an older or repeated `sequence`. Language changes are applied by the host (same persistence as the classic Settings tab) and confirmed by the next snapshot.
- The shell reads the engine through `IPrintBridgeStatusSource` (status and change notifications only), so it cannot start, stop or duplicate polling, claim jobs or print. Engine notifications from background threads are coalesced into one UI-thread update, and unchanged snapshots are not resent. Closing the window hides it to the tray, like the classic window.

Raw exception text is never shown in the shell. An unexpected error shows a generic message that points to the classic window's Logs tab.

### Future work

- **WAS-54:** feature parity (settings, print history and reprint, logs, test print, start/stop), real-printer verification, then deciding the default shell.
- **WAS-55:** Windows Service, installer, WebView2 Runtime delivery and automatic update.

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- Config section still named `OrderHub` while product is Wasla.
- The WebView2 shell is a preview: it covers status only and has not been verified with a physical printer (WAS-54).
- Every WebView2 host honors the `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` environment variable (for example a remote-debugging port). This is platform behavior the shell does not override; anyone who can set the user's environment can already inspect the process.
- Sample / download packaging may still mention older folder names in places; prefer `ServerUrl` and Wasla ProgramData paths above.
- The device details page still formats times on the server with `ToLocalTime()` (the server's zone, not `Europe/Istanbul`) and does not refresh itself. The token regeneration response still returns device times without a UTC marker.
- Live SignalR push for print-job status is future planning only: [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). It is not current implementation. An older copy remains at `docs/orders-printjob-status-signalr.md` until that file is retired.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
