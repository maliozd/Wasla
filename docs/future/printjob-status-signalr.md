# FUTURE / NOT CURRENT ARCHITECTURE — PrintJob status SignalR

> **This document is planning only.**
>
> SignalR is **not** implemented in Wasla for PrintJob (or Live Screen) status.
>
> **Live Screen polling remains the canonical real-time mechanism** for operational orders until a deliberate push phase ships.
>
> Even after a push architecture, **reconciliation polling (or equivalent server re-fetch on reconnect / modal reopen) would likely remain** so events are never the sole source of truth.
>
> Do not change SignalR source — there is none. Do not treat this file as evidence of shipped behavior.

Copied and adapted from the historical note `docs/orders-printjob-status-signalr.md` for the canonical future-docs location. Prefer this path for new planning references.

---

## Purpose

Acceptance criteria for a **future** tenant-isolated SignalR phase that updates shared print-action UI when PrintJob status changes.

The shared print action (`_OrderPrintAction` + `orders-print.js`) is the only UI that should consume these events. Do not add a second print-status implementation.

Intended button labels below are English acceptance criteria. Do not hardcode them in UI; use SharedResource when the SignalR phase is implemented.

---

## Current temporary behavior (until SignalR exists)

Phase 2B5-style flow queues a receipt through the existing Print Bridge pipeline. The open browser UI does **not** receive later PrintJob transitions.

Accepted temporary behavior:

- The clicked button changes immediately to the queued state (`In print queue`, disabled).
- Reopening the lazy Live Screen order-detail modal fetches a fresh server snapshot and shows the current PrintJob state.
- The full order-details page does not poll; it requires a reload to pick up later statuses.
- This limitation is accepted. Do not add PrintJob-status polling as a substitute for the missing SignalR path.

The UI currently reflects the PrintJob state returned when the Live Screen detail modal or the full order page is rendered. After the job is queued, Print Bridge can move it through `Pending → Printing → Printed/Failed`, but the open browser UI does not yet receive that change. Do not claim the UI already has live Printed / Printing / Failed states.

Live Screen **order** snapshot polling (`/orders/live-data`) is separate and remains in force; it is not PrintJob SignalR.

---

## Future SignalR phase: live PrintJob status

The SignalR phase must include tenant-isolated PrintJob lifecycle notifications.

### Publish

Publish an order-scoped PrintJob status event when the existing backend records a meaningful transition:

- Pending
- Printing
- Printed
- Failed
- Cancelled, if applicable

Cover jobs created by both automatic printing and manual printing.

Send the event only to the correct tenant group.

Do not include receipt payload, customer data, printer token, device secret, or other sensitive information. Use only the minimal identifiers/state required to update the UI.

### When the currently displayed order matches the event

Update the existing shared print-action component/state:

| Event status | Intended UI |
| --- | --- |
| `Pending` | Disabled `In print queue` |
| `Printing` | Disabled `Printing…` |
| `Printed` | Successful `Printed` state; enable `Reprint` |
| `Failed` | Visible failed state; enable `Retry` / `Reprint` |
| `Cancelled` | Return to the normal `Print` action where valid |

Apply the same update to:

- an open Live Screen order-detail modal
- an open full order-details page
- any future order-print status indicator that legitimately displays the same state

### Reliability

- SignalR events are **notifications**, not the source of truth.
- On reconnect, page reload, or modal reopen, fetch/render the current server state (reconciliation).
- Out-of-order or duplicate events must not regress a newer status.
- Closing and reopening the modal must not accumulate handlers.
- Multiple browser tabs may receive the event, but must not create new print jobs or duplicate side effects.
- Do not add polling **solely** for PrintJob status once this SignalR path exists; keep reconciliation fetches as above.
- Live Screen order polling is a separate product concern and is not automatically replaced by this PrintJob SignalR phase.

---

## Related current docs

- Live Screen polling and ownership: [../orders/live-screen.md](../orders/live-screen.md)
- Frontend module boundaries: [../frontend/architecture.md](../frontend/architecture.md)
- Order lifecycle (not PrintJob): [../orders/lifecycle.md](../orders/lifecycle.md)
