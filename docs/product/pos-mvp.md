# Wasla POS MVP

## Status and scope

Product definition for the first usable Wasla POS release (WAS-34, 2026-10). **Nothing in this document is implemented.** No POS project, installer, database or screen exists in this repository. The current code is Wasla Orders ([product-boundaries.md](product-boundaries.md)).

Wasla POS is a **local-first product installed at the restaurant**. Selling never depends on the internet or the Wasla cloud. The architecture is decided in [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md); the first slice and the backlog are in [pos-first-slice.md](pos-first-slice.md).

This document owns:

- The MVP cashier, waiter and kitchen workflows, and installation and setup at the restaurant
- POS roles and what each may do
- The MVP boundary: must-have, later, out of scope, assumptions and open decisions
- The restaurant-payment boundary
- UX and non-functional requirements, including working without internet

The Wasla POS page of the marketing Figma file (`Wasla-Marketing`, page `14:803`) shows the product intent:
- table service, delivery, quick sale and takeaway;
- touch product selection with modifiers;
- cash and card payment;
- kitchen and receipt printing;
- Orders and POS side by side.

It is a visual direction, not evidence that any of it exists, and it does not set the MVP scope below.

## Product position

Wasla POS is the in-restaurant point of sale. A cashier or waiter builds a check on a touch terminal, the kitchen gets a printed ticket, and the sale is closed when the payment is recorded. It is not the Wasla Orders Live Screen and does not handle platform orders. A restaurant may use both products. They share the restaurant account (tenant), not their operational screens or databases.

At the restaurant, **POS Edge** (a Windows service on one always-on PC) holds the POS database, serves the screens to the **POS Terminals** on the local network, prints, and synchronizes with the Wasla cloud whenever the internet is available. "Offline" below means offline from the internet and the Wasla cloud. The restaurant's network and its POS Edge machine must still be running.

Terms used below:

