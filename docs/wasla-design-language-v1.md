# Wasla Design Language v1

Status: approved direction. This document records locked product-design decisions for gradual visual implementation.

This is **not** evidence that the design system is fully implemented. Future visual phases must treat this file as the single authoritative reference and implement incrementally. Do not claim that current screens already match every rule below.

---

## B1. Product character

**Name:** Wasla Design Language v1

**Design character:** warm operational minimalism

Wasla is a multi-tenant SaaS for restaurant online-order operations. The visual language must feel:

- **warm** — paper, ink, and restrained orange; not cold enterprise chrome
- **reliable** — calm surfaces, predictable hierarchy, no decorative noise
- **fast** — operators can scan and act without hunting for controls
- **operational** — kitchen, counter, and manager workflows come first
- **restaurant-focused** — language, density, and actions serve restaurant staff

Wasla is **not**:

- a consumer food-delivery app
- a generic blue/gray admin template
- a full POS, ERP, inventory, or accounting product

### Avoid

Do not introduce or expand the following as product-wide patterns:

- excessive orange blocks
- oversized pills
- gradients
- glassmorphism
- oversized cards
- decorative whitespace on operational screens
- globally restyling Bootstrap primitives (`.card`, `.table`, `.btn`, `body`)
- page-specific visual systems that conflict with this language

If a screen needs extra emphasis, prefer hierarchy, density, and one restrained accent — not a new local palette or geometry.

---

## B2. Approved palette

Exact token names and hex values below are the approved brand direction. Implementation CSS custom properties may map to these names; do not invent a parallel brand palette.

### Brand / action

| Token | Hex | Role |
| --- | --- | --- |
| Wasla Orange | `#E87342` | Brand accent, active rail, selected emphasis |
| Action Orange | `#B94F2A` | Primary action, stronger press/hover of the brand orange |
| Orange Soft | `#FBE9DF` | Very light orange surface (active nav, soft highlight) |
| Deep Ink | `#28231F` | Primary text / ink |
| Warm Muted | `#746D66` | Secondary / supporting text |

### Surfaces

| Token | Hex | Role |
| --- | --- | --- |
| Application Canvas | `#F7F4EE` | App background behind the shell |
| Sidebar | `#F1EDE6` | Tenant / admin navigation surface |
| Main Surface | `#FFFEFA` | Primary content surface |
| Secondary Surface | `#F4F0E9` | Nested panels, toolbars, quiet grouping |
| Border | `#E3DDD3` | Default divider / control outline |
| Strong Border | `#D3CABD` | Stronger grouping, table chrome, emphasis edges |

### Status families

Status color is semantic, not decorative. Use family intent first; freeze exact foreground/background pairs only after contrast verification.

| Family | Intent | Typical use |
| --- | --- | --- |
| Sage green | success / active / printed | Healthy connection, completed print, positive confirmation |
| Amber | pending / new | New orders, waiting states, attention without alarm |
| Blue | printing / information | In-progress print, informational banners |
| Muted red | failed / cancelled | Failed print, cancelled order, destructive consequence |
| Neutral gray | unavailable / disabled | Offline, inactive, disabled controls |

### Contrast and platform colors

Exact status and text-on-surface pairs **must pass WCAG contrast verification** before being frozen in implementation. This document locks the families and brand hexes; it does not freeze every derived hover/disabled pair.

Platform colors (Trendyol, Yemeksepeti, Getir, and any future platform) remain confined to **platform identity badges and logos**. They must not define the general product theme, page chrome, buttons, or navigation.

---

## B3. Visual signature

The approved motif is a restrained **connection / flow line**.

Use it only where it explains a real relationship or state:

- thin active navigation rail
- lifecycle / state progression
- connection indicators
- Print Bridge connectivity

Rules:

- restrained use only
- never as decorative noise
- never as a repeating ornament, watermark, or background pattern
- prefer a thin, logical-start rail or a short progress segment over illustrated lines

If a screen has no connection, lifecycle, or selection to express, do not add the motif.

---

## B4. Geometry and spacing

### Spacing scale

Use this scale for new Wasla-owned layout:

`4 / 8 / 12 / 16 / 24 / 32`

Do not invent one-off gaps when a scale step is close enough. Operational screens may use the tighter end of the scale; management screens may use the middle.

### Radius

| Surface | Target | Notes |
| --- | --- | --- |
| Form / button | approximately `8px` | Compact controls, not pills |
| Card | approximately `10–12px` | Content surfaces |
| Large surface | maximum approximately `14px` | Drawers, large panels |
| Full pill | reserved | Actual badges and status chips only |

Navigation must **not** look like a large Bootstrap pill.

### Depth

- Borders are generally preferred over heavy shadows.
- Shadows must remain subtle and functional (dropdowns, sticky headers, elevated dialogs).
- Do not use large soft glows or stacked decorative shadows.

