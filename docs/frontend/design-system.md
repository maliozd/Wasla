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