| Term | Meaning |
| --- | --- |
| **POS Edge** | The installed Wasla POS service and its local database on the restaurant's Edge machine |
| **Terminal** | A paired Windows touch device running the Wasla POS Terminal app, which talks to POS Edge over the restaurant network or on the same machine |
| **Check** (*adisyon*) | One open or closed sale: a table's bill, a takeaway order or a quick sale. Lines and payments belong to a check |
| **Line** | One product on a check with its chosen modifiers, quantity, note and price snapshot |
| **Kitchen ticket** | A printed request to prepare the lines sent from a check; also a durable record |
| **Payment record** | Wasla's record that money was received: cash, or approved on an external card terminal. Wasla moves no money |
| **Receipt** | A printed, non-fiscal summary of a closed check ([Fiscal documents](#unresolved-product-decisions)) |
| **Business day** | The trading day used for history and totals. It runs from a configurable cut-off (default 05:00, `Europe/Istanbul`) to the next, so a service after midnight stays in its evening's day. There is no shift in the MVP |
| **Sync** | Uploading finished sales, payments, tickets and audit records from POS Edge to the Wasla cloud, and receiving licence and revocation updates |

## Installation and setup

1. **Activation (operator, internet needed once).** Wasla activates POS for the tenant and gives the restaurant a one-time activation code. The installer is run on the Edge machine, and POS Edge is activated with the code. That creates the installation's identity, its first licence lease, an Owner setup code and a printed Owner recovery code.
2. **First Owner.** On the Edge machine, the Owner sets their PIN with the setup code.
3. **Printers.** In the back office, the Owner maps the receipt and kitchen printer roles to printers installed on the Edge machine (the kitchen role may use the receipt printer) and prints a test page.
4. **Terminals.** For each terminal: install the Terminal app, then pair it with a pairing code created by the Owner or a Manager. The code also verifies POS Edge's certificate.
5. **Menu, tables, staff.** The Owner imports or enters the menu, areas and tables, and adds staff with their roles and PINs.
6. **Backups.** POS Edge backs up automatically. The Owner may add a second backup location (another disk or a USB drive).

After setup, internet is needed only to synchronize, to receive updates and licence renewals, and at least once per licence period ([licence](../architecture/pos-application-boundary.md#licensing-while-offline)).

## Cashier workflows

All flows run on a paired Terminal with a signed-in staff member ([Roles](#roles)). Every step that changes a check is a request to POS Edge over the restaurant network. **None of them needs the internet or the Wasla cloud.** The Terminal holds only unsent draft lines ([LAN loss](#lan-loss)).

### 1. Product, category and modifier selection

1. The sale screen shows category tabs (the restaurant's own order) and the products of the selected category as tiles: name, price, and a marker when the product has modifiers.
2. Tapping a product without required modifiers adds one line with quantity 1. Tapping it again increases that draft line's quantity.
3. A product with modifier groups opens a modifier panel.
   - Each group is single-choice (for example portion: 1, 1.5, 2) or multi-choice (extras), with a minimum and maximum and an optional price change per option.
   - Required groups must be completed before **Add**.
   - The panel shows the line total as options are chosen.
   - A free-text kitchen note is optional.
4. A search field filters products by name across categories, for keyboard use and long menus.
5. Inactive products and products marked unavailable today are hidden from the tiles, but remain on existing checks.

### 2. Touch-first cart

- The cart is always visible next to the products. It shows the lines with quantity, modifiers, note and line total; the subtotal and total; and the check's label (table, takeaway name or number, or quick sale).
- Lines not yet sent are editable: quantity −/+, change modifiers, note, remove. Lines already sent to the kitchen are locked; changing them is a [void](#9-void-and-cancellation).
- The primary actions are fixed in the same place on every check: **Send to kitchen**, **Pay**, and for tables **Save** (keep the check open without sending).

### 3. Dine-in table (adisyon)

1. The tables screen shows areas (for example Salon, Bahçe) and their tables. A table is **free** or **occupied** (has an open check), with the open check's total and age.
2. Tapping a free table opens a new check for it; tapping an occupied table opens its check. One table has at most one open check, even when two terminals try at the same moment.
3. Staff add lines, then **Send to kitchen**, or **Save** to keep drafts as unsent lines on POS Edge.
4. More rounds are added to the same check and sent as new kitchen tickets.
5. **Print bill** prints the current check as a pre-bill slip for the guest. It is not a receipt and can be printed many times.
6. **Pay**, by [cash](#6-cash-payment) or [external terminal](#7-payment-confirmed-on-an-external-terminal). When the recorded payments cover the total, the check closes and the table becomes free.

### 4. Takeaway (gel-al)

A check without a table, labelled with an optional name and a sequential takeaway number for the business day. It follows the dine-in steps: send to kitchen, then pay now or at pickup. Open takeaway checks are listed on the tables screen under **Takeaway**. Delivery to an address (*paket servis*) is not part of the MVP ([Out of scope](#out-of-scope)).

### 5. Quick sale (hızlı satış)

For counter sales paid immediately: the sale screen opens on an empty quick-sale check, and the cashier adds products and taps **Pay**. **Send to kitchen** is optional; when the restaurant enables it, it happens automatically on payment. The check closes on payment and the screen returns to an empty quick sale.

### 6. Cash payment

1. **Pay** shows the amount due and the methods.
2. **Cash**: buttons for the exact amount and common notes (for example 50, 100, 200 ₺), plus a numeric keypad. The screen shows change due when the tendered amount exceeds the due amount.
3. **Confirm** records one cash payment for the amount applied to the check, with the tendered amount and change. The check closes when payments cover the total.
4. The receipt prints if the restaurant enabled automatic receipts; otherwise **Print receipt** is offered.

### 7. Payment confirmed on an external terminal

1. **Pay → Card (external terminal)** shows the amount to key into the restaurant's own physical card terminal.
2. The cashier runs the payment on that terminal. Wasla does not talk to it.
3. Only after the terminal shows approval does the cashier tap **Approved on terminal**. An optional reference field accepts the terminal slip's approval or receipt number: short, alphanumeric, never a card number.
4. Wasla records the payment. If the terminal declined, the cashier taps **Cancel** and nothing is recorded.

A check may have several payment records (for example part cash, part card). Splitting a check by items is not in the MVP.

### 8. Receipt and kitchen printing

- **Kitchen ticket**: every **Send to kitchen** creates one ticket with the newly sent lines (product, modifiers, quantity, note), the check label, the staff member and the time. It prints on the kitchen printer role. Printing again marks the copy as a reprint.
- **Void slip**: voiding a sent line prints a slip to the kitchen printer, so the kitchen stops preparing it.
- **Pre-bill**: an unpaid check summary for the guest.
- **Receipt**: a closed check's lines, totals and payment methods, with the restaurant's name and the business day's receipt number. It is marked as a non-fiscal information slip ([Fiscal documents](#unresolved-product-decisions)). Reprints say "copy".
- POS Edge prints on printers installed on the Edge machine, without the internet, the cloud or Wasla Orders ([print decision](../architecture/pos-application-boundary.md#printing)).
- A print job's state is shown honestly: queued, printing, printed (accepted by the Windows spooler) or failed. A job interrupted by a restart shows "print uncertain" and is never reprinted automatically. A print failure never undoes a kitchen send or a payment.

### 9. Void and cancellation

| Situation | Who | What happens |
| --- | --- | --- |
| Remove or change an unsent line | Cashier, Waiter | Line is removed; no kitchen effect |
| Void a sent line | Cashier or Waiter requests, Manager/Owner approves on the device with their PIN | Line stays on the check as voided with a reason; void slip prints in the kitchen; totals drop |
| Cancel an open check with sent lines | Manager/Owner | All sent lines voided with one reason; the table is freed |
| Reverse a recorded payment on an open check (wrong method or amount) | Manager/Owner | A reversal record cancels that payment; the original stays visible |
| Cancel a closed sale (refund) | Manager/Owner | Each payment gets a reversal record, the check becomes **Cancelled** with a reason, and a cancellation slip prints. The staff member confirms they returned the cash or refunded on the external terminal; Wasla records that confirmation and does not perform the refund |

Nothing is deleted. Every void, reversal and cancellation is audited ([Auditability](#auditability)) and synced. Reasons come from a short fixed list (wrong item, guest changed mind, kitchen problem, other with text). A closed sale that was already synced and is then cancelled reaches the cloud as a new cancellation record. The cloud's copy of the original sale is never rewritten.

### 10. Completed-sale history

- Lists closed and cancelled checks of a business day (default today), newest first, with number, label, total, payment methods and status. Filter by status and payment method; search by check or receipt number.
- It reads only POS Edge's local database, so it works without internet.
- Opening a check shows it **read-only**: lines, voids, kitchen tickets, payments, reversals and audit entries.
- Actions from history: **Reprint receipt** (Cashier, Manager, Owner) and **Cancel sale** (Manager, Owner).
- A day summary shows totals by payment method, the expected cash for the day, and the number of cancelled checks. It is not a Z report and not a fiscal report.

### 11. Shift open and close

**Not in the MVP, for an operational reason.** A cash-drawer shift (opening float, cash in and out, counted cash, difference, shift report) is how many restaurants control cash. The MVP leaves it out because:
- **No fiscal design yet.** Restaurants that keep their fiscal cash register already close the day on it. Designing Wasla's own end-of-day and drawer flow before the [fiscal decision](#unresolved-product-decisions) risks a second, conflicting procedure the restaurant must run every night.
- **The slice proves something else.** It must prove local selling and safe sync, not cash control.

Until shifts exist, the day summary's expected cash per business day is the reconciliation figure. Shifts are the first should-have after the MVP ([Should-have later](#should-have-later)). They become a must-have for any pilot restaurant that does not use a fiscal register for end-of-day cash control.

### 12. Sync and connection state

- Every terminal shows a small sync chip: **Synchronized**, **Offline since hh:mm (n waiting)**, or **Needs attention**. It never blocks selling.
- Owners and Managers see sync health in the back office:
  - last successful upload;
  - waiting records and the oldest one's age;
  - a blocked reason, if any;
  - licence expiry;
  - clock difference;
  - the last backup result.

  They can create a support bundle from the same page.
- When the internet returns, the waiting records are uploaded by themselves. Nothing is lost by a restart in between.

## Roles

POS roles are owned by Wasla POS. They are **not** the Wasla Orders `UserRole` values (`Owner`, `Manager`, `Kitchen`, `Cashier`, `Viewer` in `src/Wasla.Domain/Enums/UserRole.cs`). There, Cashier and Kitchen handle platform orders on the Live Screen ([roles-and-permissions.md](roles-and-permissions.md)), which is a different job. Reusing that enum would tie POS permissions to Orders policies and migrations. Names may match; the definitions do not.

| Capability | Owner | Manager | Cashier | Waiter | Kitchen |
| --- | :-: | :-: | :-: | :-: | :-: |
| Open table, takeaway or quick-sale check; add and change unsent lines | ✓ | ✓ | ✓ | ✓ | |
| Send to kitchen; print pre-bill | ✓ | ✓ | ✓ | ✓ | |
| Record cash or external-terminal payment; close check | ✓ | ✓ | ✓ | | |
| Request a void of a sent line | ✓ | ✓ | ✓ | ✓ | |
| Approve a void; cancel an open check; reverse a payment; cancel a closed sale | ✓ | ✓ | | | |
| View history and reprint receipts | ✓ | ✓ | ✓ | | |
| Edit menu, prices, modifiers, tables | ✓ | ✓ | | | |
| View sync and backup status; create a support bundle; apply a downloaded update | ✓ | ✓ | | | |
| Manage POS staff and PINs; pair and revoke terminals; printer roles; POS settings; enter an emergency licence code | ✓ | | | | |
| Use any POS screen | | | | | MVP: none |

- **Kitchen** works from printed tickets in the MVP. A kitchen display screen is later. The role exists so that the staff list is complete and a kitchen screen can be added without changing other roles.
- **Waiter** cannot take payments in the MVP. Whether some restaurants want waiters to collect payment is an [open decision](#unresolved-product-decisions).
- **Owner** in POS is the restaurant's POS administrator. Its PIN, recovery and relation to the Orders Owner account are decided in the [ADR](../architecture/pos-application-boundary.md#staff-pins-and-recovery).
- POS Edge enforces this table on every request. Hiding a button is not authorization.

## MVP boundary

### Must-have

**Installed product:**
- POS Edge, a Windows service with a local SQLite database, installed with a signed installer.
- The Terminal app.
- Activation of one installation per restaurant location.
- Update with automatic rollback when the new version is unhealthy.

**Selling without internet:**
- Opening checks, lines and modifiers, kitchen tickets, payments, closing, printing, history and PIN sign-in on paired terminals all work with the internet or the Wasla cloud unavailable.

**Sync:**
- A durable local outbox, idempotent upload, replay after a restart.
- Visible sync state.
- Licence and revocation updates from the cloud.

**Licence:**
- A signed licence lease cached locally with a grace period.
- A restricted mode that never stops a service in progress.
- A one-time emergency extension.

**Backup and recovery basics:**
- Automatic local backups with integrity checks.
- A documented restore onto the same or a replacement machine.
- A restore smoke test.

**Devices and staff:**
- Paired terminals with a pinned certificate.
- Staff sign-in with a personal PIN on a paired terminal; lockout.
- Owner recovery without a permanent bypass.
- The role table above.

**Menu and tables:**
- Categories, products with price, active and available flags, modifier groups with options and price changes; edited by Owner or Manager.
- Areas and tables.

**Checks:**
- Dine-in table, takeaway, quick sale; touch cart; line notes.
- Send to kitchen with durable kitchen tickets.
- Close on full payment; one open check per table; single-winner close across terminals.

**Payments:**
- Payment records: cash (tendered and change) and external terminal (optional reference); several payments per check.
- Voids, check cancellation, payment reversal and sale cancellation with Manager or Owner approval and reasons.

**Printing:**
- Local printing of the kitchen ticket, void slip, pre-bill and receipt on mapped receipt and kitchen printer roles, with honest print state.

**History, audit and support:**
- Completed-sale history (read-only) with reprint and a day summary by payment method.
- An audit log for every money- or kitchen-affecting action, synced to the cloud.
- Support bundle export.

**Language and correctness:**
- TR, EN, AR, RU with Arabic right to left; TRY amounts.
- Idempotent writes and duplicate-payment protection ([Non-functional requirements](#non-functional-requirements)).

### Should-have later

In rough order of expected value:

1. Shifts and cash drawer: opening float, cash in and out, counted cash and difference, shift report ([why later](#11-shift-open-and-close))
2. Table operations: move a check to another table, merge checks, split a check by items or guests
3. Discounts and service charge with permission and audit
4. Kitchen display screen for the Kitchen role, and routing tickets to several kitchen printers or stations
5. Encrypted cloud backups of the POS Edge database
6. Cloud reports and a remote back office (menu, reports) in a browser, with single-writer configuration rules
7. Waiter tablets and phones (Android, iPadOS)
8. Barcode lookup for packaged products; product images
9. Daily and period sales reports, product sales ranking, export
10. Delivery orders taken by phone (*paket servis*) with customer address
11. Showing Wasla Orders platform orders and POS checks together (the Figma "one ecosystem" panel), through a cloud read model, not a shared database
12. Menu translations for product names

### Out of scope

- Integrated card processing: no card-terminal integration, no bank POS API, no card number or CVV entry, no payment provider for restaurant sales
- Using Wasla's iyzico subscription merchant account for any restaurant sale
- Fiscal receipt devices (ÖKC / *yeni nesil yazar kasa*), e-Arşiv, e-Fatura or e-Adisyon issuance, until the [fiscal decision](#unresolved-product-decisions) is made
- More than one POS Edge per location, failover between Edges, and multi-master synchronization
- Inventory, stock, recipes and purchasing
- Accounting, payroll and tax reporting
- Delivery logistics and courier management
- Loyalty, campaigns and customer accounts
- Reservations
- Multi-location management (each location stays a separate tenant, as in [tenancy.md](../architecture/tenancy.md))
- Changes to Wasla Orders, its Live Screen or its order lifecycle

### Assumptions

- **Internet use.** Service does not need the internet. It is needed for activation, for updates, and to renew the licence at least once per lease period (14 days, [open decision](#unresolved-product-decisions)). Sync uses outbound HTTPS only.
- **Edge machine.** The restaurant has one always-on Windows 10 or 11 Pro (64-bit) PC as the Edge machine, wired to the restaurant network. A UPS is recommended. It may also be a cashier station.
- **Terminals.** Windows touch devices at 1024×768 or larger, on the same network as the Edge machine (wired or a staff Wi-Fi, never a guest network).
- **Printers.** At least one ESC/POS-capable receipt printer is installed on the Edge machine for all users. A kitchen printer is optional; tickets can print on the receipt printer.
- **Card payments** run on the restaurant's existing bank terminal, and the restaurant keeps issuing fiscal documents the way it does today.
- **Prices** include VAT; the MVP shows no VAT breakdown.
- **Currency.** One currency, TRY, as in Wasla Orders (`OrderCurrency`).
- **Pilot size.** Up to about 30 tables, 300 products and 5 terminals. These are design targets, not measured limits.

### Unresolved product decisions

| Decision | Why it matters | Needed before |
| --- | --- | --- |
| **Fiscal documents.** What Turkish fiscal obligations (ÖKC, e-Arşiv, e-Adisyon) apply to a restaurant using Wasla POS, and whether Wasla must integrate with them | Not researched in WAS-34; needs legal and tax review. Until decided, every Wasla receipt says it is not a fiscal document, and the restaurant keeps its fiscal device | **Any paid pilot** (blocker) |
| **Licence lease and restricted mode.** Lease length (proposed 14 days), warning period, and what restricted mode refuses | Trades protection against unpaid use against the risk of stopping a restaurant during a long outage ([ADR](../architecture/pos-application-boundary.md#licensing-while-offline)) | Pilot onboarding |
| **Hardware and support model.** Who supplies and installs the Edge machine, whether a spare machine is required, and the restore time target | The Edge machine is the single point of failure ([ADR](../architecture/pos-application-boundary.md#failure-behavior)) | Pilot selection |
| **Owner account.** Whether the POS Owner signs in with the same email and password as Wasla Orders | POS staff identity is local for the MVP; one Wasla login needs identity to move out of the Orders tenant database ([ADR](../architecture/pos-application-boundary.md#staff-pins-and-recovery)) | Selling POS and Orders together |
| **Waiter payments.** Whether waiters may record payments, as a per-restaurant setting | Changes the role table and the audit trail | Pilot onboarding |
| **Waiter tablets.** Whether non-Windows devices are needed for the first pilots | Needs a second shell or a trusted LAN certificate scheme | Pilot selection |
| **Pricing and packaging.** Whether POS is a separate plan, an add-on to Orders, or bundled | Decides what the licence lease checks (WAS-147 owns the billing domain) | Self-service activation |
| **Kitchen printing layout.** One kitchen printer, or routing by category to several stations | Several stations need product-to-printer routing in the menu | First pilot with more than one kitchen station |
| **Receipt numbering.** Per business day or continuous | Affects history search and any later fiscal integration | First slice (the slice uses per-day numbers and can change before production) |

## Payment boundary

Wasla POS **records** restaurant sale payments. It never processes, authorizes, captures or refunds them.

| | Wasla subscription billing | Restaurant sale payments (POS) |
| --- | --- | --- |
| Who pays whom | The restaurant pays Wasla for the SaaS | The restaurant's guest pays the restaurant |
| Money movement | iyzico, through Wasla's merchant account (WAS-145 to WAS-154; not implemented yet; signup uses a Development simulator, see [tenancy.md](../architecture/tenancy.md#signup-and-checkout-access)) | Cash, or the restaurant's own bank card terminal. Wasla moves no money |
| Where it is stored | CentralDb (WAS-147) | POS Edge's local database, with immutable copies in the tenant's POS cloud database ([ADR](../architecture/pos-application-boundary.md#data-ownership)) |
| Card data | Never stored; iyzico's hosted form | Never entered or stored, locally or in the cloud. No card number, expiry or CVV field exists |

Rules:

- Payment methods in the MVP: **Cash** and **External card terminal**. Adding a method is a product decision, not configuration.
- **External-terminal records.**
  - The record means only "the cashier confirmed the terminal approved this amount".
  - The optional reference is free text up to 32 characters, letters and digits only.
  - A run of 13–19 digits that passes the Luhn check (a possible card number) is refused, at entry and again before upload. Approval codes and 12-digit retrieval reference numbers stay allowed.
- Wasla's iyzico account, keys and code are never used by POS. POS has no payment-provider dependency.
- **Reversals and refunds are records too.**
  - A reversal points to the payment it cancels and has an amount equal to that payment, a reason, the approving Manager or Owner, and the confirming staff member.
  - A recorded payment is never edited or deleted, locally or in the cloud.
  - A refund on the terminal is the cashier's action on the terminal. Wasla cannot know whether it happened and records only the confirmation.
- A check closes only when the sum of its payments minus reversals equals its total. Cash may exceed the total; the difference is change and is recorded.

## UX requirements

The POS uses the Wasla visual direction (warm operational minimalism, foundation tokens in [../frontend/design-system.md](../frontend/design-system.md)). It uses the repository's frontend stack: ASP.NET Core MVC/Razor, Bootstrap 5 and vanilla JavaScript ([../frontend/architecture.md](../frontend/architecture.md)). The pages are served by POS Edge and shown in the Terminal app. It is a separate operational surface from the Orders tenant shell.

**Layout (1024×768 and larger, landscape):**

- Three zones: categories (top or start edge), product grid (center), cart and actions (end edge, about one third of the width). In Arabic the zones mirror.
- Dense, with no decorative whitespace. Text and prices are legible at arm's length (product names at least 16 px, totals at least 24 px).
- The tables screen is a grid of table tiles per area. Both tile colour and a text label show free or occupied; colour is never the only signal.
- Portrait terminals (768 px wide) stack the cart under the products with a cart summary bar. Phone widths are not a target for the MVP.

**Touch targets and taps:**

- Every interactive control is at least 48×48 CSS px, with at least 8 px between targets. Product tiles are at least 96×72 px. WCAG 2.2 requires 24 px; 48 px is the operational target for wet or gloved hands.
- Tap counts:
  - Add a product without modifiers: 1 tap.
  - Quick sale paid with exact cash: **Pay** + **Cash exact** = 2 taps after the products.
  - Card on terminal: **Pay** + **Card** + **Approved on terminal** = 3 taps.
  - Send to kitchen: 1 tap.
  - Open an occupied table's check from the tables screen: 1 tap.
- No hover-only controls, no long-press-only actions, no drag-and-drop requirement.
- Destructive or money actions (void, cancel, reversal) need a confirmation step; adding lines never does.

**Keyboard and scanner readiness:**

- Every action is keyboard operable, with a visible focus ring:
  - The product search field is reachable with one key (`/` or F2).
  - Enter adds the highlighted product.
  - Amount entry accepts the numeric keypad.
- The sale screen must not swallow fast keyboard input, so that a keyboard-wedge barcode scanner can be added later without a redesign. Barcode lookup itself is not in the MVP.

**Localization:**

- TR (default), EN, AR, RU for every POS string, including the Terminal app, setup console and printed documents. Resource keys go under a POS prefix. There is no hardcoded copy in Razor or JavaScript, and JavaScript messages are passed from Razor, as in Wasla Orders ([20-localization-ui.mdc](../../.cursor/rules/20-localization-ui.mdc)).
- Arabic is right to left: mirrored zones, logical CSS properties, and amounts and check numbers kept left to right inside right-to-left text.
- Amounts are in TRY with `₺`, through the culture's number format, never the culture's own currency. Dates are Gregorian in every culture, as in Orders (WAS-44 for amounts, WAS-130 for dates).
- Product, category and modifier names are restaurant data and are not translated.

**Accessibility:** WCAG 2.2 AA for the POS screens:
- AA contrast in light and dark themes;
- labelled controls;
- status changes (line added, payment recorded, print failed, POS Edge unreachable, sync state) announced through live regions;
- no information by colour alone;
- `prefers-reduced-motion` respected;
- 200 % zoom without loss of function on the back-office screens.

## Non-functional requirements

These are targets for design and tests. None is measured yet.

### Responsiveness

- Tap feedback within 100 ms. Adding or changing a draft line happens on the Terminal and never waits for POS Edge.
- Writes to POS Edge (send to kitchen, save, pay, close) answer within 200 ms at the 95th percentile over the restaurant network. The UI shows progress after 300 ms and never accepts a second identical action while one is in flight.
- Nothing a cashier waits for calls the internet.
- The sale screen loads the menu once per sign-in and refreshes it when the menu changes; it does not request the menu per product tap.

### Internet loss

- Selling, printing, history and PIN sign-in on paired terminals continue unchanged. The sync chip shows "Offline since".
- Synced facts wait in POS Edge's durable outbox and are uploaded, in order and without duplicates, when the connection returns ([ADR](../architecture/pos-application-boundary.md#synchronization)).
- A Wasla cloud outage is the same as an internet outage for the restaurant.
- The licence lease covers at least 14 days without contact. Restricted mode, if ever reached, refuses only new checks from the next business day. It never stops paying, closing, printing or viewing existing checks ([ADR](../architecture/pos-application-boundary.md#licensing-while-offline)).

### LAN loss

- A Terminal that cannot reach POS Edge shows "POS Edge unavailable". Draft lines stay editable on that Terminal, and **Send to kitchen**, **Pay** and **Close** are disabled with a localized message.
- Draft lines that were never sent live only on that Terminal. Leaving the check with unsent lines asks for confirmation.
- A request that timed out is retried with the same idempotency key, so a send or payment that reached POS Edge is not duplicated ([below](#idempotency-and-duplicate-protection)).

### Edge machine failure

If the Edge machine fails, the restaurant cannot use POS until it is replaced and restored ([ADR](../architecture/pos-application-boundary.md#failure-behavior)):
- **Backups:** automatic backups every 15 minutes, and daily backups kept for 14 days.
- **Data safety:** synced facts are also safe in the cloud.
- **Fallback:** paper and the fiscal register.
- **Restore target:** within 2 hours with a spare machine.

### Idempotency and duplicate protection

- The Terminal creates the ids of checks, lines and payments (GUIDs) and sends an idempotency key with every write. A repeated request with the same key returns the first result and changes nothing.
- Recording a payment and closing a check are compare-and-set on the check's version: two terminals paying the same table cannot both succeed. The loser sees the check's current state, like Wasla Orders' single-winner order actions (WAS-104).
- A payment that would make the recorded total exceed the check total is refused, except cash, whose excess is recorded as change.
- A closed or cancelled check accepts no lines, payments or sends.
- A kitchen send sends only lines not yet sent; repeating it creates no second ticket for the same lines.
- **Uploads are idempotent too.** Each synced record has a unique id and a per-installation sequence, so the cloud stores it once however often it is sent.

### Auditability

- An append-only audit record for:
  - sign-in success, failure and lockout;
  - terminal pairing and revocation;
  - send to kitchen;
  - void request and approval;
  - check cancellation;
  - payment, payment reversal and sale cancellation;
  - receipt reprint;
  - menu price change;
  - staff and PIN changes;
  - licence and emergency-code events;
  - large clock changes;
  - backups, restores and updates.
- Each record has the UTC time, terminal, staff member (and approver when different), check, action, amounts before and after, and reason. No PINs, credentials, codes or secrets.
- Audit records are never updated or deleted, locally or in the cloud.

### Security and tenant isolation

- **One tenant per installation.** One POS Edge serves exactly one tenant and its database holds only that tenant's data. A terminal paired to one installation cannot act on another.
- **Credentials:**
  - Terminals present a device credential over HTTPS to a pinned certificate.
  - Staff sign in with a PIN only on a paired terminal.
  - POS Edge authenticates to the cloud with its installation credential. The cloud never trusts a tenant id from a request without that credential.
- **PINs** are hashed with a local secret, rate limited per terminal and staff member, and locked after repeated failures.
- **Network exposure.** POS Edge accepts no unauthenticated requests apart from pairing with a valid one-time code and an anonymous health probe that shows no tenant data. It must not be exposed to the internet.
- POS follows the repository's secrets and logging rules ([AGENTS.md](../../AGENTS.md#8-secrets-credentials-and-logging)).

## Related documents

- [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md): installed architecture, data ownership, sync, printing, devices, licence and repository decision
- [pos-first-slice.md](pos-first-slice.md): first vertical slice and backlog
- [product-boundaries.md](product-boundaries.md)
- [roles-and-permissions.md](roles-and-permissions.md): Wasla Orders roles (not POS roles)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
