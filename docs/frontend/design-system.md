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

`wasla-foundation.css` on `body.wasla-tenant-shell` (selected), with the dark value set on `[data-bs-theme="dark"] body.wasla-tenant-shell` (see [Dark mode](#dark-mode-in-the-tenant-app)):

| Token | Light | Dark | Role |
| --- | --- | --- | --- |
| `--wasla-accent` | `#E87342` | `#f26b3a` | Wasla Orange |
| `--wasla-accent-hover` | `#B94F2A` | `#e35f2e` | Action Orange |
| `--wasla-accent-soft` | `#FBE9DF` | orange at 14 % | Soft orange surface |
| `--wasla-canvas` | `#F7F4EE` | `#0f1115` | Application canvas |
| `--wasla-surface` | `#FFFEFA` | `#171a21` | Main surface |
| `--wasla-surface-muted` | `#F4F0E9` | `#141820` | Nested / quiet surface |
| `--wasla-border` / `--wasla-border-strong` | `#E3DDD3` / `#D3CABD` | white at 8 % / 14 % | Borders |
| `--wasla-text` / `--wasla-text-secondary` / `--wasla-text-muted` | `#28231F` / `#746D66` / `#746D66` | `#f3f4f6` / `#aeb4c0` / `#8b939f` | Ink / secondary / muted |
| Status family vars | success / warning / danger / info soft pairs | light status text on 14 % tints | Semantic, not decorative |
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

- Paginated or growing lists sort on the server, before `Skip`/`Take`, from an allowlist of column names (never a raw column name or SQL fragment), with a deterministic tie-breaker (newest `OrderedAt`, the order time, then `Id`).
- A sort link keeps every filter, the search and the page size, and returns to page 1. Culture is a cookie, so it is unaffected.
- A small list that is fully rendered on the page sorts in the browser, stably, with a deterministic tie-breaker.
- The sorted column's header cell carries `aria-sort`; the control is a link (server-side) or a button (client-side), keyboard-operable with a visible focus ring; the arrow icon is decorative.
- Action columns and columns without a meaningful or truthful order are not sortable.

| Table | Kind | Sorting |
| --- | --- | --- |
| Orders (`Orders/_OrdersTable.cshtml`) | Sortable data table (paginated) | Server-side: platform, customer, status, total, order time (`orderedAt`, the default, newest first; `receivedAt` is still accepted), with `aria-sort`, icons and a stable tie-breaker. The order-time cell names Wasla's import time under it when that stands in for a missing provider time or came 10 minutes or more later ([../orders/lifecycle.md](../orders/lifecycle.md#order-times-was-125)). Order code and actions are intentionally not sortable; search finds a specific order. |
| Users (`TenantUsers/Index.cshtml`) | Sortable data table (complete list on the page) | Client-side, stable (`tenant-users-index.js`): name (display name, else email), role (by permission level, Owner first, not by the translated label), created, last login (by the stored UTC value in `data-sort-last-login`, newest first on the first click; users with no recorded login stay last in both directions). Ties fall back to email, then the rendered order. Status and actions are not sortable. Last login is shown in the page culture followed by "UTC" inside `<time datetime>` (Wasla has no tenant or user time zone setting), or "Not recorded"; the list and details page share `_LastLogin.cshtml`. |
| Dashboard Recent Orders (`Dashboard/Index.cshtml`) | Chronological snapshot | Not sortable: the latest 10 of any day by order time, newest first, is its meaning; "View all" opens the sortable Orders list. Its subtitle and empty state say "latest orders", never "today" (the figures above it are today's), and rows from an earlier day show their date. |
| Print jobs (`PrintBridge/_PrintJobHistory.cshtml`) | Chronological snapshot (capped feed) | Not sortable: the latest 50, newest first, refreshed in place. |
| Order items (`Orders/Details.cshtml`, Live Screen board, list, focus and detail, receipts) | Ordered item list | Not sortable: an order's items, and each item's options, in the order the provider listed them (`Sequence`, written by order sync, WAS-44), then by stored time, then by `Id`. Rows stored before `Sequence` existed all have 0 and use that fallback, which is deterministic and the same on every surface but not guaranteed to be the provider's order. One definition: `OrderItemOrdering` (Infrastructure). |
| Platform connections (`PlatformConnections/Index.cshtml`) | Small operational list | Not sortable: at most one row per platform (three today). |
| Branches (`Branches/Index.cshtml`) | Small operational list | Not sortable: one location per tenant today. |
| Print Bridge devices (rendered by `print-bridge-page.js`) | Small operational list | Not sortable: few devices, re-rendered on poll. |

### Dark mode in the tenant app

The preference (keys, precedence, early application, cross-tab sync, tenant/Admin isolation) is described in [architecture.md](architecture.md#theme). This section covers how dark mode is painted.

**Coverage.** Every page on `_TenantLayout` renders dark: Dashboard, Orders and an order's details, Platform Connections (list, create, edit), Users (list, create, details), Order, Receipt & Printer and Account settings with the notification settings modal, Branches, Print Bridge devices and setup, and Help. So does the Live Screen (`_OrdersDisplayLayout`): Board, List, Focus, the detail modal and the settings menu. Guided setup is covered for a tenant still in Setup: the setup checklist and the guided setup cards on the Dashboard, a section panel, and the Live Screen order-training panel with its practice order. Shared parts are covered with them: the sidebar, the mobile drawer, the sidebar menus, dropdowns, modals, alerts, forms and validation messages, tables and pagination, badges and focus rings, at phone and desktop widths, LTR and RTL.

**How it works.**

- The light token block on `body.wasla-tenant-shell` also re-declares the shared `--color-*` and `--bs-*` aliases from the `--wasla-*` tokens. Those body-level declarations win over the dark values `wasla-theme.css` sets on `[data-bs-theme="dark"]`, which is why the shell used to stay light. `[data-bs-theme="dark"] body.wasla-tenant-shell` now redefines the `--wasla-*` tokens, so every alias and every token-driven rule (including the existing dark component rules in `wasla-theme.css`) resolves dark.
- The dark values are the existing dark palette of `wasla-theme.css`, which the Live Screen already used, so the panel and the Live Screen match and no third palette is added. Two values differ, for contrast: muted text is `#8b939f` (the dark sidebar muted value; `#737b89` is only 4.1:1 on a surface), and the hover orange is a darker step, `#e35f2e` (white on the theme's `#ff7d4f` is 2.5:1).
- Local overrides exist only where a fixed colour prevented theming: the Users avatars and role pills; the Bootstrap utilities `.text-danger` (Bootstrap's `#dc3545` is 3.9:1 on a dark surface), `.text-bg-light`, `.btn-light` and `.table-light`; the dark link colour, which no longer recolours Wasla buttons rendered as links; on the Live Screen, Reject, the "sound is active" pill, muted text (on the Live Screen body only), the column counts and the Trendyol GO plate in List; and the text of the notification highlight preview. The Live Screen does not load the foundation, so its dark body also defines the foundation token names the order-training panel uses (`--wasla-accent`, `--wasla-text-secondary`, …); in light mode the panel keeps its light fallbacks. `--wasla-accent-strong` has a dark value only; light mode uses the component fallback.
- Light on purpose in dark mode: the receipt paper preview, the Trendyol GO logo plate (its wordmark has black lettering) and the highlight colour swatches.
- Known contrast exceptions, in both themes and not introduced by dark mode (they do **not** meet WCAG AA for normal-size text):
  - White text on the Wasla orange of primary buttons is about 3.0:1 (the brand pairing; a hovered button is 3.5:1 in dark mode).
  - Recently completed Live Screen orders are deliberately dimmed and may fall below AA for normal-size text.
  - Disabled options are dimmed, and an unchecked switch's knob is faint.

**Not themed (light only).** Pages that do not use `_TenantLayout`, `_OrdersDisplayLayout` or `_AdminLayout` have no theme script and stay light; see [architecture.md](architecture.md#theme) for the list (sign-in and password pages, the error page, public, signup and status pages). Central Admin keeps its own dark mode, unchanged: the tenant dark tokens are scoped to `body.wasla-tenant-shell` and the Live Screen body.

**Verification.** `TenantDarkModeBrowserTests` checks in a real browser that no visible surface is light and that visible text keeps WCAG AA contrast on the pages and states above, except the known contrast exceptions listed in How it works, which the test exempts; that light mode keeps its surfaces, and that a theme choice restyles open pages, also with storage unavailable. `TenantDarkThemeContractTests` keeps every literal colour token of the shell paired with a dark value. See [../operations/testing.md](../operations/testing.md).

---

## Logo

The approved Wasla logo is the orange (`#E87342`) lowercase "wasla" wordmark. It has no separate symbol.

| File | Role |
| --- | --- |
| `docs/brand/wasla-logo-source.svg` | The approved master, unchanged: a 612 × 792 page with a white background. Not served. |
| `src/Wasla.Web/wwwroot/images/brand/wasla-logo.svg` | The canonical production asset, derived from the master by two changes only: the `viewBox` is cropped to the wordmark (`213.9 255.4 184.3 68.1`, aspect ratio about 2.71) and the two white background rectangles are removed so it sits on any surface. The glyph paths, transforms and colour are byte-identical. |

Rules:

- Web renders the logo only through the `_WaslaLogo` partial (`WaslaLogoModel`: a placement class, and `Decorative` for an empty alt). Do not inline the SVG, embed it as Base64, redraw it in CSS or replace it with a font.
- Set only the height (`.wasla-logo--sidebar`, `--admin-sidebar`, `--admin`, `--auth`, `--header`, `--status`, `--marketing`, `--hub`); the width follows the aspect ratio. The collapsed tenant rail shrinks the full logo to the rail width rather than widening the rail.
- Alt text is `Brand.LogoAlt` ("Wasla"). Use `Decorative = true` where the logo is inside content already hidden from or named for assistive technology. Plain-text mentions of Wasla stay text.
- One colour works on the light and dark surfaces in use (Central Admin dark mode, the Print Bridge app); there are no logo variants.
- The Live Screen empty state shows the logo as a decorative CSS background of the same file.
- Wasla Print Bridge ships the same file, linked from Wasla.Web at build time rather than copied; see [../integrations/print-bridge.md](../integrations/print-bridge.md).
- `favicon.ico` is still the ASP.NET template icon. The wordmark is not legible at favicon size and has no symbol to extract, so a dedicated Wasla icon has to be designed separately.
- Email templates show "Wasla" as text: SVG images are not reliably displayed by mail clients.

---

## Typography

- System / local font stacks. Do not add a font CDN.
- Exception: the public marketing pages (see [Public marketing site](#public-marketing-site)) self-host the two Wasla Marketing typefaces from `wwwroot/fonts/`. The tenant app, Central Admin and the Live Screen keep system fonts.
- One semantic `H1` per page; clear section hierarchy
- Validate layout in tr-TR, en-US, ar-SA, ru-RU (Arabic line-height / RTL)

---

## Responsive, RTL, focus, reduced motion

- Mobile tenant nav: off-canvas (foundation/theme implement shell behavior)
- RTL via `dir` + `rtl.css` + logical border/margin where foundation/theme already use them
- Focus rings on Wasla controls; do not remove outline without a visible replacement
- Theme and Live Screen CSS include `@media (prefers-reduced-motion: reduce)` blocks; JS view transitions also check the media query
- Under reduced motion AdminLTE 4.0.0-rc3 sets `.fade{display:block}`; `wasla-theme.css` keeps closed modals (`.modal.fade:not(.show)`) hidden. Do not override `.fade` itself: Bootstrap's own rules already keep closed toasts, tab panes and collapses hidden

---

## Public marketing site

**Owns:** the homepage (`/`), the Wasla POS page (`/pos`), the contact page (`/contact`), the privacy notice of the contact form (`/privacy-notice`), and the shared header and footer of `_PublicLayout` (WAS-155). The homepage, contact page, privacy notice, header and footer follow the Figma file *Wasla Marketing*, page "Wasla Landing — Refined Direction" (`259:496`: desktop `265:729`, mobile `271:1372`, contact and privacy notice in section 06). The `/pos` page body still follows the earlier POS frame `18:2`. Figma is the visual source only; what the pages may claim follows the product state and the decisions below.

- **Styles.** `wasla-marketing.css`, every rule scoped to a `wm-` class, so tenant, Admin and the other public pages are unaffected. `landing.css` still styles the signup, checkout, tenant-not-found and tenant-address-required pages. The marketing tokens (`--wm-*`, on `.wasla-landing`) are the Figma design-system values; brand orange, action orange, canvas, surface, borders and ink equal the foundation tokens above. The extra peach, sand and ink surfaces come from the same Figma file; they are not a second palette.
- **Type.** Bricolage Grotesque (headings; the display sizes use its ExtraBold at the 96 pt optical size), Figtree (text) and IBM Plex Mono (eyebrows, captions, footer headings and the kitchen ticket), all SIL Open Font License 1.1 with their `OFL.txt`, self-hosted as Latin and Latin Extended subsets (Turkish included). Arabic and Cyrillic have no glyphs in them and use the system font; Arabic labels keep normal letter spacing. Mockup UI text uses the system font instead of Figma's Inter.
- **Primary action.** Action orange `#B94F2A` with white text (4.9:1). Focus is a 2 px action-orange outline with a soft brand-orange ring; the brand orange alone is 3.0:1 on white and is not used as the only focus indicator.
- **Responsive.** Figma has 1440 px desktop and 390 px mobile frames. From 992 px the homepage hero ends with the order-flow strip (platforms, order, Print Bridge, kitchen printer); below it the strip is replaced by the vertical order flow of the mobile frame. The Live Screen mockup shows two columns below 992 px and one order per column below 768 px. Below 1100 px the header navigation folds into a menu button and sign-in sits next to it. Checked in a real browser at 1440, 768 and 390 px with no horizontal scroll, in Turkish, English and Arabic (right to left).
- **Header.** On the homepage the header lies over the hero photo, transparent with light text (`PublicLayoutViewData.HeaderOverlay`); every other public page keeps the solid canvas header. The language menu is in the footer.
- **Behaviour.** `wasla-marketing.js` opens the language menu and the mobile menu on click or keyboard (never on hover); Escape closes the open menu and returns focus. Without JavaScript the mobile navigation stays visible.
- **Mockups.** Product screens are decorative markup, not images: `aria-hidden="true"`, with the surrounding text carrying the content. Their sample orders (dish names, customer notes) are order data and are shown as such in every culture; their labels come from the app's own resources where the app has them. Wasla POS screens are labelled "Konsept arayüz" (concept interface).
- **Photos.** Real restaurant-kitchen photos from Pexels (free licence, commercial use allowed), in `images/marketing/` and credited on the page: Saad Emris (hero), Hasan Hüseyin Turan (Print Bridge), Yasin Onuş (closing panel, desktop), Esra Erdem (closing panel, phone), Rachel Claire (phone order flow). The Print Bridge screens are the app's own interface (`Wasla.PrintBridge/WebShell`) in its dark theme with sample data, and `images/brand/wasla-app-icon.svg` is the app's icon (the wordmark's first letter on the orange tile, the recipe of `WaslaAppIcon.cs`).
- **Platform marks.** The four platforms appear as logos only, without any integration status (decision of 2026-10-27): Trendyol GO and Yemeksepeti use the repository assets the app already shows, GetirYemek uses `getiryemek-plate.svg` (the purple plate, for light surfaces) and Migros Yemek `migrosyemek.svg`, both exported from the Figma file. The Trendyol GO wordmark comes from a third-party logo site, and permission to use each platform's mark in marketing is not confirmed yet.
- **Truthfulness.** No integration status (active, planned, rolling out) is written anywhere on the homepage. No "coming soon" label is shown next to Wasla POS on the homepage or in the shared header and footer (decision of 2026-10-27); the homepage POS section describes the product as being built and labels its screen as concept interface. The `/pos` page keeps its own notice. Calls to action lead only to existing flows: signup (`/signup`, with a plan), sign-in (`/auth/login` on the configured application host `app.wasla.tr`, which asks for the restaurant address; WAS-66), in-page sections, `/pos`, `/contact`, `/privacy-notice`, and the confirmed Instagram account `app.wasla.tr`. Unconfirmed social accounts, a help centre and an FAQ are left out, not linked to placeholders.
- **Contact page.** A form (name, restaurant, e-mail, optional Turkish phone with a fixed `+90`, topic, message, privacy notice acknowledgement) and the direct channels `mehmetaliozdemir@wasla.tr`, `+90 544 669 56 91` and Instagram (`PublicSiteUrls`). `ContactController` stores each message in CentralDb (`ContactMessages`) and then e-mails it to `Wasla:Contact:RecipientEmail`; Central Admin lists the messages at `/admin/contact-messages` with their notification status. The e-mail is sent only when `Email:Provider` is `Smtp`; otherwise the message is stored and marked as not notified. Antiforgery, a hidden honeypot field and the `contact-submission` rate limit ([../architecture/authentication.md](../architecture/authentication.md#credential-rate-limiting)) protect the form.
- **Privacy notice.** `/privacy-notice` is the KVKK article 10 notice for the contact form, written for this form and still a draft: it names Wasla as the data controller without a company title or address, and has not had legal review.
- **Metadata.** Each page sets its title, description, canonical URL and Open Graph tags through `PublicPageMetadata`. The canonical URL is built by `PublicSiteUrls` on the configured marketing domain (the apex), never from the request host; only a local run on `localhost` or an IP address keeps its own host. The redirect from `www.` belongs to deployment (WAS-76).

`PublicMarketingSiteTests` renders the four pages in every culture and checks the metadata, one `H1`, every link target, the external links, the images, the decorative mockups and the absence of "coming soon" next to Wasla POS. `ContactPageWebTests` and `AdminContactMessagesWebTests` cover the form and its Central Admin list.

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
| Public marketing site | `src/Wasla.Web/wwwroot/css/wasla-marketing.css`, `wwwroot/js/wasla-marketing.js`, `wwwroot/fonts/`, `wwwroot/images/marketing/`, `Views/Home/Index.cshtml`, `Views/Marketing/Pos.cshtml`, `Views/Marketing/PrivacyNotice.cshtml`, `Views/Contact/Index.cshtml`, `Views/Shared/_Public*.cshtml`, `Views/Shared/_Marketing*.cshtml` |
