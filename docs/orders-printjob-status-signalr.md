# Orders PrintJob status — SignalR follow-up

Acceptance criteria for a **future** tenant-isolated SignalR phase. This file does **not** implement SignalR.

The shared print action (`_OrderPrintAction` + `orders-print.js`) is the only UI that should consume these events. Do not add a second print-status implementation.

Intended button labels below are English acceptance criteria. Do not hardcode them in UI; use SharedResource when the SignalR phase is implemented.

## Phase 2B5 temporary behavior (current)

Phase 2B5 queues a receipt through the existing Print Bridge pipeline. The open browser UI does **not** receive later PrintJob transitions.

Until the SignalR phase exists, the accepted temporary behavior is:

- The clicked button changes immediately to the queued state (`In print queue`, disabled).
- Reopening the lazy Live Screen order-detail modal fetches a fresh server snapshot and shows the current PrintJob state.
- The full order-details page does not poll; it requires a reload to pick up later statuses.
- This limitation is accepted. Do not add PrintJob-status polling as a substitute.

The UI currently reflects the PrintJob state returned when the Live Screen detail modal or the full order page is rendered. After the job is queued, Print Bridge can move it through `Pending → Printing → Printed/Failed`, but the open browser UI does not yet receive that change. Do not claim the UI already has live Printed / Printing / Failed states.

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

- SignalR events are notifications, not the source of truth.
- On reconnect, page reload, or modal reopen, fetch/render the current server state.
- Out-of-order or duplicate events must not regress a newer status.
- Closing and reopening the modal must not accumulate handlers.
- Multiple browser tabs may receive the event, but must not create new print jobs or duplicate side effects.
- Do not add polling solely for PrintJob status once this SignalR path exists.
