# Design system

**Owns:** canonical visual direction and how current CSS relates to it.

**Does not own:** page behavior ([../orders/live-screen.md](../orders/live-screen.md)), JS architecture ([architecture.md](./architecture.md)). This file does **not** redesign the product.

This document is the design guidance. `wasla-foundation.css` is the intended token source. `wasla-theme.css` holds current shared component and page styles and is not a second palette. `docs/wasla-design-language-v1.md` is historical rationale only. Do not treat it as a required source.

---

## Purpose

Record warm operational minimalism as the intended look, name the **token source of truth**, and document migration debt where `wasla-theme.css` still carries competing values.

---

## Current vs intended

| Layer | Role today |
| --- | --- |
| This document | Canonical design guidance |
| `wwwroot/css/wasla-foundation.css` | **Intended foundation / token direction** for tenant shell (`body.wasla-tenant-shell`) |
| `wwwroot/css/wasla-theme.css` | Current shared component and page styles, including Live Screen. Not a second official palette |
| Bootstrap defaults | Primitives; Wasla should not globally restyle Bootstrap into a new system |

**Load order (tenant shell):** theme → site → **foundation** (foundation comments: load after theme; Live Screen / public / admin keep `:root` baseline from theme).

**Divergence:** `_OrdersDisplayLayout` (Live Screen) loads `wasla-theme.css` + `site.css` but **does not** load `wasla-foundation.css`. Live Screen therefore still runs on theme `:root` accents (e.g. `#ff6b35`) unless foundation is later included.

---

## Visual direction (approved)

Current direction, aligned with foundation tokens:

- Warm operational minimalism — paper/ink, restrained orange, reliable, dense, restaurant-first
- Restrained radii (controls ~8px, cards ~10–12px, large surfaces ≤ ~14px; pills reserved for chips)
- **Border-first** surfaces; minimal functional shadows only
- Dense operational layouts; avoid decorative whitespace and oversized cards on kitchen/ops screens
- Responsive, RTL-safe (logical properties), accessible focus, respect `prefers-reduced-motion`
- Avoid: excess orange blocks, oversized pills, gradients/glass, purple glow tropes, restyling Bootstrap globally

Character labels from design language: warm, reliable, fast, operational, restaurant-focused — not a consumer delivery app or ERP chrome.

---

## Tokens (foundation = token source)

`wasla-foundation.css` on `body.wasla-tenant-shell` (selected):

| Token | Value | Role |
| --- | --- | --- |
| `--wasla-accent` | `#E87342` | Wasla Orange |
| `--wasla-accent-hover` | `#B94F2A` | Action Orange |
| `--wasla-accent-soft` | `#FBE9DF` | Soft orange surface |
| `--wasla-canvas` | `#F7F4EE` | Application canvas |
| `--wasla-surface` | `#FFFEFA` | Main surface |
| `--wasla-surface-muted` | `#F4F0E9` | Nested / quiet surface |
| `--wasla-border` / `--wasla-border-strong` | `#E3DDD3` / `#D3CABD` | Borders |
| `--wasla-text` / `--wasla-text-secondary` | `#28231F` / `#746D66` | Ink / muted |
| Status family vars | success / warning / danger / info soft pairs | Semantic, not decorative |
| `--wasla-radius-sm/md/lg` | `0.4rem` / `0.65rem` / `0.9rem` | Radius scale |
| `--wasla-space-1` … `6` | `0.25rem` … `1.5rem` | Spacing (aligns with 4/8/12/16/24/32 intent) |
| `--wasla-shadow-sm/md` | subtle warm shadows | Elevation when needed |
| Focus | outline / `--wasla-focus` | Keyboard visibility |

Design language also locks status **families** (sage, amber, blue, muted red, neutral) without freezing every derived pair until contrast is verified. Platform colors stay on platform badges only.

### Migration debt (theme competing values)

`wasla-theme.css` `:root` still defines a parallel accent stack, for example:

- `--color-accent: #ff6b35` (not `#E87342`)
- Cooler grays (`#fafafa`, `#e5e7eb`, `#6c6c6c`) vs warm neutrals
- Bootstrap-ish status (`#0d6efd`, `#198754`, `#dc3545`, `#ffc107`)
- Live status vars: `--wasla-status-new`, `--wasla-status-preparing` (blue), `--wasla-status-on-the-way` (purple), etc.
- Single `--wasla-radius: 0.5rem` vs foundation radius scale