Exact implementation values (radius, rail width, shadow blur) are **subject to viewport and browser validation** before they are treated as frozen CSS tokens.

---

## B5. Navigation rules

### Active item

The selected navigation item uses:

- a very light orange surface (`Orange Soft` / `#FBE9DF`)
- an approximately `3px` orange logical-start rail (`Wasla Orange` / `#E87342`)
- slightly stronger text and icon
- a restrained radius — not a large pill

### Parent and child

- Parent and child must **not** both appear as selected pills.
- When a child route is active: the parent appears **expanded / emphasized**; only the child receives the true selected state (soft orange surface + rail).

### Directionality

Use **logical properties** (`border-inline-start`, `margin-inline-start`, `padding-inline-start`) so the active rail and alignment stay correct in RTL (Arabic).

### Mobile vs desktop

- Mobile tenant navigation must become **off-canvas**.
- Mobile must **not** reserve desktop sidebar width.
- Desktop collapsed behavior remains a **separate** pattern from mobile off-canvas. Do not reuse one breakpoint hack for both.

---

## B6. Typography and hierarchy

### Fonts

- Use a **system / local font stack** first.
- Do **not** add a font CDN.

### Page hierarchy

- One semantic `H1` per page.
- A restrained eyebrow is allowed (short product context above the title).
- Keep a clear section hierarchy: page title → section heading → supporting text → dense data.
- Operational codes (order numbers, device tokens when masked, external IDs) may use stronger weight or controlled monospace.
- Supporting text must not be made unreadably small.

### Localization

Typography and layout must be tested in:

- `tr-TR`
- `en-US`
- `ar-SA`
- `ru-RU`

Arabic requires suitable line height and RTL alignment. Do not assume a Latin-only measure or truncation rule.

### Approved Users naming

These labels are the approved product language for the tenant users / permissions surface. Implement through `SharedResource`; do not hardcode.

| Surface | tr-TR | en-US | ar-SA | ru-RU |
| --- | --- | --- | --- | --- |
| Sidebar | Kullanıcılar | Users | المستخدمون | Пользователи |
| Page title | Ekip ve Yetkiler | Team & Permissions | الفريق والصلاحيات | Команда и права доступа |
| Description | Restoran ekibinin rollerini ve erişim durumunu yönetin. | Manage restaurant team roles and access status. | أدر أدوار فريق المطعم وحالة الوصول. | Управляйте ролями команды ресторана и статусом доступа. |

Sidebar stays shorter and navigational. The page title carries the fuller operational meaning.

---

## B7. Component hierarchy

Wasla-owned components belong to these families. New visuals should extend a family, not invent a one-off control language.

| Family | Role |
| --- | --- |
| Primary action | Main next step on the current task |
| Secondary action | Alternative or supporting action |
| Quiet / link action | Low-emphasis navigation or inline text action |
| Destructive action | Irreversible or harmful consequence |
| Icon button | Compact icon-only control with an accessible name |
| Input / select / date / search | Form and filter fields |
| Status badge | Semantic state chip (not a platform logo) |
| Platform badge | Platform identity only |
| Metric card | A small, calm count or KPI |
| Content surface | Page/section container |
| Dense data table | Search and history lists |
| Empty state | No data, with a useful next action when one exists |
| Loading / skeleton | In-progress content placeholder |
| Toast | Transient feedback |
| Modal / drawer | Focused task overlay |
| Page header | Title, eyebrow, description, primary actions |
| Filter toolbar | Search/history filters |
| Sidebar parent / child | Tenant navigation tree |
| Order lifecycle action | Accept / reject / prepare / deliver (and existing equivalents) |
| Print status action | Shared print / reprint / queue / failed states |

### Required interactive states

Every interactive component must define, where applicable:

- default
- hover
- focus
- active
- disabled
- loading
- error

Focus must remain visible. Disabled and loading must not look like a new semantic status. Error belongs on the control or field, not as a page-wide color shift.

Namespace new Wasla components (for example `wasla-*`) instead of restyling Bootstrap primitives globally.

---

## B8. Page families

Do **not** force one spacing density onto all three families.

### 1. Management

Examples: Users, Settings, Branches, Connections.

- Calm and explanatory
- Medium density
- Clear page header, supporting description, and grouped forms or lists
- Reference implementation target: **Ekip ve Yetkiler** (Team & Permissions)

### 2. Operations

Example: Live Screen.

- High density
- Rapid scanning
- Strong lifecycle actions
- Distance readability (kitchen / counter)
- Minimal decoration
- Do not borrow management-page whitespace or large cards

### 3. Search / history

Examples: Orders, print history.

- Strong filters
- Dense tables
- Responsive column prioritization
- Mobile row / card transformation where required
- Filters stay predictable; do not restyle them into a second visual system

