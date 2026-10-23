# Live Screen

**Owns:** Live Screen purpose, routes, Board/List/Focus, snapshot polling, DOM reuse, lazy details, operational attention (sound / browser notifications), and non-regression boundaries versus Orders.

**Does not own:** full order lifecycle rules ([lifecycle.md](./lifecycle.md)), general frontend module map ([../frontend/architecture.md](../frontend/architecture.md)), design tokens ([../frontend/design-system.md](../frontend/design-system.md)), live receipt print status ([../architecture/realtime.md](../architecture/realtime.md)).

---

## Purpose and product split

**Orders (`/orders`) is management / search / history.**

**Live Screen (`/orders/live-display`) is real-time operational handling.**

Current implementation must keep that split. The Live Screen reads one canonical snapshot (`/orders/live-data`). Since WAS-24 a live invalidation (`OrderChanged` over SignalR) makes it read that snapshot as soon as an order is stored or changed, and polling continues as the safety net ([Live invalidation](#live-invalidation-was-24), [../architecture/realtime.md](../architecture/realtime.md)). Do not turn Orders into a second live kitchen surface.

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
- Plus `Delivered` only when `DeliveredAt` is within the last **2 minutes** (null `DeliveredAt` omitted; synchronization leaves it null for a delivery it cannot date, such as one a recovery poll imports, see [lifecycle.md](lifecycle.md#milestone-times)). The value is `LiveScreenVisibility.RecentDeliveredWindow`; the guided-demo success copy shows the same value. Orders leave only the Live Screen; they stay on the Orders page and in history.
- Line items: product name, quantity, notes
- Order-level `CustomerNote` (trimmed; whitespace → null)
- Header counters: today’s orders and today’s cancelled orders by order time (`OrderedAt`, Turkey local day; WAS-125), so a recovery import of yesterday's orders is not counted as today's
- Each order carries `placedAtUtc` (the provider's creation time, or null) beside `receivedAtUtc`. Cards show, time (elapsed) and sort by the order time: `placedAtUtc`, otherwise `receivedAtUtc` under the "Received in Wasla" label (`ordersFullscreenReceived`) instead of "Order time" (`ordersFullscreenOrderTime`). See [lifecycle.md](lifecycle.md#order-times-was-125)

### Views: Board, List, Focus

`orders-live-view.js` + `orders-live-store.js`:

- Modes: `board` (default), `list`, `focus` (legacy stored value `cards` → board)
- Preference key: `Wasla.liveScreen.viewMode` (overridable via `viewModeStorageKey`)
- **One selected view is materially rendered** into `#ordersLiveScreenHost`. Switching view refreshes that host; unused view trees are not kept as duplicate full DOMs
- Board columns group `New`+`Accepted` together; then Preparing, Ready, On the way, Completed (recent Delivered)
- Board cards have one height on desktop and tablet (≥ 768 px): `--wasla-live-board-card-block-size` (20.5rem) on every status, with identity and status at the top, the note and product preview in the middle, and the price and actions on the bottom edge. The product preview keeps its 6.75rem cap and "more in details" line. When a card holds more than fits (a long note, wrapped actions, the practice-order countdown), only the preview gives way, and a `ResizeObserver` keeps that line in step. On phones, Board cards size to their content with a shared minimum (`--wasla-live-board-card-min-block-size`, 15rem). List, Focus and the detail modal keep their own sizing
- Focus: queue + detail panel; narrow viewports toggle queue/detail; detail is loaded lazily
- From 768 px up to the Board's own width (five 280 px columns, about 1,480 px with the page padding; `max-width: 1499.98px`) the Board is wider than the screen. The Board host then fills the screen below the header and scrolls both ways, so its horizontal scrollbar stays on screen instead of under the last card of the longest column (WAS-5). Phones keep the stacked columns and the page scroll; wider screens keep the page scroll
- Each Board column, List group and Focus queue group renders at most one page of orders; see [Large active-order sets](#large-active-order-sets-was-5)
- Focus queue rows show at most two lines of product summary (CSS line clamp); the full text stays in the button's accessible name and `title`, and the selected order lists every item. In the Focus detail panel, item prices share one column at the panel edge and the action bar stays pinned at the bottom of the panel while a long item list scrolls (`.wasla-live-focus .wasla-live-detail__*` in `wasla-theme.css`; the detail modal is unchanged)

### Snapshot coordinator (one poll)

`createRefreshCoordinator` in `orders-live-store.js`:

- Default interval: **10000 ms** (`waslaOrdersOptions.pollingIntervalMs` / `orders-core.js` default). While the live connection is up the interval is **30000 ms** when Web's internal listener is on, and stays **10000 ms** when it is off (WAS-103). Both are rendered by the server (`pollingIntervalMs`, `connectedPollingIntervalMs`, from `LiveScreenPolling`); `setPollingIntervalMs` switches between them ([Live invalidation](#live-invalidation-was-24))
- One in-flight fetch; mutations invalidate and refresh; errors use exponential backoff (cap 30s)
- A read is aborted after `liveDataTimeoutMs` (15 s). A timeout that fires more than 1 s after it was due waited behind a long task on the page, not for the network: the read gets its time back once instead of being discarded (WAS-5)
- First successful snapshot is the **notification baseline** (no sound/notify for pre-existing IDs); so is the first snapshot after the snapshot's training isolation changes (see Order training below)
- Later polls detect new order IDs, then notify **after** render acceptance (notify failure does not roll back the snapshot)

### Incremental DOM reuse

Cards/rows carry `data-live-signature` from `orderContentSignature` (status, display number, platform, customer fields, note, total, timestamps, item product/qty/notes). Matching signature → reuse node (time/elapsed updates only when needed). Mismatched layout or signature → replace/rebuild that node.

The Board's "more in details" line needs each card's product preview measured. Only cards on screen are measured (an `IntersectionObserver`; a card is measured when it scrolls into view, and only on-screen previews are followed by the `ResizeObserver`), and all sizes are read before any class is written. Measuring every card with a class change between reads made the browser lay out the page once per card (WAS-5).

### Large active-order sets (WAS-5)

Unresolved orders never leave the Live Screen by date, so an outage, a test run or a provider problem can leave thousands of active orders. On 14 October 2026 a development tenant had 5,754 (about 5 MB of snapshot) and the page stayed on Loading for minutes.

- Each Board column, List group and Focus queue group renders at most **100** orders (`GROUP_PAGE_SIZE`; `waslaOrdersOptions.liveGroupPageSize` overrides it), newest order time first as sorted. Below the column, a line says how many it shows (`Orders.LiveScreen.GroupShowing`, "Showing {0} of {1}") and a button renders the next page (`Orders.LiveScreen.GroupShowMore`). The button is described by that line, keeps focus while there is more, and then moves focus to the first order it revealed. No request is made.
- Nothing is filtered: the snapshot, the header counts and the column counts still hold every active order, and every order is reachable. Revealed pages are kept for the page view, across polls and view switches. A restaurant-sized column (100 or fewer) looks as before, without the control.
- Always rendered beyond the page: the order holding keyboard focus, the Focus selection, the order whose details are open (so focus can return to its button) and the practice order.
- New orders are the newest, so they are on the first page; the sound, notification and highlight rules are unchanged. An order pushed off the first page by newer ones waits behind "Show more".

Measured in headless Edge on a developer machine with a deterministic copy of that set (5,756 snapshot orders, 3.2 MB; `LargeActiveOrderSeed`, `LiveScreenLargeSnapshotBrowserTests`): before, the first Board frame came 216 s after the response and an unchanged refresh took 154 s; rendering all cards with the overflow fix alone still froze the page for about 40 s after the first frame (`content-visibility: auto` on thousands of cards) or, without `content-visibility`, made every frame take 0.4 to 0.8 s. With paging, the first frame comes about 0.1 s after the response (about 1.4 s after navigation), the page has about 4,300 elements instead of 225,000, an unchanged refresh renders in about 5 ms, and each further page takes 15 to 30 ms up to 1,000 cards. JSON parsing (10 to 30 ms) and the transfer were never the bottleneck. These figures describe one machine and are not a capacity claim.

### Lazy order details

- Board/List: detail modal (`orders-live-detail-modal.js`) loads `/orders/{id}/detail-panel` on demand — **not** one details DOM per card
- Focus: same helper into the focus detail container with cancellation/retry
- Document-level click delegation (cards are replaced on poll)
- The receipt print action inside an open modal or Focus panel follows the server's print status live (Pending, Printing, the outcome) for users who may print manually, and reads it every 10 seconds only while the live connection is down. Closed details are never polled ([../architecture/realtime.md](../architecture/realtime.md))

### Lifecycle actions on Live Screen

Same `orders-actions.js` POST + antiforgery path as Orders. On Live Screen, `liveStore.beginMutation` pauses/invalidates polling until the action completes, then refreshes. Event `wasla:order-action-completed` refreshes open detail UIs.

### Order training (guided setup)

The last guided-setup section (`live-screen-demo`) runs here. Guided setup is Owner-only (see [../product/onboarding.md](../product/onboarding.md)); other roles see the ordinary Live Screen without any training panel. `OrdersController.LiveDisplay` asks `IGuidedSetupCoordinator.GetLiveScreenAsync` (read-only) and renders `_GuidedTrainingPanel` in the page flow above the orders:

- The Owner's first-use Start / Skip decision is on the Dashboard only. The panel has no first-use variant: every role that may use guided setup also has the Dashboard, so nothing is rendered here before the Owner starts.
- **Training** while InProgress and the current section is `live-screen-demo`. Step keys (`GuidedTrainingSteps`): `intro`, `practice-new`, `practice-accepted`, `practice-preparing`, `practice-ready`, `practice-on-the-way`, `practice-delivered`. The step is derived from the saved step and the user's latest practice order (`IGuidedDemoService.GetLatestAsync`), so reloads and closed pages resume correctly.
- The practice order is a `GuidedDemoSession` (not an `Order`): merged into `/orders/live-data` for its owner only, marked `isDemo`, never synced, printed or counted. Browser actions: Approve, Start preparing, Mark ready only (`GuidedDemoTransitions.UserActions`). `OnTheWay` and `Delivered` come only from `Wasla.Worker` (`IGuidedDemoDeliverySimulator`, run at the deadline by `GuidedDemoScheduler`), each `GuidedDemoTiming.StageDuration` (20 s) after the previous status began. When a stage's deadline passes, the countdown asks the store for one read-only refresh (`requestRefresh`, coalesced with the poll) and retries once a second at most 8 times, so the next stage shows near its 20 seconds. A delivered practice order leaves the Live Screen after the same 20 s; real delivered orders keep `RecentDeliveredWindow` (2 min). The practice order's card shows a display-only countdown for these three stages (see [../product/onboarding.md](../product/onboarding.md#practice-order-countdown)).
- Commands: `POST /guided-setup/training/practice` (idempotent start) and `POST /guided-setup/training/complete` (only once the practice order was delivered). Complete, Skip and End take a tenant that is still in Setup live and open the normal Live Screen (see [../product/onboarding.md](../product/onboarding.md)).
- `wasla-guided-training.js` follows `wasla:live-rendered` (which carries the snapshot's `orders`, `training` and `automation`) forward only and outlines the practice order in Board, List and Focus. Real orders never pause, advance or otherwise change training. Escape hides the panel for this page view only. Provider synchronization and polling never stop for training.
- **Per-user isolation during order training:** for the Owner who is in order training, `/orders/live-data` leaves real orders out of `orders` (only the practice order remains) and adds `training: { isolated: true, realOrdersReceived }`: how many orders have a `ReceivedAt` at or after that user's guided-setup start, in any status. This depends only on the user being in order training, not on the tenant's operational mode: an Owner training in an already-live restaurant is isolated too, while everyone else keeps the normal snapshot (`training: null`) and the restaurant's automation keeps running. The header counters are unchanged. The page shows the count in one polite status line (`role="status"`) below the training actions: updated in place, never focused, absent at zero, only the number (`GuidedSetup.Training.RealOrders.*`). Real orders are still synchronized and listed on Orders. If the training state cannot be read, the normal snapshot is returned.
- **Silent first live snapshot:** besides the first snapshot after a page load, the coordinator also treats the first snapshot whose isolation differs from the previous one (entering or leaving isolated training, e.g. the Owner completed or skipped in another tab) as the notification baseline. The orders it reveals get no sound, browser notification or highlight; orders after it are announced as usual. Complete and Skip redirect to the Live Screen, so their first snapshot is a fresh baseline too. Orders that arrived during training then appear by the usual snapshot rules: active ones on the Live Screen, terminal ones only on Orders and in history.
- **Notifications at the practice order:** on the `practice-new` step (the practice order has arrived) the panel explains that real orders play the new-order sound and highlight their card while practice orders stay silent, and that browser notifications are optional and ask for permission once (`GuidedSetup.Training.Notifications.Body`). Its button opens the same notification settings modal as the gear menu; nothing on the panel asks for permission itself.
- Isolation and the count come from the same snapshot. A live invalidation for a real order makes the snapshot read sooner, so the count updates within seconds; it never shows the order, plays a sound, or pauses or advances training. Practice orders are not announced live; the countdown's refresh moves them.

### Customer note and money

- Order note rendered distinctly from item notes (board / list / focus helpers)
- Visible amounts use the shared Live Screen formatter in `orders-live-store.js` (`formatAmount` / `liveMoneyFormat`). Business currency is TRY. The formatter uses `style: "currency"`, `currency: "TRY"`, and `currencyDisplay: "narrowSymbol"`, so the visible symbol is `₺`. Do not render the literal code `TRY` on those amounts, and do not concatenate a `₺` string onto the number. UI culture (`displayCulture`, default `tr-TR`) controls separators and symbol placement only. The code is `OrderCurrency.Code`, which the server-rendered Dashboard and Orders pages also use (`OrderUiHelper.FormatMoney`); a unit test keeps the two the same.
- Line items, on the board, list and focus cards and in the detail, come in the provider's order (`OrderItems.Sequence`), the same order as order details and receipts; see [../frontend/design-system.md](../frontend/design-system.md#tables-and-sorting).

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

Stop Sound: `#ordersLiveDisplayStopSound`. Browser Notification API runs only if settings allow it and permission is `granted`. The Live Screen settings gear shows read-only status in two labelled groups (`role="group"`): Automation (sync, auto-approve, receipt, and the pending-setup hint) and Notifications (sound). Each value is a pill in the active, off, or waiting-for-setup state. It is a Bootstrap dropdown, so Escape closes it and returns focus to the gear. “Manage settings” opens `/settings/orders`. The notification modal is not on the Orders header.

**Notification settings (WAS-13):** the gear menu's “Notification settings” entry (`data-notification-settings-open`) opens the shared notification settings modal (`_NotificationSettingsModalShell`, `GET`/`POST /notification-settings`, `orders-notification-settings.js`), the same one Order Settings uses. It is shown to every Live Screen user, because the preferences are per user and the endpoints need only a signed-in tenant user; “Manage settings” still needs `CanManageOrderSettings`. `O.notificationSettings.show(trigger)` is the one way a page opens it: it ignores a second click while the modal loads, leaves it closed when the load fails, and returns focus to the gear when the modal closes. Saving reloads `/notification-settings/current`, so the menu's sound value follows at once.

Sound, highlight and browser notifications stay independent. Browser permission is asked only when the user turns the browser-notification switch on (`Notification.requestPermission()` while the permission is `default`); opening the modal, testing a sound and saving never ask, and saving never changes the sound or highlight values the user did not touch. The modal states the browser's current permission (`#browserNotificationPermissionHelp`: optional, allowed, blocked, or not supported) without asking.

**Automation indicators (effective state):** for every Live Screen user (`CanViewLiveScreen`: Owner, Manager, Kitchen, Cashier, Viewer), each `/orders/live-data` snapshot carries `automation: { orderSync, autoApprove, autoReceipt }`, each `Active`, `Off` or `PendingSetup`, computed on the server from the resolved tenant's settings row (`ITenantOperationalModeService`). It is read-only operational status; the settings themselves stay behind `TenantManagerOrOwner`, and only those users see “Manage settings”. The indicators show `…` until the first snapshot arrives. `PendingSetup` means configured on while the tenant is in Setup ([../product/onboarding.md](../product/onboarding.md#configured-and-effective-automation)): it is shown as `Orders.Automation.PendingSetup` in a warning tone (neither the green Active nor the Off look), with the visible explanation `Orders.Automation.PendingSetupDescription`. Order sync is never pending, because Setup does not stop ingestion. `orders-automation-status.js` only displays the section from `wasla:live-rendered`; it sends no request and infers nothing from the page. Each poll refreshes it, so completing or skipping guided setup in another tab shows on the next snapshot without a reload. The section never takes part in new-order detection, so the change plays no sound, shows no notification and highlights nothing. If the status cannot be read, the snapshot is returned without it and the indicators keep their last state. A settings change is not announced live: while the live connection is up it shows at the next poll (30 seconds with Web's internal listener on, 10 without it).

### Platform connection status (header)

The badge next to the restaurant name says whether platform orders are actually being synchronized. An open page, a working poll or a successful request is never evidence: every value comes from the tenant's platform connection records, read on each `/orders/live-data` poll for every Live Screen user (`CanViewLiveScreen`). Before WAS-12 the badge was static markup that always said "Live".

The snapshot carries `sync: { state, lastSuccessfulSyncUtc, platforms: [{ platform, state }] }`, computed by `LiveScreenSyncRules` (Application) from `IPlatformConnectionService.GetListAsync` and the tenant's Order Sync setting (`ITenantOperationalModeService`), both for the resolved tenant. Only states travel: no store id, credential, failure count, attempt time or error text.

| State | When | Badge |
| --- | --- | --- |
| `NotConnected` | The tenant has no platform connection | `Orders.LiveDisplay.SyncStatus.NotConnected`, muted, plug icon |
| `SyncDisabled` | Order Sync is off, or every connection is deactivated | `Orders.LiveDisplay.SyncStatus.SyncDisabled`, muted, pause icon |
| `Live` | Every active connection has a successful sync within its stale threshold and did not fail its last attempt | `Orders.LiveDisplay.LiveStatus`, the existing green look with the dot |
| `Issue` | Any active connection failed its last attempt, is paused by the circuit breaker, never completed a sync, or has no successful sync within its threshold | `Orders.LiveDisplay.SyncStatus.IssueWithPlatforms` naming the affected platforms (or `…SyncStatus.Issue`), warning look, triangle icon |

Per connection, the classification is `ProviderConnectionHealthRules.Classify` (the same rule as the Central Admin view) with the Live Screen's own threshold:

- **Evidence of success** is `PlatformConnection.LastSuccessfulSync`, the Worker's checkpoint. It moves only after a fetch window was fetched and stored completely (see [synchronization.md](synchronization.md#checkpoint-and-outage-recovery)). `LastSyncAttempt` is not evidence, and neither is the absence of a recorded failure: a connection with attempts but no successful sync, or whose syncs stopped completing (for example because the Worker is not running), is an issue. While history is still being recovered after an outage, the checkpoint is behind, so the connection is an issue until recovery catches up.
- **Stale threshold** (`LiveScreenSyncRules.StaleAfter`): three expected syncs, each the connection's own `SyncIntervalSeconds` plus one Worker cycle (`OrderSyncSchedule.WorkerCycleSeconds`, 15 s), because a due connection waits for the next cycle. With the default 30-second interval that is 135 seconds. The Central Admin view keeps its longer presentation threshold (10 minutes, `ProviderConnectionHealthRules.StaleAfter`).
- **Several platforms:** one unhealthy active connection makes the whole badge an issue and is named; healthy platforms never hide it. Deactivated connections are reported as `SyncDisabled` and do not affect the active ones.
- **Last successful sync** (meta line, `Orders.LiveDisplay.LastSync`): the oldest last successful sync of the active connections, so everything up to that time came in from every active platform. Hidden while an active connection has never synced. Shown as a time on the server's current day in the page's time zone, otherwise with the date.

`orders-platform-sync-status.js` only displays the section from `wasla:live-rendered`; it sends no request. The badge is a polite `role="status"` region whose words carry the state (the dot and icons are `aria-hidden`), written only when the text changes, and it shows `Common.Loading` until the first snapshot. When the section is missing (the status could not be read on the server) or a poll fails (`wasla:live-poll-status` from the coordinator: stale or session expired), the badge shows the issue state, because the screen cannot back up "Live"; the next accepted snapshot shows the server's state again. The section takes no part in new-order detection, sound or notifications.

### Restaurant logo (header)

Added in WAS-127. The box before the restaurant name shows the tenant's own logo when it has one, otherwise the storefront icon (`bi-shop`). The logo is decorative (`alt=""`, the box is `aria-hidden`): the restaurant name beside it names the restaurant.

- **Managing it:** Account Settings (`/settings/account`, `CanManageTenantSettings`, Owner only) uploads, previews, replaces and removes it. Other roles see it and cannot change it; the server enforces this, not the hidden page.
- **Storage:** one `RestaurantLogos` row in the tenant's own database (`IRestaurantLogoService`), apart from `TenantOperationalSettings` so reading settings never loads the image. There is no file path: the bytes, the media type detected from them and a version (a hash of the bytes) are stored. A user-supplied file name or media type is never used.
- **Accepted images** (`RestaurantLogoImage`): PNG, JPEG or WebP, recognized from the content; at most 1 MB and 4096 × 4096 pixels. SVG, GIF and everything else are refused with a localized message. The pixel data is not decoded: a damaged image that passes shows blank or partial; a response that is not an image at all falls back to the storefront icon.
- **Serving:** `GET /restaurant-logo?v={version}` (`CanViewLiveScreen`) serves the current tenant's logo from tenant resolution, with `nosniff`, a `sandbox` CSP and `Cross-Origin-Resource-Policy: same-origin`. The current version may be cached for good (`immutable`); an old or missing version is served `no-cache` with an ETag, and no logo is `404`, `no-store`. A page rendered after a replacement or removal therefore never shows the old image.
- **Rendering:** the header is rendered once with the page and is not part of the polled snapshot, so polling never re-renders or re-requests it. A Live Screen already open keeps the logo it loaded until it is reloaded. A failed read of the logo shows the storefront icon and never breaks the page. The logo is contained (`object-fit: contain`) in the icon's 3rem box, with no border. Uploaded images are stored as they are (no background removal): a logo with its own white background shows as a white square. The upload hint recommends a square, simple icon with a transparent background (PNG or WebP).
- **Background style:** the Owner chooses what the header draws behind the logo on Account Settings (`POST /settings/account/logo/background`, same Owner-only policy and antiforgery as the upload). It is stored on the logo's row as `RestaurantLogoBackground` (int):

  | Style (form value) | Light theme | Dark theme |
  | --- | --- | --- |
  | Theme-aware (`theme-aware`, 0, default and recommended) | transparent | subtle light surface |
  | Transparent (`transparent`, 1) | transparent | transparent |
  | Light (`light`, 2) | subtle light surface | subtle light surface |
  | Dark (`dark`, 3) | subtle dark surface | subtle dark surface |

  Only these four form values are accepted; anything else (another case, a number, an unknown word, a missing value) is `400` and changes nothing. Changing the style changes neither the image nor its version, so nothing is uploaded or fetched again. Replacing the image keeps the style; removing the logo removes it, and the next logo starts at Theme-aware. The style is a CSS class on the header box (`wasla-logo-bg--*`, wasla-theme.css) whose surfaces come from custom properties of the nearest `[data-bs-theme]`, so a theme switch restyles the logo without a request. A surface has a hairline padding and an inset 1px ring at very low opacity, never a raised edge. Account Settings shows the light-theme and dark-theme headers side by side and restyles both as soon as another option is picked. A logo that fails to load drops the logo classes, so the plain storefront tile is shown whatever the style.

**Implementation quirk:** `localStorage` key `Wasla.soundUnlocked` is written when unlock succeeds and is not read on load. It is a cleanup candidate, not part of the audio contract.

### Live invalidation (WAS-24)

`orders-live-realtime.js` connects the page's single live connection (`wasla-realtime.js`, loaded for every Live Screen user through `_LiveConnectionScripts`) to the snapshot coordinator. Details: [../architecture/realtime.md](../architecture/realtime.md#live-screen-invalidation-was-24).

- The hub joins every Live Screen user (`CanViewLiveScreen`) to the tenant's `live-screen` group; Kitchen and Viewer do not get print status.
- An `OrderChanged` (an ingested order, an operator action, automatic approval) carries the order's `ChangeVersion`; each snapshot order carries its `changeVersion` too. When the snapshot on screen already shows the order at that version or later, nothing is read; otherwise it calls `liveStore.requestRefresh()`. Notifications within 150 ms are read together; notification reads are at least 1 s apart; the coordinator keeps one request in flight with one follow-up.
- Connected: polling every 30 s when Web's internal listener (`Realtime:Internal`) is on. When it is off (missing or disabled), Worker and Api changes cannot be pushed, so a connected screen keeps polling every 10 s and new platform orders, their sound and browser notification still show within 10 s (WAS-103, [startup policy](../architecture/realtime.md#startup-policy-was-103)). Not connected: every 10 s. Every (re)connect, including a back/forward-cache restore, reads the snapshot at once.
- The browser's own connection cannot tell whether Worker changes reach Web: a Worker whose port or secret differs from Web's leaves a connected screen at 30 s with Worker orders up to 30 s late. Outside Development, Worker and Api refuse to start without the channel unless it is explicitly disabled.
- The page is "connected" only after the hub's `tenantEventsReady` (sent once the groups are joined), so the read on (re)connect cannot miss a change made just before the join.
- Nothing in the notification is rendered. New-order detection, sound, browser notifications, highlight, the silent baseline and training isolation run exactly as for a poll, so a new order is announced once.

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
5. **Polling remains** as the safety net (10 s while not connected; while connected, 30 s with Web's internal listener on and 10 s without it). Do not remove it without measurements (WAS-24). The snapshot stays the only source the Live Screen renders.
6. Do not put Notification Settings back on the Orders header; operational attention stays Live Screen / settings routes.
7. **Bounded columns** — a column renders one page of orders at a time; the counts and "Show more" keep every active order visible and reachable. Do not render thousands of cards at once (WAS-5).

---

## Future

- Optional push for order deltas; even then, reconciliation polling would likely remain (see PrintJob future note for the same reliability pattern).
- Order deltas instead of invalidation, if snapshot reads ever need to shrink (not measured yet).
- A connected polling rate chosen from measurements (30 s is a starting value).

---

## Source map

| Concern | Location |
| --- | --- |
| Routes / comments | `src/Wasla.Web/Areas/Tenant/Controllers/OrdersController.cs` |
| Live page | `…/Views/Orders/LiveDisplay.cshtml`, `_OrdersDisplayLayout.cshtml` |
| Snapshot | `src/Wasla.Infrastructure/Services/OrderReadService.cs` |
| Coordinator + render | `wwwroot/js/orders/orders-live-store.js` |
| Live invalidation | `wwwroot/js/orders/orders-live-realtime.js`, `Views/Orders/_LiveConnectionScripts.cshtml` |
| View mode | `wwwroot/js/orders/orders-live-view.js` |
| Detail modal | `wwwroot/js/orders/orders-live-detail-modal.js` |
| Page init | `wwwroot/js/orders/orders-live-display-page.js` |
| Audio / notifications | `wwwroot/js/orders/orders-audio.js` |
| Automation indicators | `wwwroot/js/orders/orders-automation-status.js`, `src/Wasla.Application/Abstractions/Setup/TenantAutomationStatus.cs` |
| Platform connection status | `wwwroot/js/orders/orders-platform-sync-status.js`, `src/Wasla.Application/Orders/LiveScreenSyncRules.cs`, `src/Wasla.Application/Orders/OrderSyncSchedule.cs` |
| Orders bootstrap | `wwwroot/js/orders/orders-page.js` |
| Order training | `…/Views/Orders/_GuidedTrainingPanel.cshtml`, `wwwroot/js/wasla-guided-training.js`, `src/Wasla.Web/GuidedSetup/GuidedSetupCoordinator.cs` |
