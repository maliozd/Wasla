# Wasla Print Bridge

## Purpose and scope

This document describes the Windows Print Bridge client and how it talks to Wasla for receipt jobs.

It owns:

- Process boundary (separate Windows app)
- Setup fields (`ServerUrl`, device token)
- ProgramData paths and polling defaults
- Auth header and job API surface (high level)

It does not own receipt template HTML, order lifecycle, or full tenant UI for devices.

## Process boundary

Wasla Print Bridge is a **separate Windows process** (`Wasla.PrintBridge`).

- Web and Worker create `PrintJob` rows (or call existing print services). They do **not** print to Windows printers directly.
- Print Bridge polls the server, claims jobs, and submits them to local Windows printing.
- Physical paper output is not guaranteed by spooler acceptance alone.

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

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- Config section still named `OrderHub` while product is Wasla.
- Sample / download packaging may still mention older folder names in places; prefer `ServerUrl` and Wasla ProgramData paths above.
- The device details page still formats times on the server with `ToLocalTime()` (the server's zone, not `Europe/Istanbul`) and does not refresh itself. The token regeneration response still returns device times without a UTC marker.
- Live SignalR push for print-job status is future planning only: [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). It is not current implementation. An older copy remains at `docs/orders-printjob-status-signalr.md` until that file is retired.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