---

## B9. Functional and implementation guardrails

Visual work must **preserve** existing functional contracts. A visual phase is not a license to change behavior.

Preserve:

- routes
- authorization policies
- tenant isolation
- Razor model binding
- antiforgery
- localization (`SharedResource`, RTL)
- IDs and `data-*` attributes used by JavaScript
- delegated event handlers
- polling (including `/orders/table` where it already exists)
- browser notifications
- audio unlock and Stop Sound
- lifecycle actions
- receipt printing
- Print Bridge job semantics (`Pending → Printing → Printed / Failed`)
- future SignalR boundaries (do not implement SignalR as part of visual work)

### Implementation rules

- No new frontend framework (no React / Vue for this language).
- No second CSS framework.
- No font CDN.
- Namespace new Wasla components.
- Avoid global `.card` / `.table` / `.btn` / `body` overrides.
- Visual changes must not silently alter behavior.

Keep Web / Application / Infrastructure boundaries. Do not move business rules into CSS or JavaScript for the sake of a visual refresh.

---

## B10. Rollout

Implement gradually. Later phases must not invent a conflicting language.

1. **Phase 2C closeout** — tenant shell, mobile off-canvas sidebar, active navigation treatment, Ekip ve Yetkiler as the reference management page
2. **Phase 2C2** — Live Screen operational polish
3. **Phase 2C3** — Orders / search / history polish
4. Remaining management / settings pages
5. Print Bridge web management surfaces
6. Authentication / signup / provisioning surfaces where appropriate

**SignalR** remains a separate future functional phase. Visual work must not introduce SignalR.

Temporary UTF-8 receipt test output remains until the user **explicitly** requests removal. See `docs/temporary-receipt-test-output.md`. Do not treat visual rollout as permission to delete that test path.

---

## B11. Decision status

### Locked decisions

These are approved product-design decisions. Future visual work should follow them rather than reopen the character of the product.

- Language name: **Wasla Design Language v1**
- Character: **warm operational minimalism**
- Product qualities: warm, reliable, fast, operational, restaurant-focused
- Wasla is not a consumer delivery app, not a generic blue/gray admin template, and not a full POS
- Brand / action hexes: Wasla Orange `#E87342`, Action Orange `#B94F2A`, Orange Soft `#FBE9DF`, Deep Ink `#28231F`, Warm Muted `#746D66`
- Surface hexes: Application Canvas `#F7F4EE`, Sidebar `#F1EDE6`, Main Surface `#FFFEFA`, Secondary Surface `#F4F0E9`, Border `#E3DDD3`, Strong Border `#D3CABD`
- Status families: sage green, amber, blue, muted red, neutral gray — by semantic intent
- Platform colors stay on platform badges / logos only
- Visual signature: restrained connection / flow line (nav rail, lifecycle, connectivity)
- Spacing scale: `4 / 8 / 12 / 16 / 24 / 32`
- Radius direction: ~8px controls, ~10–12px cards, max ~14px large surfaces; full pills only for badges / chips
- Borders preferred over heavy shadows
- Active nav: light orange surface + ~3px logical-start rail + stronger text/icon; restrained radius
- Parent expanded / emphasized; only the child gets the true selected state
- Logical properties for RTL
- Mobile tenant nav is off-canvas and must not reserve desktop sidebar width
- Desktop collapsed nav is separate from mobile off-canvas
- System / local fonts only; no font CDN
- One semantic H1 per page
- Approved Users naming (sidebar / title / description) in TR / EN / AR / RU
- Three page families with distinct density: management, operations, search/history
- Component families and required interactive states listed in B7
- Functional guardrails in B9
- Rollout order in B10
- SignalR is not part of visual work
- Temporary UTF-8 receipt test output stays until explicit user request

### Provisional decisions requiring browser / contrast / viewport validation

These stay directional until they are verified in a real browser across the supported cultures and viewports.

- Exact CSS custom-property names and any derived hover / pressed / disabled mixes
- Exact WCAG-passing foreground / background pairs for every status family
- Exact `3px` rail width, radius pixels, and shadow values after desktop / tablet / mobile checks
- Active-nav contrast of `Orange Soft` + `Deep Ink` / `Wasla Orange` in light UI and any future dark-mode decision
- Arabic line-height, wrapping, and logical-start rail alignment
- Russian and Arabic title length in the page header and sidebar
- Mobile off-canvas width, overlay, and focus-trap behavior
- Live Screen distance readability at kitchen / counter viewing distance
- Orders table column prioritization and mobile row / card transformation
- Whether metric cards and filter toolbars need a slightly tighter radius than content cards
- Any dark-mode or high-contrast extension (not defined in v1)

Until those checks pass, implement with the locked tokens and families, then adjust only the provisional measurements — do not change the product character or invent a second palette.
