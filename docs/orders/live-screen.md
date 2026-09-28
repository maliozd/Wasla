# Live Screen

**Owns:** Live Screen purpose, routes, Board/List/Focus, snapshot polling, DOM reuse, lazy details, operational attention (sound / browser notifications), and non-regression boundaries versus Orders.

**Does not own:** full order lifecycle rules ([lifecycle.md](./lifecycle.md)), general frontend module map ([../frontend/architecture.md](../frontend/architecture.md)), design tokens ([../frontend/design-system.md](../frontend/design-system.md)), future PrintJob SignalR ([../future/printjob-status-signalr.md](../future/printjob-status-signalr.md)).

---

## Purpose and product split

**Orders (`/orders`) is management / search / history.**

**Live Screen (`/orders/live-display`) is real-time operational handling.**

Current implementation must keep that split. Live Screen intentionally polls. SignalR is **not** implemented for Live Screen (or PrintJob status). Do not turn Orders into a second live kitchen surface.

---

## Current implementation

### Routes and pages

| Route | Role |
| --- | --- |
| `GET /orders` | Management Index: filters, paged table, open Live Screen link |
| `GET /orders/history` | Redirects to `/orders` with query preserved |
| `GET /orders/table` | HTML table partial used after lifecycle actions on Orders (not Live Screen polling) |
| `GET /orders/live-display` | Live Screen page (`LiveDisplay.cshtml`, layout `_OrdersDisplayLayout`) |
| `GET /orders/live-data` | JSON snapshot polled by Live Screen |
| `GET /orders/live-screen` | Legacy HTML card partial; **not** what the Live Screen coordinator polls today |
| `GET /orders/{id}/detail-panel` | Lazy HTML detail for modal / Focus panel |
| `POST /orders/{id}/approve` … `mark-ready` | Lifecycle actions (shared with Orders). Mark ready is the last restaurant action: `OnTheWay` and `Delivered` come from provider sync (see [lifecycle.md](lifecycle.md)) |

Controller comments that say the UI “still polls the HTML partial” are **stale relative to JS**: `orders-live-store.js` fetches `liveDataUrl` (`/orders/live-data`).

Authorization: Live Screen actions use `CanViewLiveScreen` where applied on those endpoints.

### Snapshot query

`OrderReadService.GetLiveScreenSnapshotAsync`:

- Includes active statuses: `New`, `Accepted`, `Preparing`, `ReadyForPickup`, `OnTheWay`
- Plus `Delivered` only when `DeliveredAt` is within the last **2 minutes** (null `DeliveredAt` omitted). The value is `LiveScreenVisibility.RecentDeliveredWindow`; the guided-demo success copy shows the same value. Orders leave only the Live Screen; they stay on the Orders page and in history.
- Line items: product name, quantity, notes
- Order-level `CustomerNote` (trimmed; whitespace → null)
- Header counters: today’s received count and today’s cancelled count (Turkey local day)

### Views: Board, List, Focus

`orders-live-view.js` + `orders-live-store.js`:

- Modes: `board` (default), `list`, `focus` (legacy stored value `cards` → board)
- Preference key: `Wasla.liveScreen.viewMode` (overridable via `viewModeStorageKey`)
- **One selected view is materially rendered** into `#ordersLiveScreenHost`. Switching view refreshes that host; unused view trees are not kept as duplicate full DOMs
- Board columns group `New`+`Accepted` together; then Preparing, Ready, On the way, Completed (recent Delivered)
- Focus: queue + detail panel; narrow viewports toggle queue/detail; detail is loaded lazily

### Snapshot coordinator (one poll)

`createRefreshCoordinator` in `orders-live-store.js`:

- Default interval: **10000 ms** (`waslaOrdersOptions.pollingIntervalMs` / `orders-core.js` default)
- One in-flight fetch; mutations invalidate and refresh; errors use exponential backoff (cap 30s)
- First successful snapshot is the **notification baseline** (no sound/notify for pre-existing IDs)
- Later polls detect new order IDs, then notify **after** render acceptance (notify failure does not roll back the snapshot)

### Incremental DOM reuse

Cards/rows carry `data-live-signature` from `orderContentSignature` (status, display number, platform, customer fields, note, total, timestamps, item product/qty/notes). Matching signature → reuse node (time/elapsed updates only when needed). Mismatched layout or signature → replace/rebuild that node.

### Lazy order details

- Board/List: detail modal (`orders-live-detail-modal.js`) loads `/orders/{id}/detail-panel` on demand — **not** one details DOM per card
- Focus: same helper into the focus detail container with cancellation/retry
- Document-level click delegation (cards are replaced on poll)

### Lifecycle actions on Live Screen

