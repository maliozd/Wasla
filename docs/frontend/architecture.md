# Frontend architecture

**Owns:** how Wasla Web UI is structured today — Razor, Bootstrap, vanilla JS modules, localization wiring, and cross-cutting browser concerns.

**Does not own:** order lifecycle ([../orders/lifecycle.md](../orders/lifecycle.md)), Live Screen operational detail ([../orders/live-screen.md](../orders/live-screen.md)), visual tokens ([design-system.md](./design-system.md)).

---

## Purpose

Describe the **current** frontend stack so contributors extend the existing ASP.NET MVC/Razor + Bootstrap 5 + vanilla JS pattern. Do **not** introduce React, Vue, or a heavy SPA build for MVP.

---

## Current implementation

### Stack

- ASP.NET Core MVC / Razor views and partials
- Bootstrap 5.3 (CDN) + Bootstrap Icons
- Vanilla JavaScript IIFEs under `src/Wasla.Web/wwwroot/js/`
- Shared toast helper `wasla-toast.js`
- No frontend bundler requirement for orders JS (asp-append-version script tags)

### Page composition

1. Layout chooses CSS (`_TenantLayout`, `_OrdersDisplayLayout`, public/admin layouts).
2. View renders markup + `window.waslaOrdersOptions` (or similar) with **localized message dictionaries** from `IStringLocalizer<SharedResource>`.
3. Scripts load in dependency order (core → feature modules → page bootstrap).

Supported UI cultures remain **tr-TR** (default/fallback), **en-US**, **ar-SA**, **ru-RU**. User-facing strings go through SharedResource; do not hardcode new UI copy in Razor/JS.

### `window.WaslaOrders`

`orders-core.js` builds the shared namespace from `waslaOrdersOptions`:

- URLs (table, live data, notification settings, detail panel, print, sync/order settings)
- `pollingIntervalMs` default `10000`
- `messages` map + `getMessage`
- toast/warning helpers, debug flags (`Wasla.ordersDebug`)
- feature attachments: `O.table`, `O.audio`, `O.liveStore`, `O.liveView`, `O.notificationSettings`, …

Other pages may use other option bags; Orders/Live Screen are the densest example.

### Module organization (`wwwroot/js/orders/`)

| File | Role |
| --- | --- |
| `orders-core.js` | Namespace, options, messaging |
| `orders-page.js` | Orders Index filters only (no live poll/sound) |
| `orders-table.js` | Table/card partial refresh helpers; optional `initPolling` (not started by Live coordinator) |
| `orders-actions.js` | Lifecycle POST + confirm + antiforgery |
| `orders-print.js` | Shared print / reprint action UI |
| `orders-audio.js` | Sound unlock, playback, browser notifications |
| `orders-notification-settings.js` | Modal load/save for notification preferences |
| `orders-automation-status.js` | Live settings chips for sync / auto-approve / receipt |
| `orders-sync-settings.js` / `orders-order-settings.js` | Settings screens |
| `orders-live-view.js` | Board/List/Focus selection |
| `orders-live-store.js` | Snapshot coordinator + incremental render |
| `orders-live-detail-modal.js` | Lazy detail fetch |
| `orders-live-display-page.js` | Live Display bootstrap |

### Fetch and antiforgery

- Mutations use `fetch` with `RequestVerificationToken` from a hidden antiforgery input on the page.
- Live snapshot uses GET `/orders/live-data` with abort + timeout.
- Session expiry / login redirect is treated as a hard stop on Live Screen (`onStatus("session")`).

### Localization pattern

Pass strings from Razor into a config object (`messages: { … }`). JS never embeds Turkish/English UI sentences. Cultures and RTL are layout concerns (`html lang` / `dir`, `rtl.css` when Arabic).

### Modals and events

- Bootstrap modals for detail and notification settings.
- Document-level **event delegation** for actions and Live view controls (DOM nodes are replaced on poll).
- Custom event `wasla:order-action-completed` coordinates detail refresh without duplicating lifecycle rules.

### Formatters and density

- Live Screen money uses `Intl.NumberFormat` with `currency: "TRY"` and `currencyDisplay: "narrowSymbol"` so visible amounts show `₺`. UI culture changes separators and placement, not the business currency. Do not print the literal code `TRY` or concatenate `₺` in those formatters. See [../orders/live-screen.md](../orders/live-screen.md).
- Operational screens favor compact Bootstrap controls and Wasla-owned classes; management screens use filter cards and dense tables.