Treat these as **debt to migrate toward foundation tokens**, not a second brand. New work should prefer foundation names when touching tenant shell; do not invent a third palette.

---

## Components (families)

Component families: primary/secondary/quiet/destructive actions, icon buttons, inputs, status vs platform badges, metric cards, content surfaces, dense tables, empty/loading, toast, modal, page header, filter toolbar, lifecycle actions, shared print status action.

Interactive states expected where applicable: default, hover, focus, active, disabled, loading, error.

Operational density: Live Screen cards/columns and Orders filters should stay compact and scannable; do not expand into marketing card grids.

### Tables and sorting

Tenant data tables use the shared `.wasla-table` primitive in `wasla-foundation.css` (inside `.wasla-table-wrap`, which scrolls sideways only): muted uppercase headers, border-first rows, `.wasla-table__num` for amounts and times (end-aligned, tabular figures), `.wasla-table__actions`, `.wasla-table__datetime` (a received time may wrap only between date and time; both tables render it with the shared `_ReceivedAtTime` partial: time only for today, date and time otherwise) and `.wasla-table__empty`. Page CSS only adjusts spacing (`wasla-dash-table` and `wasla-orders-table` in `wasla-theme.css`).

Responsive modifiers:

- `.wasla-table--stack`: below 768 px each row becomes a block of labelled cells (each cell carries a `.wasla-table__cell-label`).
- `.wasla-table--stack-lg`: from 768 to 1199 px each row is a compact card with a three-column grid of labelled cells and the actions on their own line, for tables too wide for tablets (Orders).
- `.wasla-table--sortable`: on stacked layouts the header row becomes a small "sort by" bar that keeps only the sortable headers, so no focusable sort control is ever hidden. A plain stacked table keeps its header for screen readers only.

Sorting rules:

- Paginated or growing lists sort on the server, before `Skip`/`Take`, from an allowlist of column names (never a raw column name or SQL fragment), with a deterministic tie-breaker (newest `ReceivedAt`, then `Id`).
- A sort link keeps every filter, the search and the page size, and returns to page 1. Culture is a cookie, so it is unaffected.
- A small list that is fully rendered on the page sorts in the browser, stably, with a deterministic tie-breaker.
- The sorted column's header cell carries `aria-sort`; the control is a link (server-side) or a button (client-side), keyboard-operable with a visible focus ring; the arrow icon is decorative.
- Action columns and columns without a meaningful or truthful order are not sortable.

| Table | Kind | Sorting |
| --- | --- | --- |
| Orders (`Orders/_OrdersTable.cshtml`) | Sortable data table (paginated) | Server-side: platform, customer, status, total, received, with `aria-sort`, icons and a stable tie-breaker. Order code and actions are intentionally not sortable; search finds a specific order. |
| Users (`TenantUsers/Index.cshtml`) | Sortable data table (complete list on the page) | Client-side, stable (`tenant-users-index.js`): name (display name, else email), role (by permission level, Owner first, not by the translated label), created. Ties fall back to email, then the rendered order. Status, last login and actions are not sortable. Last login is not sortable because tenant users' last login is not recorded yet (the column shows "not recorded"). |
| Dashboard Recent Orders (`Dashboard/Index.cshtml`) | Chronological snapshot | Not sortable: the latest 10, newest first, is its meaning; "View all" opens the sortable Orders list. |
| Print jobs (`PrintBridge/_PrintJobHistory.cshtml`) | Chronological snapshot (capped feed) | Not sortable: the latest 50, newest first, refreshed in place. |
| Order items (`Orders/Details.cshtml`, Live Screen detail) | Ordered item list | Not sortable: an order's items in one fixed order. That order is by item record (`OrderItems.Id`, a GUID), which is stable but not guaranteed to be the provider's sequence. |
| Platform connections (`PlatformConnections/Index.cshtml`) | Small operational list | Not sortable: at most one row per platform (three today). |
| Branches (`Branches/Index.cshtml`) | Small operational list | Not sortable: one location per tenant today. |
| Print Bridge devices (rendered by `print-bridge-page.js`) | Small operational list | Not sortable: few devices, re-rendered on poll. |

