# Wasla POS MVP

## Status and scope

Product definition for the first usable Wasla POS release (WAS-34, 2026-10). **Nothing in this document is implemented.** No POS project, module, database or screen exists in this repository; the current code is Wasla Orders ([product-boundaries.md](product-boundaries.md)). The application, database and print boundaries are decided in [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md). The first build step and the follow-up backlog are in [pos-first-slice.md](pos-first-slice.md).

This document owns:

- The MVP cashier, waiter and kitchen workflows
- POS roles and what each may do
- The MVP boundary: must-have, later, out of scope, assumptions and open decisions
- The restaurant-payment boundary
- UX and non-functional requirements

The Wasla POS page of the marketing Figma file (`Wasla-Marketing`, page `14:803`) shows the product intent: table service, delivery, quick sale and takeaway, touch product selection with modifiers, cash and card payment, kitchen and receipt printing, and Orders and POS side by side. It is a visual direction, not evidence that any of it exists, and it does not set the MVP scope below.

## Product position

Wasla POS is the in-restaurant point of sale: a cashier or waiter builds a check on a touch screen, the kitchen gets a printed ticket, and the sale is closed when the payment is recorded. It is not the Wasla Orders Live Screen and does not handle platform orders. A restaurant may use both products. They share the restaurant account (tenant), not their operational screens or databases.

Terms used below:

| Term | Meaning |
| --- | --- |
| **Check** (*adisyon*) | One open or closed sale: a table's bill, a takeaway order or a quick sale. Lines and payments belong to a check |
| **Line** | One product on a check with its chosen modifiers, quantity, note and price snapshot |
| **Kitchen ticket** | A printed request to prepare the lines sent from a check; also a durable record |
| **Payment record** | Wasla's record that money was received: cash, or approved on an external card terminal. Wasla moves no money |
| **Receipt** | A printed, non-fiscal summary of a closed check ([Fiscal documents](#unresolved-product-decisions)) |
| **Business day** | The calendar day in `Europe/Istanbul` used for history and totals. There is no shift in the MVP |

## Cashier workflows

All flows run on a paired POS device with a signed-in staff member ([Roles](#roles)). Every step that changes a check is a server request; the device holds only unsent draft lines ([Network loss](#network-loss)).

### 1. Product, category and modifier selection

1. The sale screen shows category tabs (the restaurant's own order) and the products of the selected category as tiles: name, price, and a marker when the product has modifiers.
2. Tapping a product without required modifiers adds one line with quantity 1. Tapping it again increases that draft line's quantity.
3. A product with modifier groups opens a modifier panel. Each group is single-choice (for example portion: 1, 1.5, 2) or multi-choice (extras), with a minimum and maximum and an optional price change per option. Required groups must be completed before **Add**; the panel shows the line total as options are chosen. A free-text kitchen note is optional.
4. A search field filters products by name across categories, for keyboard use and long menus.
5. Inactive products and products marked unavailable today are hidden from the tiles, but remain on existing checks.

### 2. Touch-first cart

- The cart is always visible next to the products: lines with quantity, modifiers, note and line total; subtotal and total; the check's label (table, takeaway name/number, or quick sale).
- Lines not yet sent are editable: quantity −/+, change modifiers, note, remove. Lines already sent to the kitchen are locked; changing them is a [void](#9-void-and-cancellation).
- The primary actions are fixed in the same place on every check: **Send to kitchen**, **Pay**, and for tables **Save** (keep the check open without sending).

### 3. Dine-in table (adisyon)

1. The tables screen shows areas (for example Salon, Bahçe) and their tables. A table is **free** or **occupied** (has an open check), with the open check's total and age.
2. Tapping a free table opens a new check for it; tapping an occupied table opens its check. One table has at most one open check.
3. Staff add lines, then **Send to kitchen** (or **Save** to keep drafts as unsent lines on the server).
4. More rounds are added to the same check and sent as new kitchen tickets.
5. **Print bill** prints the current check as a pre-bill slip for the guest (not a receipt; it can be printed many times).
6. **Pay** ([cash](#6-cash-payment) or [external terminal](#7-payment-confirmed-on-an-external-terminal)). When the recorded payments cover the total the check closes and the table becomes free.

### 4. Takeaway (gel-al)

A check without a table, labelled with an optional name and a sequential takeaway number for the business day. It follows the dine-in steps: send to kitchen, then pay now or at pickup. Open takeaway checks are listed on the tables screen under **Takeaway**. Delivery to an address (*paket servis*) is not part of the MVP ([Out of scope](#out-of-scope)).

### 5. Quick sale (hızlı satış)

For counter sales paid immediately: the sale screen opens on an empty quick-sale check. The cashier adds products and taps **Pay**; **Send to kitchen** is optional and, when the restaurant enables it, happens automatically on payment. The check closes on payment and the screen returns to an empty quick sale.

### 6. Cash payment

1. **Pay** shows the amount due and the methods.
2. **Cash**: buttons for the exact amount and common notes (for example 50, 100, 200 ₺), plus a numeric keypad. The screen shows change due when the tendered amount exceeds the due amount.
3. **Confirm** records one cash payment for the amount applied to the check, with the tendered amount and change. The check closes when payments cover the total.
4. The receipt prints if the restaurant enabled automatic receipts; otherwise **Print receipt** is offered.

### 7. Payment confirmed on an external terminal

1. **Pay → Card (external terminal)** shows the amount to key into the restaurant's own physical card terminal.
2. The cashier runs the payment on that terminal. Wasla does not talk to it.
3. Only after the terminal shows approval, the cashier taps **Approved on terminal**. An optional reference field accepts the terminal slip's approval or receipt number (short, alphanumeric; never card numbers).
4. Wasla records the payment. If the terminal declined, the cashier taps **Cancel** and nothing is recorded.

A check may have several payment records (for example part cash, part card). Splitting a check by items is not in the MVP.

### 8. Receipt and kitchen printing

- **Kitchen ticket**: every **Send to kitchen** creates one ticket with the newly sent lines (product, modifiers, quantity, note), the check label, the staff member and the time. It prints on the restaurant's kitchen printer. Printing again marks the copy as a reprint.
- **Void slip**: voiding a sent line prints a slip to the kitchen printer so the kitchen stops preparing it.
- **Pre-bill**: an unpaid check summary for the guest.
- **Receipt**: a closed check's lines, totals and payment methods, with the restaurant's name and the business day's receipt number. It is marked as a non-fiscal information slip ([Fiscal documents](#unresolved-product-decisions)). Reprints say "copy".
- Printing goes through Wasla Print Bridge ([print decision](../architecture/pos-application-boundary.md#print-bridge)). A print job's state is shown honestly: queued, printing, printed (accepted by the Windows spooler) or failed. A print failure never undoes a kitchen send or a payment.

### 9. Void and cancellation

| Situation | Who | What happens |
| --- | --- | --- |
| Remove or change an unsent line | Cashier, Waiter | Line is removed; no kitchen effect |
| Void a sent line | Cashier or Waiter requests, Manager/Owner approves on the device with their PIN | Line stays on the check as voided with a reason; void slip prints in the kitchen; totals drop |
| Cancel an open check with sent lines | Manager/Owner | All sent lines voided with one reason; the table is freed |
| Reverse a recorded payment on an open check (wrong method or amount) | Manager/Owner | A reversal record cancels that payment; the original stays visible |
| Cancel a closed sale (refund) | Manager/Owner | Each payment gets a reversal record, the check becomes **Cancelled** with a reason, and a cancellation slip prints. The staff member confirms they returned the cash or refunded on the external terminal; Wasla records that confirmation and does not perform the refund |

Nothing is deleted. Every void, reversal and cancellation is audited ([Auditability](#auditability)). Reasons come from a short fixed list (wrong item, guest changed mind, kitchen problem, other with text).

### 10. Completed-sale history

- Lists closed and cancelled checks of a business day (default today), newest first, with number, label, total, payment methods and status. Filter by status and payment method; search by check or receipt number.
- Opening a check shows it **read-only**: lines, voids, kitchen tickets, payments, reversals and audit entries.
- Actions from history: **Reprint receipt** (Cashier, Manager, Owner) and **Cancel sale** (Manager, Owner).
- A day summary shows totals by payment method and the number of cancelled checks. It is not a Z report and not a fiscal report.

### 11. Shift open and close

**Not in the MVP.** A cash-drawer shift with opening float, counted cash and difference is common, but the first slice can be used without it: history and the day summary are per business day. Shifts are the first should-have after the MVP ([Should-have later](#should-have-later)).

## Roles

POS roles are owned by Wasla POS. They are **not** the Wasla Orders `UserRole` values (`Owner`, `Manager`, `Kitchen`, `Cashier`, `Viewer` in `src/Wasla.Domain/Enums/UserRole.cs`): there, Cashier and Kitchen handle platform orders on the Live Screen ([roles-and-permissions.md](roles-and-permissions.md)), which is a different job. Reusing that enum would tie POS permissions to Orders policies and migrations. Names may match; the definitions do not.

| Capability | Owner | Manager | Cashier | Waiter | Kitchen |
| --- | :-: | :-: | :-: | :-: | :-: |
| Open table, takeaway or quick-sale check; add and change unsent lines | ✓ | ✓ | ✓ | ✓ | |
| Send to kitchen; print pre-bill | ✓ | ✓ | ✓ | ✓ | |
| Record cash or external-terminal payment; close check | ✓ | ✓ | ✓ | | |
| Request a void of a sent line | ✓ | ✓ | ✓ | ✓ | |
| Approve a void; cancel an open check; reverse a payment; cancel a closed sale | ✓ | ✓ | | | |
| View history and reprint receipts | ✓ | ✓ | ✓ | | |
| Edit menu, prices, modifiers, tables | ✓ | ✓ | | | |
| Manage POS staff and PINs; pair and revoke POS devices; POS settings | ✓ | | | | |
| Use any POS screen | | | | | MVP: none |

- **Kitchen** works from printed tickets in the MVP. A kitchen display screen is later. The role exists so that the staff list is complete and a kitchen screen can be added without changing other roles.
- **Waiter** cannot take payments in the MVP. Whether some restaurants want waiters to collect payment is an [open decision](#unresolved-product-decisions).
- **Owner** in POS is the restaurant's POS administrator. How it relates to the Orders Owner account is decided in the [ADR](../architecture/pos-application-boundary.md#identity-and-sign-in).
- Server-side policies enforce this table. Hiding a button is not authorization.

## MVP boundary

### Must-have

- Paired POS devices; staff sign-in with a personal PIN on a paired device; the role table above
- Menu: categories, products with price, active and available flags, modifier groups with options and price changes; edited by Owner/Manager
- Areas and tables
- Checks: dine-in table, takeaway, quick sale; touch cart; line notes
- Send to kitchen with durable kitchen tickets
- Payment records: cash (tendered and change) and external terminal (optional reference); several payments per check
- Close on full payment; one open check per table; single-winner close across devices
- Voids, check cancellation, payment reversal and sale cancellation with Manager/Owner approval and reasons
- Kitchen ticket, void slip, pre-bill and receipt printing through Print Bridge, with honest print state
- Completed-sale history (read-only) with reprint and day summary by payment method
- Audit log for every money- or kitchen-affecting action
- TR, EN, AR, RU with Arabic right to left; TRY amounts
- Idempotent writes and duplicate-payment protection ([Non-functional requirements](#non-functional-requirements))

### Should-have later

In rough order of expected value:

1. Shifts / cash drawer: opening float, cash in/out, counted cash and difference, shift report
2. Table operations: move a check to another table, merge checks, split a check by items or guests
3. Discounts and service charge with permission and audit
4. Kitchen display screen for the Kitchen role, and routing tickets to several kitchen printers or stations
5. Remote back office in a normal browser (menu, reports) without a paired device
6. Barcode lookup for packaged products; product images
7. Daily and period sales reports, product sales ranking, export
8. Offline sale capture with later synchronization ([ADR](../architecture/pos-application-boundary.md#network-loss-and-offline))
9. Delivery orders taken by phone (*paket servis*) with customer address
10. Showing Wasla Orders platform orders and POS checks together (the Figma "one ecosystem" panel), through a read model, not a shared database
11. Menu translations for product names

### Out of scope

- Integrated card processing: no card-terminal integration, no bank POS API, no card number or CVV entry, no payment provider for restaurant sales
- Using Wasla's iyzico subscription merchant account for any restaurant sale
- Fiscal receipt devices (ÖKC / *yeni nesil yazar kasa*), e-Arşiv, e-Fatura or e-Adisyon issuance, until the [fiscal decision](#unresolved-product-decisions) is made
- Inventory, stock, recipes and purchasing
- Accounting, payroll and tax reporting
- Delivery logistics and courier management
- Loyalty, campaigns and customer accounts
- Reservations
- Multi-location management (each location stays a separate tenant, as in [tenancy.md](../architecture/tenancy.md))
- Changes to Wasla Orders, its Live Screen or its order lifecycle

### Assumptions

- A restaurant using POS has a stable internet connection during service; the MVP is online-only ([Network loss](#network-loss)).
- The restaurant has, or buys, a Windows PC for Print Bridge and at least one ESC/POS-capable receipt printer reachable from it; a kitchen printer is optional (tickets can print on the receipt printer).
- POS devices are touch screens running a current Chromium browser (Edge or Chrome) at 1024×768 or larger; Windows all-in-one terminals and tablets of 10 inches or more.
- Card payments run on the restaurant's existing bank terminal, and the restaurant keeps issuing fiscal documents the way it does today.
- Prices include VAT; the MVP shows no VAT breakdown.
- One currency, TRY, as in Wasla Orders (`OrderCurrency`).
- A pilot restaurant runs up to about 30 tables, 300 products and 5 POS devices. These are design targets, not measured limits.

### Unresolved product decisions

| Decision | Why it matters | Needed before |
| --- | --- | --- |
| **Fiscal documents.** What Turkish fiscal obligations (ÖKC, e-Arşiv, e-Adisyon) apply to a restaurant using Wasla POS, and whether Wasla must integrate with them | Not researched in WAS-34 and needs legal and tax review. Until decided, every Wasla receipt says it is not a fiscal document, and the restaurant keeps its fiscal device | Any paid pilot |
| **Owner account.** Whether the POS Owner signs in with the same email and password as Wasla Orders | The ADR keeps POS staff in the POS database for the MVP; one Wasla login needs identity to move out of the Orders tenant database ([ADR](../architecture/pos-application-boundary.md#identity-and-sign-in)) | Selling POS and Orders together |
| **Waiter payments.** Whether waiters may record payments, as a per-restaurant setting | Changes the role table and the audit trail | Pilot onboarding |
| **Offline service.** Whether a pilot restaurant accepts that POS stops when its internet is down | Decides whether offline capture moves into the MVP, which is a large change ([ADR](../architecture/pos-application-boundary.md#network-loss-and-offline)) | Pilot selection |
| **Pricing and packaging.** Whether POS is a separate plan, an add-on to Orders, or bundled | Decides how POS activation is checked against the tenant's subscription (WAS-147 owns the billing domain) | POS activation self-service |
| **Kitchen printing layout.** One kitchen printer, or routing by category to several stations | Several stations need product-to-printer routing in the menu | First pilot with more than one kitchen station |
| **Receipt numbering.** Per business day or continuous | Affects history search and any later fiscal integration | First slice (the slice uses per-day numbers and can change before production) |

## Payment boundary

Wasla POS **records** restaurant sale payments. It never processes, authorizes, captures or refunds them.

| | Wasla subscription billing | Restaurant sale payments (POS) |
| --- | --- | --- |
| Who pays whom | The restaurant pays Wasla for the SaaS | The restaurant's guest pays the restaurant |
| Money movement | iyzico, through Wasla's merchant account (WAS-145 to WAS-154; not implemented yet; signup uses a Development simulator, see [tenancy.md](../architecture/tenancy.md#signup-and-checkout-access)) | Cash, or the restaurant's own bank card terminal. Wasla moves no money |
| Where it is stored | CentralDb (WAS-147) | The POS tenant database ([ADR](../architecture/pos-application-boundary.md#data-ownership)) |
| Card data | Never stored; iyzico's hosted form | Never entered or stored. No card number, expiry or CVV field exists |

Rules:

- Payment methods in the MVP: **Cash** and **External card terminal**. Adding a method is a product decision, not configuration.
- An external-terminal record means only "the cashier confirmed the terminal approved this amount". The optional reference is free text up to 32 characters, letters and digits only, and is validated so that a run of 13–19 digits that passes the Luhn check (a possible card number) is refused. Approval codes and 12-digit retrieval reference numbers stay allowed.
- Wasla's iyzico account, keys and code are never used by POS. POS has no payment-provider dependency.
- **Reversals and refunds** are records too. A reversal points to the payment it cancels, has an amount equal to that payment, a reason, the approving Manager/Owner and the confirming staff member. A recorded payment is never edited or deleted. A refund on the terminal is the cashier's action on the terminal; Wasla cannot know whether it happened and records only the confirmation.
- A check closes only when the sum of its payments minus reversals equals its total. Cash may exceed it; the difference is change and is recorded.

## UX requirements

The POS uses the Wasla visual direction (warm operational minimalism, foundation tokens in [../frontend/design-system.md](../frontend/design-system.md)) and the repository's frontend stack: ASP.NET Core MVC/Razor, Bootstrap 5 and vanilla JavaScript ([../frontend/architecture.md](../frontend/architecture.md)). It is a separate operational surface from the Orders tenant shell.

**Layout (1024×768 and larger, landscape):**

- Three zones: categories (top or start edge), product grid (center), cart and actions (end edge, about one third of the width). In Arabic the zones mirror.
- Dense, no decorative whitespace; text and prices legible at arm's length (product names at least 16 px, totals at least 24 px).
- The tables screen is a grid of table tiles per area; tile colour and a text label both show free or occupied (colour is never the only signal).
- Portrait tablets (768 px wide) stack the cart under the products with a cart summary bar; phone widths are not a target for the MVP.

**Touch targets and taps:**

- Every interactive control is at least 48×48 CSS px with at least 8 px between targets; product tiles at least 96×72 px. (WCAG 2.2 requires 24 px; 48 px is the operational target for wet or gloved hands.)
- Add a product without modifiers: 1 tap. Quick sale paid with exact cash: product taps + **Pay** + **Cash exact** = 2 taps after the products. Card on terminal: **Pay** + **Card** + **Approved on terminal** = 3 taps. Send to kitchen: 1 tap. Open an occupied table's check: 1 tap from the tables screen.
- No hover-only controls, no long-press-only actions, no drag-and-drop requirement.
- Destructive or money actions (void, cancel, reversal) need a confirmation step; adding lines never does.

**Keyboard and scanner readiness:**

- Every action is keyboard operable with a visible focus ring; the product search field is reachable with one key (`/` or F2), Enter adds the highlighted product, and amount entry accepts the numeric keypad.
- The sale screen must not swallow fast keyboard input, so that a keyboard-wedge barcode scanner can be added later without a redesign. Barcode lookup itself is not in the MVP.

**Localization:**

- TR (default), EN, AR, RU for every POS string, resource keys under a POS prefix, no hardcoded copy in Razor or JavaScript, JavaScript messages passed from Razor, as in Wasla Orders ([20-localization-ui.mdc](../../.cursor/rules/20-localization-ui.mdc)).
- Arabic is right to left: mirrored zones, logical CSS properties, amounts and check numbers kept left to right inside right-to-left text.
- Amounts in TRY with `₺` via the culture's number format, never the culture's own currency; Gregorian dates in every culture, as Orders does (WAS-44 for amounts, WAS-130 for dates).
- Product, category and modifier names are restaurant data and are not translated.

**Accessibility:** WCAG 2.2 AA for the POS screens: AA contrast in light and dark themes, labelled controls, status changes (line added, payment recorded, print failed, connection lost) announced through live regions, no information by colour alone, `prefers-reduced-motion` respected, 200 % zoom without loss of function on the back-office screens.

## Non-functional requirements

These are targets for design and tests. None is measured yet.

### Responsiveness

- Tap feedback within 100 ms; adding or changing a draft line is local and never waits for the server.
- Server writes (send to kitchen, save, pay, close) answer within 500 ms at the 95th percentile on a normal restaurant connection; the UI shows progress after 300 ms and never accepts a second identical action while one is in flight.
- The sale screen loads the menu once per sign-in and refreshes it when the menu changes; it does not request the menu per product tap.

### Network loss

The MVP is online-only and says so on screen:

- A connection indicator is always visible. When the server cannot be reached, draft lines stay editable on the device, and **Send to kitchen**, **Pay** and **Close** are disabled with a localized message.
- Draft lines that were never sent live only on that device. Leaving the check with unsent lines asks for confirmation. They are not promised to survive a browser crash.
- A request that timed out is retried with the same idempotency key, so a send or payment that reached the server is not duplicated ([below](#idempotency-and-duplicate-protection)).
- Kitchen tickets and receipts already queued print when the Print Bridge PC reconnects; Print Bridge's own retry rules apply.

### Idempotency and duplicate protection

- The device creates the ids of checks, lines and payments (GUIDs) and sends an idempotency key with every write. A repeated request with the same key returns the first result and changes nothing.
- Recording a payment and closing a check are compare-and-set on the check's version: two devices paying the same table cannot both succeed. The loser sees the check's current state, as in Wasla Orders' single-winner order actions (WAS-104).
- A payment that would make the recorded total exceed the check total is refused, except cash, whose excess is recorded as change.
- A closed or cancelled check accepts no lines, payments or sends.
- A kitchen send sends only lines not yet sent; repeating it creates no second ticket for the same lines.

### Auditability

- An append-only audit record for: sign-in success and failure, device pairing and revocation, send to kitchen, void request and approval, check cancellation, payment, payment reversal, sale cancellation, receipt reprint, menu price change, staff and PIN changes.
- Each record has the UTC time, device, staff member (and approver when different), check, action, amounts before and after, and reason. No PINs, tokens or secrets.
- Audit records are never updated or deleted by the application.

### Security and tenant isolation

- Tenant context comes from the server-side resolution of the request host and the device credential, never from a request body ([tenancy.md](../architecture/tenancy.md)). A device paired to one restaurant cannot act for another.
- PINs are hashed, rate limited per device and staff member, and locked after repeated failures; a PIN signs in only on a paired device.
- POS follows the repository's secrets and logging rules ([AGENTS.md](../../AGENTS.md#8-secrets-credentials-and-logging)).

## Related documents

- [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md): repository, application, database, identity and print decision
- [pos-first-slice.md](pos-first-slice.md): first vertical slice and backlog
- [product-boundaries.md](product-boundaries.md)
- [roles-and-permissions.md](roles-and-permissions.md): Wasla Orders roles (not POS roles)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
