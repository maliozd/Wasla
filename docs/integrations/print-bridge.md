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

## CLI helpers

See [../operations/cli.md](../operations/cli.md):

- `generate-print-bridge-token`
- `seed-print-job`
- `list-print-jobs`

## Known gaps / debt

- Config section still named `OrderHub` while product is Wasla.
- Sample / download packaging may still mention older folder names in places; prefer `ServerUrl` and Wasla ProgramData paths above.
- Live SignalR push for print-job status is future planning only: [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). It is not current implementation. An older copy remains at `docs/orders-printjob-status-signalr.md` until that file is retired.

## Related docs

- [../orders/synchronization.md](../orders/synchronization.md) (orders → receipts pipeline entry)
- [../operations/local-development.md](../operations/local-development.md)
- [../operations/deployment.md](../operations/deployment.md)