### Known limitation: dark mode in the tenant shell

Choosing dark mode does not restyle the tenant shell: the body, cards and tables stay light on every tenant page (for example Branches). This predates the dashboard and table work, which does not fix it; the foundation tokens on `body.wasla-tenant-shell` have no dark values yet. The Live Screen (`_OrdersDisplayLayout`, which does not load the foundation) does render dark. The preference itself is described in [architecture.md](architecture.md#theme).

---

## Logo

The approved Wasla logo is the orange (`#E87342`) lowercase "wasla" wordmark. It has no separate symbol.

| File | Role |
| --- | --- |
| `docs/brand/wasla-logo-source.svg` | The approved master, unchanged: a 612 × 792 page with a white background. Not served. |
| `src/Wasla.Web/wwwroot/images/brand/wasla-logo.svg` | The canonical production asset, derived from the master by two changes only: the `viewBox` is cropped to the wordmark (`213.9 255.4 184.3 68.1`, aspect ratio about 2.71) and the two white background rectangles are removed so it sits on any surface. The glyph paths, transforms and colour are byte-identical. |

Rules:

- Web renders the logo only through the `_WaslaLogo` partial (`WaslaLogoModel`: a placement class, and `Decorative` for an empty alt). Do not inline the SVG, embed it as Base64, redraw it in CSS or replace it with a font.
- Set only the height (`.wasla-logo--sidebar`, `--admin-sidebar`, `--admin`, `--auth`, `--header`, `--status`, `--illustration`); the width follows the aspect ratio. The collapsed tenant rail shrinks the full logo to the rail width rather than widening the rail.
- Alt text is `Brand.LogoAlt` ("Wasla"). Use `Decorative = true` where the logo is inside content already hidden from or named for assistive technology. Plain-text mentions of Wasla stay text.
- One colour works on the light and dark surfaces in use (Central Admin dark mode, the Print Bridge app); there are no logo variants.
- The Live Screen empty state shows the logo as a decorative CSS background of the same file.
- Wasla Print Bridge ships the same file, linked from Wasla.Web at build time rather than copied; see [../integrations/print-bridge.md](../integrations/print-bridge.md).
- `favicon.ico` is still the ASP.NET template icon. The wordmark is not legible at favicon size and has no symbol to extract, so a dedicated Wasla icon has to be designed separately.
- Email templates show "Wasla" as text: SVG images are not reliably displayed by mail clients.

---

## Typography

- System / local font stacks. Do not add a font CDN.
- One semantic `H1` per page; clear section hierarchy
- Validate layout in tr-TR, en-US, ar-SA, ru-RU (Arabic line-height / RTL)

---

## Responsive, RTL, focus, reduced motion

- Mobile tenant nav: off-canvas (foundation/theme implement shell behavior)
- RTL via `dir` + `rtl.css` + logical border/margin where foundation/theme already use them
- Focus rings on Wasla controls; do not remove outline without a visible replacement
- Theme and Live Screen CSS include `@media (prefers-reduced-motion: reduce)` blocks; JS view transitions also check the media query

---

## Future

- Gradual migration of theme accent/status tokens onto foundation values
- Optionally load foundation on Live Display once operational styles are verified against warm tokens
- Do not claim every screen already matches this document

---

## Source map

| Concern | Location |
| --- | --- |
| Canonical guidance | `docs/frontend/design-system.md` |
| Historical rationale (not normative) | `docs/wasla-design-language-v1.md` |
| Foundation tokens | `src/Wasla.Web/wwwroot/css/wasla-foundation.css` |
| Theme / components | `src/Wasla.Web/wwwroot/css/wasla-theme.css` |
| RTL helpers | `src/Wasla.Web/wwwroot/css/rtl.css` |
| Logo (canonical asset, partial) | `src/Wasla.Web/wwwroot/images/brand/wasla-logo.svg`, `Views/Shared/_WaslaLogo.cshtml` |
| Logo master | `docs/brand/wasla-logo-source.svg` |