Same `orders-actions.js` POST + antiforgery path as Orders. On Live Screen, `liveStore.beginMutation` pauses/invalidates polling until the action completes, then refreshes. Event `wasla:order-action-completed` refreshes open detail UIs.

### Customer note and money

- Order note rendered distinctly from item notes (board / list / focus helpers)
- Visible amounts use the shared Live Screen formatter in `orders-live-store.js` (`formatAmount` / `liveMoneyFormat`). Business currency is TRY. The formatter uses `style: "currency"`, `currency: "TRY"`, and `currencyDisplay: "narrowSymbol"`, so the visible symbol is `₺`. Do not render the literal code `TRY` on those amounts, and do not concatenate a `₺` string onto the number. UI culture (`displayCulture`, default `tr-TR`) controls separators and symbol placement only.

### Audio and browser notifications (Live Screen ownership)

Runtime lives in the Live Screen browser context:

- Scripts: `orders-audio.js`, settings load via `orders-notification-settings.js` on Live Display init
The saved sound preference and the runtime browser audio state are different.

Saved preference: `NewOrderSoundEnabled` (and related fields) from `/notification-settings/current`.

Runtime state in `orders-audio.js`: `unknown`, `allowed`, or `blocked`. Page load starts at `unknown`.

| Saved preference | Runtime state | What the operator sees |
| --- | --- | --- |
| Disabled | any | Sound is off. No browser-audio warning. |
| Enabled | `unknown` | Sound preference is active. No browser-audio warning. |
| Enabled | `allowed` | Sound is active. No browser-audio warning. |
| Enabled | `blocked` | The browser-audio warning may be shown. |

`blocked` is set only when `audio.play()` rejects with `NotAllowedError`. First load, no prior gesture, `AbortError`, and any other play failure are not `blocked`.

`sessionUnlocked` is an in-memory flag set when a play in this tab succeeds. It is not the permission state. Do not treat `sessionUnlocked === false` as blocked audio.

Stop Sound: `#ordersLiveDisplayStopSound`. Browser Notification API runs only if settings allow it and permission is `granted`. The Live Screen settings gear shows sync, auto-approve, receipt, and sound status. “Manage settings” opens `/settings/orders`. The notification modal is not on the Orders header.

**Implementation quirk:** `localStorage` key `Wasla.soundUnlocked` is written when unlock succeeds and is not read on load. It is a cleanup candidate, not part of the audio contract.

### Responsive / RTL / motion

- Live layout: `_OrdersDisplayLayout` sets `lang` / `dir` (Arabic → RTL) and loads `rtl.css`
- View-switch animation respects `prefers-reduced-motion`
- Theme CSS contains Live Screen operational density styles (Live layout currently loads `wasla-theme.css`, not `wasla-foundation.css` — see design-system doc)

### Orders page boundary (explicit)

`orders-page.js`: Orders is search/history — **no** live polling start, sound, highlight, or browser notifications on that page.

`Index.cshtml` header exposes “Open Live Display”; Notification Settings are **not** on the Orders header.

`orders-table.js` still exposes `initPolling` against `/orders/table`, but Live Display does not call it; Orders Index does not start it on load. After an action on Orders, the table refreshes once.

---

## Non-regression list

1. **Orders must not become the Live Screen** — no kitchen polling/sound ownership on `/orders`.
2. **No duplicate view DOM** — only the selected Board/List/Focus tree is rendered in the host.
3. **No details DOM per card** — lazy modal / Focus panel only.
4. **No polling per view** — one snapshot coordinator for the page.
5. **Polling remains** until a deliberate SignalR (or other push) phase; Live Screen polling stays canonical until then.
6. Do not put Notification Settings back on the Orders header; operational attention stays Live Screen / settings routes.

---

## Future

- Optional push for order deltas; even then, reconciliation polling would likely remain (see PrintJob future note for the same reliability pattern).
- PrintJob live button states: planning only in [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md).

---

## Source map

| Concern | Location |
| --- | --- |
| Routes / comments | `src/Wasla.Web/Areas/Tenant/Controllers/OrdersController.cs` |
| Live page | `…/Views/Orders/LiveDisplay.cshtml`, `_OrdersDisplayLayout.cshtml` |
| Snapshot | `src/Wasla.Infrastructure/Services/OrderReadService.cs` |
| Coordinator + render | `wwwroot/js/orders/orders-live-store.js` |
| View mode | `wwwroot/js/orders/orders-live-view.js` |
| Detail modal | `wwwroot/js/orders/orders-live-detail-modal.js` |
| Page init | `wwwroot/js/orders/orders-live-display-page.js` |
| Audio / notifications | `wwwroot/js/orders/orders-audio.js` |
| Orders bootstrap | `wwwroot/js/orders/orders-page.js` |