### Responsive, RTL, accessibility

- Viewport meta on layouts; Live Screen has narrow Focus behavior.
- RTL: `dir="rtl"` + `wwwroot/css/rtl.css` (+ logical properties in foundation/theme where applied).
- Focus-visible styles on key controls; `aria-pressed` on view switch; live regions for connection/sound status.
- `prefers-reduced-motion`: Live view transitions and several theme animations disable/simplify when reduce is set.

### Browser-local vs server settings

| Concern | Storage |
| --- | --- |
| Notification sound enabled, sound name, volume, browser notification flag, highlight prefs | Server (notification settings endpoints) |
| Live view mode | `localStorage` (`Wasla.liveScreen.viewMode`) |
| Tenant app light/dark choice | `localStorage` (`Wasla.tenant.theme`); see [Theme](#theme) |
| Central Admin light/dark choice | `localStorage` (`Wasla.theme`); see [Theme](#theme) |
| Sound unlock flag write | `localStorage` (`Wasla.soundUnlocked`) — written; not restored on load today |
| Orders debug badge | `localStorage` (`Wasla.ordersDebug`) |

### Theme

`wwwroot/js/theme-preference.js` is the only code that resolves, applies and stores the light/dark theme. Each layout loads it synchronously in `<head>`, before the stylesheets, and names its app on `<html data-wasla-theme-scope>`: `tenant` for `_TenantLayout` and `_OrdersDisplayLayout` (Live Screen), `admin` for `_AdminLayout`. It sets `data-bs-theme` on `<html>`, and on `<body>` as soon as the parser inserts it, so the first frame is painted in the resolved theme. `theme-mode.js` only drives the toggle buttons (`.wasla-theme-toggle`).

**Scope: browser, per app.** The choice is stored in the browser, not on the user or the tenant. That matches the architecture: there is no per-user preference store in CentralDb or TenantDb, the theme must be known before the first frame (a server or user setting would need a request or a database read on every page), operational devices such as a kitchen Live Screen are often shared by several users, and each tenant host is its own origin, so one tenant's choice never reaches another tenant's pages. Central Admin is served under `/admin` on every host, so it can share an origin with the tenant app; the two apps use separate keys and never read or write each other's.

**Tenant app precedence:**

1. An explicit choice (`Wasla.tenant.theme` is `light` or `dark`), made with the toggle, applies on every tenant page, after reloads and in new browser sessions, until the user toggles again or clears site data.
2. Without one, the operating-system theme (`prefers-color-scheme`) applies, and a later system change updates open pages.
3. A system change never overwrites an explicit choice. There is no "follow the system again" control; clearing site data returns to it.

Open pages of the app follow a choice made in another tab (the `storage` event), so an open Live Screen follows the panel. Any other stored value is ignored. The tenant app does not read the old shared `Wasla.theme` key. Its value is not a reliable tenant choice: the previous script wrote `light` there whenever nothing was stored yet, and the tenant app and Central Admin both wrote to it, so a stored value may have come from either app. A user who had chosen dark before this change gets the system theme until they choose again.

**Central Admin** keeps its existing precedence under its existing key: its stored choice, otherwise light. It does not follow the system theme. Like the tenant app, it now follows a choice made in another Admin tab and no longer writes the key when a page loads, only when the toggle is used.

Not themed: every page that does not use one of the three layouts above has no theme script and no dark styles, so it stays light. That includes the tenant sign-in, forgot-password and reset-password pages, the Central Admin sign-in page, the error page, the public landing, tenant-not-found and tenant-address-required pages, and the signup and status layouts. How the tenant app and the Live Screen are painted in dark mode, and what stays light on purpose, is in [design-system.md](design-system.md#dark-mode-in-the-tenant-app).

---

## Future

- Continue modular vanilla JS; avoid a second parallel orders UI framework.
- Any SignalR consumer should reuse shared print/action components — see [../future/printjob-status-signalr.md](../future/printjob-status-signalr.md). Live Screen order polling remains until a deliberate phase.

---

## Source map

| Concern | Location |
| --- | --- |
| Tenant shell | `Areas/Tenant/Views/Shared/_TenantLayout.cshtml` |
| Live layout | `Areas/Tenant/Views/Shared/_OrdersDisplayLayout.cshtml` |
| Orders JS | `wwwroot/js/orders/*.js` |
| RTL | `wwwroot/css/rtl.css` |
| Localization | `Wasla.Web/Resources/SharedResource*.resx` |
